using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinForward.Core;

public sealed class FlowTable
{
    /// <summary>
    /// Entries one scan hold may examine. Hold granularity is deliberately minimal: a scan hold collects
    /// nothing, and a removal hold removes exactly one entry at the cursor. The unit is the examination
    /// because that is what bounds the work a concurrent <see cref="TryResolve"/> can queue behind, and the
    /// acceptance evidence is the countable <see cref="SweepHoldProbe"/>, not a wall-clock reading. The
    /// sweep's own duration is report-only and deliberately pays for this granularity: measured, one
    /// removal per hold keeps 11.6–14.2 M resolves inside the sweep window per 15 s against ~0.1 M for
    /// 256-removal holds, at the cost of a ~120 ms all-expired round against the before-series' 26.2 ms.
    /// </summary>
    internal const int SweepChunkEntries = 256;

    private readonly Dictionary<FlowKey, FlowState> _states;
    private readonly Dictionary<TransportTuple, FlowState> _transportIndex;

    // Live-slot registry: _liveStates[0.._liveCount) is exactly the set of FlowState instances in
    // _states, with no holes and no duplicates. The dictionary has no enumeration that can be resumed
    // across a released gate, so the sweep walks this array instead and keeps the cursor across its
    // chunked holds. Every mutation of the registry — the append in TryClaimResolved and the swap-remove
    // in the sweep — happens under _gate, the same gate every claim takes.
    private readonly FlowState[] _liveStates;
    private readonly FlowState[] _freeStates;
    private readonly Lock _gate = new();

    // Sweep-level single flight, outer to _gate and never taken while _gate is held. Two interleaved
    // rounds could otherwise double-return a state to _freeStates and let two flows share one FlowState.
    private readonly Lock _sweepGate = new();
    private readonly TimeProvider _timeProvider;
    private int _liveCount;
    private int _freeStateCount;
    private long _nextGeneration;

    /// <summary>
    /// Creates a flow table. A null <paramref name="timeProvider"/> uses <see cref="TimeProvider.System"/>
    /// (production behavior); tests inject a controllable clock to assert refresh and expiry boundaries exactly.
    /// </summary>
    public FlowTable(int capacity = 65_536, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _states = new Dictionary<FlowKey, FlowState>(capacity);
        _transportIndex = new Dictionary<TransportTuple, FlowState>(capacity * 2);
        _liveStates = new FlowState[capacity];
        _freeStates = new FlowState[capacity];
    }

    /// <summary>The bounded capacity the table was constructed with.</summary>
    public int Capacity { get; }

    /// <summary>The number of tracked flows (a gate-consistent snapshot; diagnostics only).</summary>
    public int Count
    {
        get { lock (_gate) return _states.Count; }
    }

    /// <summary>
    /// The number of states in the live-slot registry (a gate-consistent snapshot; diagnostics only, so a
    /// test can assert the registry mirrors <see cref="Count"/> across churn).
    /// </summary>
    internal int LiveStateCountForDiagnostics
    {
        get { lock (_gate) return _liveCount; }
    }

    /// <summary>
    /// Whether the calling thread holds the table gate (diagnostics only): a test predicate reads it to
    /// prove caller code never runs under this table's lock.
    /// </summary>
    internal bool HoldsGateForDiagnostics => _gate.IsHeldByCurrentThread;

    /// <summary>
    /// The work-per-hold diagnostics sink, or null in production (diagnostics only). The round reads it
    /// once into a local and records each hold behind a plain null check, so a Release run carries it with
    /// no allocation and no conditional-compilation hook.
    /// </summary>
    internal SweepHoldProbe? HoldProbe { get; set; }

    /// <summary>
    /// Counts what each <see cref="_gate"/> hold inside <see cref="RemoveExpired"/> did, so the
    /// work-per-hold bound is provable by exact counts instead of a wall-clock reading. With minimal hold
    /// granularity a scan hold examines between 1 and <see cref="SweepChunkEntries"/> entries and a removal
    /// hold removes 0 or 1, so <see cref="RemovalHistogram"/> only ever has counts at 0 and 1 — the array
    /// keeps the chunk-sized shape so the sink is the same for either granularity. A held candidate records
    /// a removal hold with 0 (it still took the lock to advance the cursor). The sink is passive and
    /// single-round: the attaching test installs a fresh instance for the round it means to measure.
    /// </summary>
    internal sealed class SweepHoldProbe
    {
        public int ScanHolds { get; private set; }

        public int RemovalHolds { get; private set; }

        public int MaxExaminations { get; private set; }

        public int MaxRemovals { get; private set; }

        public int[] ExaminationHistogram { get; } = new int[SweepChunkEntries + 1];

        public int[] RemovalHistogram { get; } = new int[SweepChunkEntries + 1];

        public void RecordScanHold(int examinations)
        {
            ScanHolds++;
            if (examinations > MaxExaminations) MaxExaminations = examinations;
            ExaminationHistogram[examinations]++;
        }

        public void RecordRemovalHold(int removals)
        {
            RemovalHolds++;
            if (removals > MaxRemovals) MaxRemovals = removals;
            RemovalHistogram[removals]++;
        }
    }

    /// <summary>
    /// Resolves a flow for a packet whose key may differ from the stored key in direction, origin
    /// kind, or origin adapter. A flow is identified by its transport tuple (address family,
    /// protocol, and the local/remote endpoint pair in either orientation); origin kind and origin
    /// adapter are provenance metadata that must not cause a reverse or cross-adapter observation
    /// to be re-evaluated as a new flow. A transport-tuple index resolves either orientation without
    /// scanning the flow table.
    /// </summary>
    public bool TryResolve(FlowKey key, out FlowState? state)
    {
        lock (_gate)
        {
            return TryResolveLocked(key, out state);
        }
    }

    /// <summary>
    /// Atomically resolves an existing flow for <paramref name="key"/> or claims a new flow when no
    /// matching flow exists. The decision factory runs only for a genuinely new flow. When a flow is
    /// already present (including in reverse or cross-adapter orientation) the existing decision is
    /// returned and the factory is not invoked, so policy is evaluated exactly once per logical flow.
    /// </summary>
    public bool TryClaimResolved(FlowKey key, Func<FlowDecision> decide, out FlowState? state)
    {
        lock (_gate)
        {
            if (TryResolveLocked(key, out state)) return state is not null;
            if (_states.Count >= Capacity)
            {
                state = null;
                return false;
            }

            var decision = decide();
            var created = RentState();
            created.Reset(key, decision, ++_nextGeneration);
            _states.Add(key, created);
            AddToTransportIndex(created);
            _liveStates[_liveCount] = created;
            _liveCount++;
            state = created;
            return true;
        }
    }

    /// <summary>
    /// Removes flow decisions idle past <paramref name="idleTimeout"/>. An entry whose idle has
    /// elapsed but whose <paramref name="isHeld"/> predicate reports a live holder (e.g. a TCP
    /// redirect session still relaying, or a flow inside its post-teardown grace window) is
    /// skipped without touching <see cref="FlowState.LastActivityUtc"/>, so it expires at its
    /// original idle point once the hold lapses instead of being re-armed. The predicate is only
    /// consulted for idle-elapsed candidates, and it runs with <em>no table lock held</em> — the
    /// store/tombstone locks it may take are never nested inside this table's gate. A null predicate
    /// removes every idle entry.
    /// <para>
    /// One call still completes one round over the states present at entry, at minimal hold granularity: a
    /// scan hold examines at most <see cref="SweepChunkEntries"/> entries and removes nothing, the predicate
    /// runs with no lock held, and one removal hold removes exactly one entry at the cursor. Because the
    /// swap-remove happens at the cursor, the tail element lands exactly where the cursor points and is
    /// examined next, so the round never needs a cursor rewind. Every hold is therefore bounded in
    /// examinations or removals — the expiry work a concurrent <see cref="TryResolve"/> can queue behind;
    /// attach a <see cref="SweepHoldProbe"/> to see the counts. Concurrent calls serialize here, which keeps
    /// the exactly-once state-pool return intact.
    /// </para>
    /// </summary>
    public int RemoveExpired(DateTimeOffset now, TimeSpan idleTimeout, Func<FlowKey, bool>? isHeld = null)
    {
        lock (_sweepGate)
        {
            // One comparison per entry instead of a DateTimeOffset subtraction, and the shape a bucketed
            // activity stamp needs: cut off at an integer computed once per call (research F3.4).
            var cutoffTicks = now.UtcTicks - idleTimeout.Ticks;
            var probe = HoldProbe;

            // The round's progress target (the states present at entry) under the gate. It is not the
            // registry's validity bound: _liveCount also shrinks as this round removes, and the loop
            // needs both so it can never read the stale slot at cursor == _liveCount after the last
            // removal and hand the same state to _freeStates twice.
            int roundLimit;
            lock (_gate) roundLimit = _liveCount;

            var cursor = 0;
            var removed = 0;
            while (cursor < roundLimit && cursor < _liveCount)
            {
                var candidate = ScanChunk(ref cursor, roundLimit, cutoffTicks, out var examinations);
                probe?.RecordScanHold(examinations);

                // The chunk held only live entries: take the next chunk, or finish when a bound is reached.
                if (candidate is null) continue;

                // No table lock is held around the predicate (requirement 3). A hold skips its entry
                // without touching its activity, so it expires at its original idle point.
                if (isHeld is not null && isHeld(candidate.Key))
                {
                    lock (_gate) cursor++;
                    probe?.RecordRemovalHold(0);
                    continue;
                }

                var removalsHere = RemoveCandidateAt(ref cursor, candidate, cutoffTicks);
                probe?.RecordRemovalHold(removalsHere);
                removed += removalsHere;
            }

            AssertRegistryConsistent();
            return removed;
        }
    }

    /// <summary>
    /// One scan hold. Examines up to <see cref="SweepChunkEntries"/> registry entries from
    /// <paramref name="cursor"/>, advancing it past every still-live entry, and returns the first
    /// idle-elapsed candidate with the cursor left <em>on</em> it — or null when the chunk held only live
    /// entries or a loop bound was reached. Nothing is removed and no caller code runs here, so the hold's
    /// cost is this chunk's cost; <paramref name="examinations"/> includes the candidate returned, which is
    /// the last entry this hold looked at.
    /// </summary>
    private FlowState? ScanChunk(ref int cursor, int roundLimit, long cutoffTicks, out int examinations)
    {
        lock (_gate)
        {
            var examined = 0;
            while (cursor < roundLimit && cursor < _liveCount && examined < SweepChunkEntries)
            {
                var state = _liveStates[cursor];

                // The comparison is the integer form of the DateTimeOffset subtraction this replaced:
                // idle has elapsed once `now - LastActivityUtc >= idleTimeout`, i.e. once the stamp is at
                // or below the cutoff. A strict `<` here would keep one extra tick of retention and break
                // the exact boundary the expiry tests pin.
                if (state.LastActivityUtc.UtcTicks > cutoffTicks)
                {
                    cursor++;
                    examined++;
                    continue;
                }

                examinations = examined + 1;
                return state;
            }

            examinations = examined;
            return null;
        }
    }

    /// <summary>
    /// One removal hold: re-checks the candidate at the cursor under the gate (still present, still the
    /// same instance, still idle-elapsed, still removable by key) and swap-removes it there, so the tail
    /// element lands exactly at the cursor and is examined next — which is why the cursor never needs a
    /// rewind. The cursor only advances when the entry survived, so a claim that revived the candidate
    /// wins and is retried next round. Returns 1 when a state was removed, 0 otherwise.
    /// </summary>
    private int RemoveCandidateAt(ref int cursor, FlowState candidate, long cutoffTicks)
    {
        lock (_gate)
        {
            if (cursor < _liveCount &&
                ReferenceEquals(_liveStates[cursor], candidate) &&
                candidate.LastActivityUtc.UtcTicks <= cutoffTicks &&
                _states.Remove(candidate.Key, out _))
            {
                RemoveFromTransportIndex(candidate);
                _liveStates[cursor] = _liveStates[--_liveCount];
                ReturnState(candidate);
                return 1;
            }

            cursor++;
            return 0;
        }
    }

    /// <summary>
    /// Debug-only integrity check: after a round the registry must mirror <see cref="_states"/> exactly. A
    /// state that left the table while keeping a registry slot could be returned to the pool twice and
    /// then shared by two flows, so the invariant is asserted here and pinned by the registry churn gate.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertRegistryConsistent()
    {
        lock (_gate)
        {
            Debug.Assert(_liveCount == _states.Count, $"The flow-table live registry holds {_liveCount} slots for {_states.Count} states.");
        }
    }

    private FlowState RentState()
    {
        if (_freeStateCount > 0)
        {
            var recycled = _freeStates[--_freeStateCount];
            _freeStates[_freeStateCount] = null!;
            return recycled;
        }

        return new FlowState();
    }

    private void ReturnState(FlowState state)
    {
        state.Reset(default, default, 0);
        if (_freeStateCount < _freeStates.Length) _freeStates[_freeStateCount++] = state;
    }

    private bool TryResolveLocked(FlowKey key, out FlowState? state)
    {
        if (_states.TryGetValue(key, out state) || _transportIndex.TryGetValue(TransportTuple.From(key), out state))
        {
            state.Touch(_timeProvider.GetUtcNow());
            return true;
        }

        state = null;
        return false;
    }

    private void AddToTransportIndex(FlowState state)
    {
        var tuple = TransportTuple.From(state.Key);
        _transportIndex.Add(tuple, state);
        _transportIndex.TryAdd(tuple.Reverse(), state);
    }

    private void RemoveFromTransportIndex(FlowState state)
    {
        var tuple = TransportTuple.From(state.Key);
        _transportIndex.Remove(tuple);
        _transportIndex.Remove(tuple.Reverse());
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct TransportTuple(
        AddressFamilyKind AddressFamily,
        TransportProtocol Protocol,
        Endpoint Local,
        Endpoint Remote)
    {
        public static TransportTuple From(FlowKey key) => new(key.AddressFamily, key.Protocol, key.Local, key.Remote);
        public TransportTuple Reverse() => this with { Local = Remote, Remote = Local };

        // Transport-only hash set shared with FlowKey via FlowHash.Combine. FlowKey compares
        // origin-aware yet must hash transport-only so origin variants land in the same bucket as
        // the flows they alias; this orientation-agnostic index key needs the same buckets for the
        // reverse tuple. Both delegate to the one expression, so the two hash sets cannot drift.
        public override int GetHashCode() => FlowHash.Combine(AddressFamily, Protocol, Local, Remote);

        public bool Equals(TransportTuple other) =>
            Protocol == other.Protocol &&
            AddressFamily == other.AddressFamily &&
            Local.Port == other.Local.Port &&
            Remote.Port == other.Remote.Port &&
            Local.Address.Bits == other.Local.Address.Bits &&
            Remote.Address.Bits == other.Remote.Address.Bits &&
            Local.Address.Family == other.Local.Address.Family &&
            Remote.Address.Family == other.Remote.Address.Family &&
            Local.Address.ScopeId == other.Local.Address.ScopeId &&
            Remote.Address.ScopeId == other.Remote.Address.ScopeId;
    }
}
