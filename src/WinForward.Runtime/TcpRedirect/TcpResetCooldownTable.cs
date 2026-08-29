using WinForward.Core;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The per-tuple amplification guard for capacity-rejection resets (S4). A SYN rejected at the
/// session budget elicits at most one RST|ACK per original 4-tuple per cooldown window, so
/// retransmitted SYNs inside the window stay silently dropped and a spoofed-source SYN flood
/// cannot turn the proxy into a reflection amplifier. Bounded by the session budget and evicts
/// the oldest claim when full, mirroring <see cref="TcpRedirectTombstoneTable"/>; it occupies
/// neither the session budget nor the redirect table. Thread-safe via a single gate lock,
/// matching the other redirect tables.
/// </summary>
internal sealed class TcpResetCooldownTable
{
    private readonly Dictionary<FlowKey, DateTimeOffset> _expiryByTuple = [];
    private readonly Queue<FlowKey> _insertionOrder = new();
    private readonly Lock _gate = new();
    private readonly int _capacity;

    public TcpResetCooldownTable(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        _capacity = capacity;
    }

    /// <summary>
    /// Claims the right to inject one reset for the tuple: true when no live claim exists or the
    /// previous window already elapsed, recording a fresh expiry; false while a claim is inside
    /// its window (the reset must stay silent). A refreshed tuple is not re-enqueued, so at
    /// capacity the eviction may drop a tuple whose expiry was just refreshed — conservative in
    /// the safe direction (a slightly early extra reset, never a missing guard).
    /// </summary>
    public bool TryClaim(FlowKey tuple, DateTimeOffset now, TimeSpan window)
    {
        lock (_gate)
        {
            if (_expiryByTuple.TryGetValue(tuple, out var expiry) && now < expiry) return false;
            if (_expiryByTuple.TryAdd(tuple, now + window))
            {
                _insertionOrder.Enqueue(tuple);
                if (_expiryByTuple.Count > _capacity) EvictOldestUnderGate();
            }
            else
            {
                _expiryByTuple[tuple] = now + window;
            }
            return true;
        }
    }

    /// <summary>Removes a tuple's cooldown claim; internal so tests can advance the window.</summary>
    internal bool Remove(FlowKey tuple)
    {
        lock (_gate) return _expiryByTuple.Remove(tuple);
    }

    private void EvictOldestUnderGate()
    {
        while (_insertionOrder.Count > 0)
        {
            var oldest = _insertionOrder.Dequeue();
            if (_expiryByTuple.Remove(oldest)) return;
        }
    }
}
