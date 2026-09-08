using System.Runtime.InteropServices;

namespace WinForward.Core;

public sealed class FlowTable
{
    private readonly Dictionary<FlowKey, FlowState> _states = [];
    private readonly Dictionary<TransportTuple, FlowState> _transportIndex = [];
    private readonly Lock _gate = new();
    private readonly int _capacity;
    private long _nextGeneration;

    public FlowTable(int capacity = 65_536)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
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
            if (_states.Count >= _capacity)
            {
                state = null;
                return false;
            }

            var created = new FlowState(key, decide(), ++_nextGeneration);
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
            // mutate the dictionary mid-enumeration), with no LINQ allocation.
            List<FlowKey>? expired = null;
            foreach (var pair in _states)
            {
                if (now - pair.Value.LastActivityUtc < idleTimeout) continue;
                if (isHeld is not null && isHeld(pair.Key)) continue;
                (expired ??= []).Add(pair.Key);
            }

            if (expired is null) return 0;
            foreach (var key in expired)
            {
                if (_states.Remove(key, out var state)) RemoveFromTransportIndex(state);
            }

            return expired.Count;
        }
    }

    private bool TryResolveLocked(FlowKey key, out FlowState? state)
    {
        if (_states.TryGetValue(key, out state) || _transportIndex.TryGetValue(TransportTuple.From(key), out state))
        {
            state.Touch(DateTimeOffset.UtcNow);
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

        // Mirrors FlowKey.GetHashCode's field set on purpose: FlowKey hashes exactly these
        // transport fields while also comparing origin, and TransportTuple is the
        // orientation-agnostic index key. Two equivalence relations over one endpoint shape —
        // parallel by design, not a dedup candidate.
        public override int GetHashCode() => HashCode.Combine(
            (ulong)Local.Address.Bits,
            (ulong)(Local.Address.Bits >> 64),
            (ulong)Remote.Address.Bits,
            (ulong)(Remote.Address.Bits >> 64),
            Local.Port,
            Remote.Port,
            (byte)AddressFamily,
            (byte)Protocol);

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
