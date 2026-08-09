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
    internal TcpRedirectAssociation(FlowKey originalKey, Endpoint originalDestination, AdapterContext originAdapter, Endpoint translatedListenerTuple, long generation, DateTimeOffset now)
    {
        OriginalKey = originalKey;
        OriginalDestination = originalDestination;
        OriginAdapter = originAdapter;
        TranslatedListenerTuple = translatedListenerTuple;
        Generation = generation;
        LastActivityUtc = now;
    }

    public FlowKey OriginalKey { get; }
    public Endpoint OriginalDestination { get; }
    public AdapterContext OriginAdapter { get; }
    public Endpoint TranslatedListenerTuple { get; }
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
    private readonly Dictionary<Endpoint, TcpRedirectAssociation> _byTranslated = [];
    private readonly Dictionary<ushort, TcpRedirectAssociation> _byProxyPort = [];
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
    public bool TryClaim(FlowKey originalKey, Endpoint originalDestination, AdapterContext originAdapter, Endpoint translatedTuple, DateTimeOffset now, out TcpRedirectAssociation? association)
    {
        lock (_gate)
        {
            if (_byOriginal.TryGetValue(originalKey, out var existing))
            {
                existing.Touch(now);
                association = existing;
                return true;
            }

            if (_byTranslated.ContainsKey(translatedTuple))
            {
                // The translated listener tuple already belongs to another logical flow. Sharing it
                // would make reverse packets impossible to route deterministically.
                association = null;
                return false;
            }

            if (_byOriginal.Count >= _capacity)
            {
                association = null;
                return false;
            }

            var created = new TcpRedirectAssociation(originalKey, originalDestination, originAdapter, translatedTuple, ++_nextGeneration, now);
            _byOriginal.Add(originalKey, created);
            _byTranslated.Add(translatedTuple, created);
            _byProxyPort.Add(translatedTuple.Port, created);
            association = created;
            return true;
        }
    }

    public bool TryResolveByTranslated(Endpoint translatedTuple, DateTimeOffset now, out TcpRedirectAssociation? association) =>
        TryFind(_byTranslated, translatedTuple, now, out association);

    /// <summary>
    /// Resolves an association by proxy port. A reverse packet from the local proxy listener has a
    /// source port equal to the proxy port but a source address equal to the client's own IP (the
    /// proxy connects to the client using the client's local address), so exact tuple matching
    /// fails; the port is the stable discriminator. Each listener binds a unique ephemeral port, so
    /// the port index keeps this O(1) on the per-packet reverse path.
    /// </summary>
    public bool TryResolveByProxyPort(ushort proxyPort, DateTimeOffset now, out TcpRedirectAssociation? association)
    {
        lock (_gate)
        {
            if (_byProxyPort.TryGetValue(proxyPort, out association))
            {
                association.Touch(now);
                return true;
            }
            association = null;
            return false;
        }
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
            _byTranslated.Remove(association.TranslatedListenerTuple);
            _byProxyPort.Remove(association.TranslatedListenerTuple.Port);
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
                _byTranslated.Remove(association.TranslatedListenerTuple);
                _byProxyPort.Remove(association.TranslatedListenerTuple.Port);
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
}
