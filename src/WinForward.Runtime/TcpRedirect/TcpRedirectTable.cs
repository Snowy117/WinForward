using System.Numerics;
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
    internal TcpRedirectAssociation(FlowKey originalKey, Endpoint originalDestination, nint originAdapterHandle, Endpoint translatedListenerTuple, IPAddressValue? forwardLocalAddress, long generation, DateTimeOffset now)
    {
        OriginalKey = originalKey;
        OriginalDestination = originalDestination;
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
        _activityBucket = ActivityBucket.FromUtc(now);
    }

    public FlowKey OriginalKey { get; }
    public Endpoint OriginalDestination { get; }
    public nint OriginAdapterHandle { get; }
    public Endpoint TranslatedListenerTuple { get; }
    /// <summary>
    /// The adapter-local DNAT destination for forwarded flows. Stored raw because the per-packet
    /// rewrite consumes it directly; null selects the host IP-swap shape.
    /// </summary>
    public IPAddressValue? ForwardLocalAddress { get; }
    public Endpoint ReverseSourceEndpoint { get; }
    public Endpoint ReverseDestinationEndpoint { get; }
    public Endpoint AcceptedPeerEndpoint { get; }
    public long Generation { get; }
    public RelayPhase Phase { get; internal set; }

    /// <summary>
    /// The association's idle stamp, derived from <see cref="ActivityBucket"/>: a warm resolve writes
    /// one integer and never reads a clock, and the sweeps compare buckets (never this property, whose
    /// value is quantised down to its bucket and can be up to 500 ms older than the true instant).
    /// </summary>
    public DateTimeOffset LastActivityUtc => ActivityBucket.ToUtc(Volatile.Read(ref _activityBucket));

    /// <summary>The internal integer bucket the sweeps compare; the never-early operator reads this.</summary>
    internal long BucketForDiagnostics => Volatile.Read(ref _activityBucket);

    private long _activityBucket;

    /// <summary>
    /// The client ISN observed on the original SYN and a bounded copy of that frame, recorded at
    /// redirect setup so a relay setup failure can be surfaced to the client as a protocol-correct
    /// RST crafted from real sequence numbers instead of a silent hang. The copy is rented from
    /// the syn-copy pool and released at table removal.
    /// </summary>
    public uint? ClientInitialSeq { get; internal set; }

    /// <summary>Whether the bounded original-SYN template is present (a lease is recorded).</summary>
    public bool HasOriginalSynTemplate => _originalSynTemplate is not null;

    /// <summary>
    /// A read-only view of the bounded original-SYN template, empty when none was recorded. The
    /// span is valid until the association is removed from the redirect table.
    /// </summary>
    public ReadOnlySpan<byte> OriginalSynTemplate => _originalSynTemplate is { } template ? template.Span[.._originalSynTemplateLength] : default;

    private NativeLease? _originalSynTemplate;
    private int _originalSynTemplateLength;

    internal void SetOriginalSynTemplate(NativeLease template, int length)
    {
        _originalSynTemplate = template;
        _originalSynTemplateLength = length;
    }

    /// <summary>
    /// Releases the recorded template lease. Idempotent: a repeat call is a no-op (the field is
    /// cleared), so the table-removal and expiry paths can both call it safely.
    /// </summary>
    internal void ReleaseOriginalSynTemplate()
    {
        _originalSynTemplate?.Dispose();
        _originalSynTemplate = null;
        _originalSynTemplateLength = 0;
    }

    /// <summary>The listener-side ISN, observed when the reverse SYN-ACK passed the reverse hook.</summary>
    public uint? ServerInitialSeq { get; internal set; }

    /// <summary>
    /// The highest client-side sequence advancement observed on the forward leg
    /// (seq + payload length, SYN/FIN each counting one), or null before any forward frame
    /// passed the redirect. A reset acknowledging this value stays in the client's window even
    /// after it already sent request data; null degrades to <see cref="ClientInitialSeq"/> + 1.
    /// </summary>
    public uint? ClientNextSeq
    {
        get
        {
            var value = Volatile.Read(ref _clientNextSeq);
            return value < 0 ? null : (uint)value;
        }
    }

    /// <summary>The server-side counterpart of <see cref="ClientNextSeq"/>, tracked on the reverse leg.</summary>
    public uint? ServerNextSeq
    {
        get
        {
            var value = Volatile.Read(ref _serverNextSeq);
            return value < 0 ? null : (uint)value;
        }
    }

    /// <summary>
    /// The unobserved sentinel. The trackers are <see cref="long"/> so <c>0xFFFFFFFF</c> stays a
    /// legal tracked sequence and this value is unreachable from real data, and so a reader sees one
    /// aligned 64-bit word — never a torn <c>uint?</c> pair.
    /// </summary>
    private const long Unobserved = -1;

    private long _clientNextSeq = Unobserved;
    private long _serverNextSeq = Unobserved;

    /// <summary>
    /// Advances the forward-leg tracker to <paramref name="sequenceNext"/> when it lies ahead of the
    /// tracked value (TCP wraparound-aware), so retransmissions and pure ACKs never move it
    /// backwards. The CAS-max loop is the lock's exact predicate: an unobserved tracker always
    /// writes ("first observation wins"), otherwise racers serialise on the compare-exchange and the
    /// loser re-reads and re-tests.
    /// </summary>
    internal void ObserveClientSequence(uint sequenceNext)
    {
        while (true)
        {
            var current = Volatile.Read(ref _clientNextSeq);
            if (current >= 0 && !IsSequenceAhead(sequenceNext, (uint)current)) return;
            if (Interlocked.CompareExchange(ref _clientNextSeq, sequenceNext, current) == current) return;
        }
    }

    /// <summary>The reverse-leg counterpart of <see cref="ObserveClientSequence"/>.</summary>
    internal void ObserveServerSequence(uint sequenceNext)
    {
        while (true)
        {
            var current = Volatile.Read(ref _serverNextSeq);
            if (current >= 0 && !IsSequenceAhead(sequenceNext, (uint)current)) return;
            if (Interlocked.CompareExchange(ref _serverNextSeq, sequenceNext, current) == current) return;
        }
    }

    /// <summary>RFC 793-style serial-number comparison: candidate is ahead when the wrapped
    /// difference is positive and non-zero (strictly forward within the comparison window).</summary>
    private static bool IsSequenceAhead(uint candidate, uint current) => candidate != current && (int)(candidate - current) > 0;

    public void Touch(DateTimeOffset now) => Volatile.Write(ref _activityBucket, ActivityBucket.FromUtc(now));
}

/// <summary>
/// The exactly-once ownership table for TCP redirect flows, mirroring <see cref="UdpProxy.UdpAssociationTable"/>.
/// An original flow key is claimed once; a translated listener tuple may belong to only one original
/// flow, so reverse packets route deterministically. Capacity is bounded; a full table fails closed.
/// Thread-safe via a single gate lock, matching the reference UDP table.
/// </summary>
public sealed class TcpRedirectTable
{
    private readonly Dictionary<FlowKey, TcpRedirectAssociation> _byOriginal = [];
    private readonly Dictionary<Endpoint, TcpRedirectAssociation> _byTranslatedListener = [];
    private readonly Dictionary<ReverseRedirectTuple, TcpRedirectAssociation> _byReverse = [];
    private readonly Dictionary<AddressPair, TcpRedirectAssociation> _byAddressPair = [];
    private readonly Lock _gate = new();

    // Sweep-level single flight, outer to _gate and never taken while _gate is held. The sweep collects
    // candidates into a reused scratch (so a tick allocates nothing) and then removes them one short hold
    // at a time, which is why _gate no longer covers the scratch's lifetime.
    private readonly Lock _sweepGate = new();
    private readonly List<TcpRedirectAssociation> _expiredScratch = [];
    private readonly int _capacity;
    // Reference count per listener port. A reverse candidate's source port is always a live
    // listener port, so a zero count proves the packet cannot match the reverse index. Mutations
    // run under _gate with Interlocked ops; consults are lock-free Volatile reads safe for the
    // per-packet warm path.
    private readonly int[] _candidatePorts = new int[65_536];
    private long _nextGeneration;
    private int _gateEntryCount;
    private int _reverseProbeCount;

    // Direct-mapped warm caches over the two per-packet indexes, allocated once in the constructor and
    // never grown. The validated field pairs are get-only, so a served entry is always the association
    // the slot named and never a different one: an unpopulated slot or a collision fails validation and
    // falls back to the gated authority. It can be stale, though — a reader that loaded the reference
    // before a removal's guarded clear still returns that association, because the index delete and the
    // cache clear are not one atomic step with an in-flight probe — and the caller then handles one
    // packet on an association that was retired a moment later. `RemoveUnderGate` is the single
    // invalidation point every removal path shares.
    private readonly TcpRedirectAssociation?[] _warmReverse;
    private readonly TcpRedirectAssociation?[] _warmOriginal;

    public TcpRedirectTable(int? capacity = null)
    {
        if (capacity is < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        _capacity = capacity ?? 16_384;
        var slots = BitOperations.RoundUpToPowerOf2((uint)Math.Clamp((long)_capacity * 8, 1_024, 16_384));
        _warmReverse = new TcpRedirectAssociation?[slots];
        _warmOriginal = new TcpRedirectAssociation?[slots];
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                NoteGateEntry();
                return _byOriginal.Count;
            }
        }
    }

    /// <summary>
    /// The gate-entry diagnostics sink, or null in production (diagnostics only). It fires immediately
    /// after every <see cref="_gate"/> acquisition, so a test can park a holder inside the gate or
    /// count the entries a driven packet path takes.
    /// </summary>
    internal Action? GateHoldProbe { get; set; }

    /// <summary>
    /// The number of <see cref="_gate"/> acquisitions recorded since the process started, counted only
    /// while <see cref="GateHoldProbe"/> is attached (diagnostics only, never on the product path).
    /// </summary>
    internal int GateEntryCountForDiagnostics => Volatile.Read(ref _gateEntryCount);

    /// <summary>
    /// The number of <see cref="TryResolveByReverse"/> probes recorded since the process started, counted
    /// under the same attachment as <see cref="GateEntryCountForDiagnostics"/> (diagnostics only): the
    /// fold's contract is a probe count, and a warm reverse packet must take exactly one.
    /// </summary>
    internal int ReverseProbeCountForDiagnostics => Volatile.Read(ref _reverseProbeCount);

    private void NoteGateEntry()
    {
        var probe = GateHoldProbe;
        if (probe is null) return;
        Interlocked.Increment(ref _gateEntryCount);
        probe();
    }

    /// <summary>
    /// Claims an association for <paramref name="originalKey"/> exactly once. If the key already
    /// exists the existing association is touched and returned. If <paramref name="translatedTuple"/>
    /// already belongs to a different original key, the claim is rejected (fail-closed) so reverse
    /// packets never route nondeterministically. A full table is rejected the same way.
    /// <paramref name="forwardLocalAddress"/> is the adapter-local redirect destination address for
    /// forwarded flows (DNAT shape); null selects the host IP-swap shape.
    /// </summary>
    public bool TryClaim(FlowKey originalKey, Endpoint originalDestination, nint originAdapterHandle, Endpoint translatedTuple, IPAddressValue? forwardLocalAddress, DateTimeOffset now, out TcpRedirectAssociation? association)
    {
        lock (_gate)
        {
            NoteGateEntry();
            if (_byOriginal.TryGetValue(originalKey, out var existing))
            {
                existing.Touch(now);
                association = existing;
                return true;
            }

            var created = new TcpRedirectAssociation(originalKey, originalDestination, originAdapterHandle, translatedTuple, forwardLocalAddress, ++_nextGeneration, now);
            // ReSharper disable once DuplicatedSequentialIfBodies // Fail-closed claim gate: the doc contract names two distinct rejection reasons (tuple already owned by another key / table full); merging them into one 3-clause condition would collapse that distinction on the claim path.
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
            // Last writer wins when several associations share an address pair: a fragment
            // carries no ports, so attribution is inherently ambiguous there and the newest
            // claim is the best guess.
            _byAddressPair[NormalizeAddressPair(originalKey.Local.Address, originalKey.Remote.Address)] = created;
            Volatile.Write(ref _warmReverse[ReverseSlot(created.ReverseSourceEndpoint, created.ReverseDestinationEndpoint)], created);
            Volatile.Write(ref _warmOriginal[OriginalSlot(originalKey)], created);
            // The count rises inside the claim gate before the caller can rewrite and inject the
            // SYN, so the listener's first reverse candidate (the SYN-ACK) can never arrive before
            // its port is observable on the warm path.
            Interlocked.Increment(ref _candidatePorts[translatedTuple.Port]);
            association = created;
            return true;
        }
    }

    /// <summary>
    /// Resolves a reverse redirect frame by its complete pre-rewrite wire tuple. The tuple shape
    /// follows the association origin: a host flow's listener replies from
    /// client-address:proxy-port to server-address:original-client-port, while a forwarded flow's
    /// listener replies from adapter-local-address:proxy-port to client-address:original-client-port.
    /// A validated cache hit takes no gate; a miss falls back to the gated authority, so a collision,
    /// an unpopulated slot or a stale entry can only cost the lock — never a wrong answer.
    /// </summary>
    public bool TryResolveByReverse(Endpoint local, Endpoint remote, DateTimeOffset now, out TcpRedirectAssociation? association)
    {
        if (GateHoldProbe is not null) Interlocked.Increment(ref _reverseProbeCount);
        var slot = ReverseSlot(local, remote);
        var cached = Volatile.Read(ref _warmReverse[slot]);
        if (cached is not null && cached.ReverseSourceEndpoint == local && cached.ReverseDestinationEndpoint == remote)
        {
            cached.Touch(now);
            association = cached;
            return true;
        }

        lock (_gate)
        {
            NoteGateEntry();
            if (_byReverse.TryGetValue(new ReverseRedirectTuple(local, remote), out association))
            {
                association.Touch(now);
                Volatile.Write(ref _warmReverse[slot], association);
                return true;
            }
            association = null;
            return false;
        }
    }

    /// <summary>
    /// Whether any live association's listener occupies <paramref name="port"/> (the warm-path
    /// prefilter). A reverse candidate's source port is always a listener port — the reference count
    /// rises in <see cref="TryClaim"/> in the same hold that adds the reverse index entry, and falls in
    /// the same hold that removes it — so a miss proves the packet cannot match the reverse index; a hit
    /// is merely a candidate, and the full tuple check runs before anything is reversed.
    /// </summary>
    internal bool IsReverseCandidatePort(ushort port) => Volatile.Read(ref _candidatePorts[port]) != 0;

    /// <summary>
    /// Resolves the association that owns <paramref name="originalKey"/>, from the lock-free cache when
    /// its validated entry is resident and from the gated authority otherwise (which warms the slot).
    /// </summary>
    public bool TryResolveByOriginal(FlowKey originalKey, DateTimeOffset now, out TcpRedirectAssociation? association)
    {
        var slot = OriginalSlot(originalKey);
        var cached = Volatile.Read(ref _warmOriginal[slot]);
        if (cached is not null && cached.OriginalKey.Equals(originalKey))
        {
            cached.Touch(now);
            association = cached;
            return true;
        }

        lock (_gate)
        {
            NoteGateEntry();
            if (_byOriginal.TryGetValue(originalKey, out association))
            {
                association.Touch(now);
                Volatile.Write(ref _warmOriginal[slot], association);
                return true;
            }
            association = null;
            return false;
        }
    }

    /// <summary>
    /// Resolves an association whose original flow endpoints match the given IP address pair in
    /// either orientation — the fragment match: a non-first IP fragment carries no ports, so the
    /// address pair is the finest key it can be attributed by. When several associations share the
    /// pair, the most recently claimed one wins (see <see cref="TryClaim"/>). A mixed-family pair
    /// never matches: a flow's endpoints always share one family.
    /// </summary>
    public bool TryResolveByAddressPair(IPAddressValue first, IPAddressValue second, DateTimeOffset now, out TcpRedirectAssociation? association)
    {
        association = null;
        if (first.Family != second.Family) return false;
        var pair = NormalizeAddressPair(first, second);
        lock (_gate)
        {
            NoteGateEntry();
            if (_byAddressPair.TryGetValue(pair, out var found))
            {
                found.Touch(now);
                association = found;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Removes a specific association from both indexes. Used by the coordinator's fail-closed
    /// teardown when a listener, rewrite, or relay setup fails so the alias is released for reuse
    /// and no half-claimed flow lingers. <paramref name="onRemoved"/>, when supplied, runs inside
    /// the table gate after the indexes are updated, so observers that see the association gone
    /// also see whatever the callback published (e.g. the TIME_WAIT tombstone) — there is no
    /// window between removal and publication.
    /// </summary>
    public bool TryRemove(TcpRedirectAssociation association, Action<TcpRedirectAssociation>? onRemoved = null)
    {
        lock (_gate)
        {
            NoteGateEntry();
            if (!_byOriginal.TryGetValue(association.OriginalKey, out var current) || !ReferenceEquals(current, association)) return false;
            RemoveUnderGate(association);
            onRemoved?.Invoke(association);
            return true;
        }
    }

    /// <summary>
    /// The one removal body: every index this association occupies is released, its listener port count
    /// falls, and both warm-cache entries are cleared with an
    /// <see cref="object.ReferenceEquals(object, object)"/> guard (never a
    /// blind wipe of a colliding association's entry). Factoring it is what makes "no removal site can
    /// forget the caches" structural rather than a review promise.
    /// </summary>
    private void RemoveUnderGate(TcpRedirectAssociation association)
    {
        _byOriginal.Remove(association.OriginalKey);
        _byTranslatedListener.Remove(association.TranslatedListenerTuple);
        _byReverse.Remove(new ReverseRedirectTuple(association.ReverseSourceEndpoint, association.ReverseDestinationEndpoint));
        RemoveAddressPairUnderGate(association);
        association.ReleaseOriginalSynTemplate();
        // Released under the same gate as the index removal; the caller's ReferenceEquals guard makes
        // idempotent removals a no-op here, so the count never double-decrements.
        Interlocked.Decrement(ref _candidatePorts[association.TranslatedListenerTuple.Port]);
        var reverseSlot = ReverseSlot(association.ReverseSourceEndpoint, association.ReverseDestinationEndpoint);
        if (ReferenceEquals(Volatile.Read(ref _warmReverse[reverseSlot]), association)) Volatile.Write(ref _warmReverse[reverseSlot], null);
        var originalSlot = OriginalSlot(association.OriginalKey);
        if (ReferenceEquals(Volatile.Read(ref _warmOriginal[originalSlot]), association)) Volatile.Write(ref _warmOriginal[originalSlot], null);
    }

    /// <summary>
    /// Removes associations idle past <paramref name="idleTimeout"/>. The table has <em>no production
    /// caller</em> — tests only; the live TCP expiry leg is
    /// <see cref="TcpRedirectSessionStore.RemoveExpiredAsync"/> — but its gate is a warm packet-path gate,
    /// so the sweep keeps the same contract as the live sites: one scan hold that collects idle-elapsed
    /// associations into a reused scratch (no predicates, no removals), then one short hold per removal that
    /// re-checks the association's presence and idleness before running the removal body.
    /// </summary>
    public int RemoveExpired(DateTimeOffset now, TimeSpan idleTimeout)
    {
        var cutoffBucket = ActivityBucket.Cutoff(now, idleTimeout);
        lock (_sweepGate)
        {
            _expiredScratch.Clear();
            lock (_gate)
            {
                NoteGateEntry();
                foreach (var association in _byOriginal.Values)
                {
                    if (association.BucketForDiagnostics < cutoffBucket) _expiredScratch.Add(association);
                }
            }

            var removed = 0;
            foreach (var association in _expiredScratch)
            {
                lock (_gate)
                {
                    NoteGateEntry();
                    // Re-check under the gate: the association may have been torn down, or touched by a
                    // reverse/forward resolve, since the scan released it.
                    if (!_byOriginal.TryGetValue(association.OriginalKey, out var current) ||
                        !ReferenceEquals(current, association) ||
                        association.BucketForDiagnostics >= cutoffBucket)
                    {
                        continue;
                    }

                    RemoveUnderGate(association);
                    removed++;
                }
            }

            return removed;
        }
    }

    public TcpRedirectAssociation[] Snapshot()
    {
        lock (_gate)
        {
            NoteGateEntry();
            return [.. _byOriginal.Values];
        }
    }

    private void RemoveAddressPairUnderGate(TcpRedirectAssociation association)
    {
        var pair = NormalizeAddressPair(association.OriginalKey.Local.Address, association.OriginalKey.Remote.Address);
        // A newer association on the same pair may own the slot; only clear what this one owns.
        if (ReferenceEquals(_byAddressPair.GetValueOrDefault(pair), association)) _byAddressPair.Remove(pair);
    }

    private static AddressPair NormalizeAddressPair(IPAddressValue first, IPAddressValue second)
        => first.Bits <= second.Bits ? new AddressPair(first, second) : new AddressPair(second, first);

    private int ReverseSlot(Endpoint source, Endpoint destination) => HashCode.Combine(source, destination) & (_warmReverse.Length - 1);

    private int OriginalSlot(FlowKey originalKey) => originalKey.GetHashCode() & (_warmOriginal.Length - 1);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ReverseRedirectTuple(Endpoint Source, Endpoint Destination);

    /// <summary>An orientation-independent endpoint address pair; the fragment match key.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct AddressPair(IPAddressValue Lesser, IPAddressValue Greater);
}
