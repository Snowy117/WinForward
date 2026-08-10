using WinForward.Core;

namespace WinForward.Runtime;

/// <summary>
/// The lifecycle phase of a TCP redirect flow. A flow starts <see cref="Redirecting"/> when its SYN
/// has been rewritten toward the local listener, advances to <see cref="Relaying"/> once the local
/// connection is accepted and the upstream relay is established, and enters <see cref="Closing"/>
/// during teardown so concurrent observations do not re-arm setup.
/// </summary>
public enum RelayPhase
{
    Redirecting,
    Relaying,
    Closing,
}

public sealed class TcpRedirectAssociation
{
    internal TcpRedirectAssociation(FlowKey originalKey, Endpoint originalDestination, AdapterContext originAdapter, nint originAdapterHandle, Endpoint translatedListenerTuple, long generation, DateTimeOffset now)
    {
        OriginalKey = originalKey;
        OriginalDestination = originalDestination;
        OriginAdapter = originAdapter;
        OriginAdapterHandle = originAdapterHandle;
        TranslatedListenerTuple = translatedListenerTuple;
        ReverseSourceEndpoint = Endpoint.From(originalKey.Local.Address, translatedListenerTuple.Port);
        ReverseDestinationEndpoint = Endpoint.From(originalKey.Remote.Address, originalKey.Local.Port);
        AcceptedPeerEndpoint = ReverseDestinationEndpoint;
        Generation = generation;
        LastActivityUtc = now;
    }

    public FlowKey OriginalKey { get; }
    public Endpoint OriginalDestination { get; }
    public AdapterContext OriginAdapter { get; }
    public nint OriginAdapterHandle { get; }
    public Endpoint TranslatedListenerTuple { get; }
    public Endpoint ReverseSourceEndpoint { get; }
    public Endpoint ReverseDestinationEndpoint { get; }
    public Endpoint AcceptedPeerEndpoint { get; }
    public long Generation { get; }
    public RelayPhase Phase { get; internal set; }
    public DateTimeOffset LastActivityUtc { get; private set; }

    public void Touch(DateTimeOffset now) => LastActivityUtc = now;
}

/// <summary>
/// The exactly-once ownership table for TCP redirect flows, mirroring <see cref="UdpAssociationTable"/>.
/// An original flow key is claimed once; a translated listener tuple may belong to only one original
/// flow, so reverse packets route deterministically. Capacity is bounded; a full table fails closed.
/// Thread-safe via a single gate lock, matching the reference UDP table.
/// </summary>
public sealed class TcpRedirectTable
{
    private readonly Dictionary<FlowKey, TcpRedirectAssociation> _byOriginal = [];
    private readonly Dictionary<Endpoint, TcpRedirectAssociation> _byTranslatedListener = [];
    private readonly Dictionary<ReverseRedirectTuple, TcpRedirectAssociation> _byReverse = [];
    private readonly Lock _gate = new();
    private readonly int _capacity;
    private long _nextGeneration;

    public TcpRedirectTable(int capacity = 16_384)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate) return _byOriginal.Count;
        }
    }

    /// <summary>
    /// Claims an association for <paramref name="originalKey"/> exactly once. If the key already
    /// exists the existing association is touched and returned. If <paramref name="translatedTuple"/>
    /// already belongs to a different original key, the claim is rejected (fail-closed) so reverse
    /// packets never route nondeterministically. A full table is rejected the same way.
    /// </summary>
    public bool TryClaim(FlowKey originalKey, Endpoint originalDestination, AdapterContext originAdapter, nint originAdapterHandle, Endpoint translatedTuple, DateTimeOffset now, out TcpRedirectAssociation? association)
    {
        lock (_gate)
        {
            if (_byOriginal.TryGetValue(originalKey, out var existing))
            {
                existing.Touch(now);
                association = existing;
                return true;
            }

            var reverse = new ReverseRedirectTuple(
                Endpoint.From(originalKey.Local.Address, translatedTuple.Port),
                Endpoint.From(originalKey.Remote.Address, originalKey.Local.Port));
            if (_byTranslatedListener.ContainsKey(translatedTuple) || _byReverse.ContainsKey(reverse))
            {
                association = null;
                return false;
            }

            if (_byOriginal.Count >= _capacity)
            {
                association = null;
                return false;
            }

            var created = new TcpRedirectAssociation(originalKey, originalDestination, originAdapter, originAdapterHandle, translatedTuple, ++_nextGeneration, now);
            _byOriginal.Add(originalKey, created);
            _byTranslatedListener.Add(translatedTuple, created);
            _byReverse.Add(new ReverseRedirectTuple(created.ReverseSourceEndpoint, created.ReverseDestinationEndpoint), created);
            association = created;
            return true;
        }
    }

    public bool TryResolveByTranslated(Endpoint translatedTuple, DateTimeOffset now, out TcpRedirectAssociation? association) =>
        TryFind(_byTranslatedListener, translatedTuple, now, out association);

    /// <summary>
    /// Resolves a reverse redirect frame by its complete pre-rewrite wire tuple. The wildcard
    /// listener replies from the route-selected client address, so the source is
    /// client-address:proxy-port and the destination is server-address:original-client-port.
    /// </summary>
    public bool TryResolveByReverse(Endpoint local, Endpoint remote, DateTimeOffset now, out TcpRedirectAssociation? association)
    {
        lock (_gate)
        {
            if (_byReverse.TryGetValue(new ReverseRedirectTuple(local, remote), out association))
            {
                association.Touch(now);
                return true;
            }
            association = null;
            return false;
        }
    }

    public bool IsReverseCandidate(Endpoint local, Endpoint remote)
    {
        lock (_gate) return _byReverse.ContainsKey(new ReverseRedirectTuple(local, remote));
    }

    public bool TryResolveByOriginal(FlowKey originalKey, DateTimeOffset now, out TcpRedirectAssociation? association) =>
        TryFind(_byOriginal, originalKey, now, out association);

    /// <summary>
    /// Removes a specific association from both indexes. Used by the coordinator's fail-closed
    /// teardown when a listener, rewrite, or relay setup fails so the alias is released for reuse
    /// and no half-claimed flow lingers.
    /// </summary>
    public bool TryRemove(TcpRedirectAssociation association)
    {
        lock (_gate)
        {
            if (!_byOriginal.TryGetValue(association.OriginalKey, out var current) || !ReferenceEquals(current, association)) return false;
            _byOriginal.Remove(association.OriginalKey);
            _byTranslatedListener.Remove(association.TranslatedListenerTuple);
            _byReverse.Remove(new ReverseRedirectTuple(association.ReverseSourceEndpoint, association.ReverseDestinationEndpoint));
            return true;
        }
    }

    public int RemoveExpired(DateTimeOffset now, TimeSpan idleTimeout)
    {
        lock (_gate)
        {
            var expired = _byOriginal.Values.Where(value => now - value.LastActivityUtc >= idleTimeout).Distinct().ToArray();
            foreach (var association in expired)
            {
                _byOriginal.Remove(association.OriginalKey);
                _byTranslatedListener.Remove(association.TranslatedListenerTuple);
                _byReverse.Remove(new ReverseRedirectTuple(association.ReverseSourceEndpoint, association.ReverseDestinationEndpoint));
            }
            return expired.Length;
        }
    }

    public TcpRedirectAssociation[] Snapshot()
    {
        lock (_gate) return _byOriginal.Values.ToArray();
    }

    private bool TryFind<TKey>(Dictionary<TKey, TcpRedirectAssociation> table, TKey key, DateTimeOffset now, out TcpRedirectAssociation? association) where TKey : notnull
    {
        lock (_gate)
        {
            if (table.TryGetValue(key, out association))
            {
                association.Touch(now);
                return true;
            }
            association = null;
            return false;
        }
    }

    private readonly record struct ReverseRedirectTuple(Endpoint Source, Endpoint Destination);
}
