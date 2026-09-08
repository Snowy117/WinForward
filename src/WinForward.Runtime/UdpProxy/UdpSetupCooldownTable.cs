using WinForward.Core;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// The setup-failure cooldown index for the UDP proxy: a flow whose SOCKS5 setup failed is
/// rejected fail-closed for a short window so the next datagram does not immediately hammer a
/// dead server. Bounded by the coordinator's session capacity; a write at capacity evicts the
/// entry with the earliest retry deadline instead of refusing. Thread-safe via its own leaf
/// gate — nothing calls back into the coordinator while holding it; coordinator call sites keep
/// holding the coordinator gate around table calls wherever their own critical sections require
/// it, so the table's lock is additional, leaf-level. The structural mirror of the TCP reset
/// cooldown table (TcpRedirect/TcpResetCooldownTable.cs); distinct from the TCP redirect grace
/// index (a 60 s TIME_WAIT window) — this is a 1 s retry cooldown.
/// </summary>
internal sealed class UdpSetupCooldownTable
{
    /// <summary>How long a failed flow stays rejected before its next datagram retries setup.</summary>
    private static readonly TimeSpan SetupFailureCooldown = TimeSpan.FromSeconds(1);

    private readonly Dictionary<FlowKey, DateTimeOffset> _retryAtByFlow = [];
    private readonly Lock _gate = new();
    private readonly int _capacity;

    public UdpSetupCooldownTable(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        _capacity = capacity;
    }

    /// <summary>
    /// Returns true while the flow is inside its cooldown window (the datagram is rejected
    /// fail-closed); an entry whose retry deadline has elapsed is removed on touch.
    /// </summary>
    public bool TryHit(FlowKey flow, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_retryAtByFlow.TryGetValue(flow, out var retryAt)) return false;
            if (now < retryAt) return true;
            _retryAtByFlow.Remove(flow);
            return false;
        }
    }

    /// <summary>Arms (or refreshes) the flow's cooldown from its failure time.</summary>
    public void Write(FlowKey flow, DateTimeOffset failedAt)
    {
        lock (_gate)
        {
            // R3-UDP: the cooldown dictionary is bounded at the session capacity (one entry per
            // distinct flow is the natural upper bound). At capacity the oldest-timestamp entry
            // is evicted rather than refusing the write — refusal would skip the cooldown and
            // turn a failing-server storm into an immediate-retry storm. Refreshing an existing
            // key never grows the count.
            if (_retryAtByFlow.Count >= _capacity && !_retryAtByFlow.ContainsKey(flow)) EvictOldestUnderGate();
            _retryAtByFlow[flow] = failedAt + SetupFailureCooldown;
        }
    }

    /// <summary>Removes cooldown entries whose retry deadline has elapsed; runs on the periodic sweep.</summary>
    public void PruneExpired(DateTimeOffset now)
    {
        if (_retryAtByFlow.Count == 0) return;
        List<FlowKey>? expired = null;
        foreach (var pair in _retryAtByFlow)
        {
            if (pair.Value <= now) (expired ??= []).Add(pair.Key);
        }

        if (expired is null) return;
        foreach (var flow in expired) _retryAtByFlow.Remove(flow);
    }

    /// <summary>Clears every cooldown; the dispose drain makes the entries unreachable anyway.</summary>
    public void Clear()
    {
        lock (_gate) _retryAtByFlow.Clear();
    }

    /// <summary>The live cooldown count (bounded by capacity); for tests and diagnostics.</summary>
    public int Count
    {
        get { lock (_gate) return _retryAtByFlow.Count; }
    }

    /// <summary>
    /// Removes the cooldown entry with the earliest retry deadline. A linear scan is
    /// deliberate: setup failure is a cold path, and the timestamp value doubles as the age order.
    /// </summary>
    private void EvictOldestUnderGate()
    {
        FlowKey? oldest = null;
        var oldestRetryAt = DateTimeOffset.MaxValue;
        foreach (var pair in _retryAtByFlow)
        {
            if (pair.Value >= oldestRetryAt) continue;
            oldestRetryAt = pair.Value;
            oldest = pair.Key;
        }

        if (oldest is { } evicted) _retryAtByFlow.Remove(evicted);
    }
}
