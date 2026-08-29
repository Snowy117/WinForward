using System.Runtime.InteropServices;
using WinForward.Core;

namespace WinForward.Runtime.TcpRedirect;

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
    internal TcpRedirectAssociation(FlowKey originalKey, Endpoint originalDestination, AdapterContext originAdapter, nint originAdapterHandle, Endpoint translatedListenerTuple, IPAddressValue? forwardLocalAddress, long generation, DateTimeOffset now)
    {
        OriginalKey = originalKey;
        OriginalDestination = originalDestination;
        OriginAdapter = originAdapter;
        OriginAdapterHandle = originAdapterHandle;
        TranslatedListenerTuple = translatedListenerTuple;
        ForwardLocalAddress = forwardLocalAddress;
        if (forwardLocalAddress is null)
        {
            // Host shape: the rewritten SYN arrives from server-address:client-port at the client's
            // own address, so the listener peer and the reverse source are keyed on those.
            ReverseSourceEndpoint = Endpoint.From(originalKey.Local.Address, translatedListenerTuple.Port);
            ReverseDestinationEndpoint = Endpoint.From(originalKey.Remote.Address, originalKey.Local.Port);
        }
        else
        {
            // Forwarded DNAT shape: the rewritten SYN keeps the client's tuple and only the
            // destination moves to the adapter-local address, so the listener peer is the client
            // itself and the reverse source is the adapter-local address on the listener port.
            ReverseSourceEndpoint = Endpoint.From(forwardLocalAddress.Value, translatedListenerTuple.Port);
            ReverseDestinationEndpoint = Endpoint.From(originalKey.Local.Address, originalKey.Local.Port);
        }
        AcceptedPeerEndpoint = ReverseDestinationEndpoint;
        Generation = generation;
        LastActivityUtc = now;
    }

    public FlowKey OriginalKey { get; }
    public Endpoint OriginalDestination { get; }
    public AdapterContext OriginAdapter { get; }
    public nint OriginAdapterHandle { get; }
    public Endpoint TranslatedListenerTuple { get; }
    /// <summary>
    /// The adapter-local DNAT destination for forwarded flows. Stored raw (hot-path contract 1)
    /// because the per-packet rewrite consumes it directly; null selects the host IP-swap shape.
    /// </summary>
    public IPAddressValue? ForwardLocalAddress { get; }
    public Endpoint ReverseSourceEndpoint { get; }
    public Endpoint ReverseDestinationEndpoint { get; }
    public Endpoint AcceptedPeerEndpoint { get; }
    public long Generation { get; }
    public RelayPhase Phase { get; internal set; }
    public DateTimeOffset LastActivityUtc { get; private set; }

    /// <summary>
    /// The client ISN observed on the original SYN and a bounded copy of that frame, recorded at
    /// redirect setup so a relay setup failure can be surfaced to the client as a protocol-correct
    /// RST crafted from real sequence numbers instead of a silent hang.
    /// </summary>
    public uint? ClientInitialSeq { get; internal set; }
    public byte[]? OriginalSynFrameCopy { get; internal set; }

    /// <summary>The listener-side ISN, observed when the reverse SYN-ACK passed the reverse hook.</summary>
    public uint? ServerInitialSeq { get; internal set; }

    /// <summary>
    /// The highest client-side sequence advancement observed on the forward leg
    /// (seq + payload length, SYN/FIN each counting one), or null before any forward frame
    /// passed the redirect. A reset acknowledging this value stays in the client's window even
    /// after it already sent request data; null degrades to <see cref="ClientInitialSeq"/> + 1.
    /// </summary>
    public uint? ClientNextSeq { get { lock (_sequenceGate) return _clientNextSeq; } }

    /// <summary>The server-side counterpart of <see cref="ClientNextSeq"/>, tracked on the reverse leg.</summary>
    public uint? ServerNextSeq { get { lock (_sequenceGate) return _serverNextSeq; } }

    private readonly Lock _sequenceGate = new();
    private uint? _clientNextSeq;
    private uint? _serverNextSeq;

    /// <summary>
    /// Advances the forward-leg tracker to <paramref name="sequenceNext"/> when it lies ahead of
    /// the tracked value (TCP wraparound-aware), so retransmissions and pure ACKs never move it
    /// backwards. Guarded by its own lock: the trackers are written on the data path and read on
    /// the teardown path, neither of which holds the table gate.
    /// </summary>
    internal void ObserveClientSequence(uint sequenceNext)
    {
        lock (_sequenceGate)
        {
            if (_clientNextSeq is not uint current || IsSequenceAhead(sequenceNext, current)) _clientNextSeq = sequenceNext;
        }
    }

    /// <summary>The reverse-leg counterpart of <see cref="ObserveClientSequence"/>.</summary>
    internal void ObserveServerSequence(uint sequenceNext)
    {
        lock (_sequenceGate)
        {
            if (_serverNextSeq is not uint current || IsSequenceAhead(sequenceNext, current)) _serverNextSeq = sequenceNext;
        }
    }

    /// <summary>RFC 793-style serial-number comparison: candidate is ahead when the wrapped
    /// difference is positive and non-zero (strictly forward within the comparison window).</summary>
    private static bool IsSequenceAhead(uint candidate, uint current) => candidate != current && (int)(candidate - current) > 0;

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

    public TcpRedirectTable(int? capacity = null)
    {
        if (capacity is < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        _capacity = capacity ?? 16_384;
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
    /// <paramref name="forwardLocalAddress"/> is the adapter-local redirect destination address for
    /// forwarded flows (DNAT shape); null selects the host IP-swap shape.
    /// </summary>
    public bool TryClaim(FlowKey originalKey, Endpoint originalDestination, AdapterContext originAdapter, nint originAdapterHandle, Endpoint translatedTuple, IPAddressValue? forwardLocalAddress, DateTimeOffset now, out TcpRedirectAssociation? association)
    {
        lock (_gate)
        {
            if (_byOriginal.TryGetValue(originalKey, out var existing))
            {
                existing.Touch(now);
                association = existing;
                return true;
            }

            var created = new TcpRedirectAssociation(originalKey, originalDestination, originAdapter, originAdapterHandle, translatedTuple, forwardLocalAddress, ++_nextGeneration, now);
            if (_byTranslatedListener.ContainsKey(translatedTuple) || _byReverse.ContainsKey(new ReverseRedirectTuple(created.ReverseSourceEndpoint, created.ReverseDestinationEndpoint)))
            {
                association = null;
                return false;
            }

            if (_byOriginal.Count >= _capacity)
            {
                association = null;
                return false;
            }

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
    /// Resolves a reverse redirect frame by its complete pre-rewrite wire tuple. The tuple shape
    /// follows the association origin: a host flow's listener replies from
    /// client-address:proxy-port to server-address:original-client-port, while a forwarded flow's
    /// listener replies from adapter-local-address:proxy-port to client-address:original-client-port.
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

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ReverseRedirectTuple(Endpoint Source, Endpoint Destination);
}
