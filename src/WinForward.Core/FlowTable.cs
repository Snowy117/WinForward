using System.Runtime.InteropServices;

namespace WinForward.Core;

public sealed class FlowTable
{
    private readonly Dictionary<FlowKey, FlowState> _states;
    private readonly Dictionary<TransportTuple, FlowState> _transportIndex;
    private readonly List<FlowKey> _expiredScratch = [];
    private readonly FlowState[] _freeStates;
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
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
    /// consulted for idle-elapsed candidates. A null predicate removes every idle entry.
    /// </summary>
    public int RemoveExpired(DateTimeOffset now, TimeSpan idleTimeout, Func<FlowKey, bool>? isHeld = null)
    {
        lock (_gate)
        {
            // One enumeration collecting expired keys (a snapshot: the removal below must not
            // mutate the dictionary mid-enumeration), into a reused scratch buffer.
            _expiredScratch.Clear();
            foreach (var pair in _states)
            {
                if (now - pair.Value.LastActivityUtc < idleTimeout) continue;
                if (isHeld is not null && isHeld(pair.Key)) continue;
                _expiredScratch.Add(pair.Key);
            }

            if (_expiredScratch.Count == 0) return 0;
            var removed = _expiredScratch.Count;
            foreach (var key in _expiredScratch)
            {
                if (_states.Remove(key, out var state))
                {
                    RemoveFromTransportIndex(state);
                    ReturnState(state);
                }
            }
            _expiredScratch.Clear();

            return removed;
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
