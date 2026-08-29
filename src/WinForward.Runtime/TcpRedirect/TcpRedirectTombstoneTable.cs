using System.Runtime.InteropServices;
using WinForward.Core;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The TIME_WAIT-grace tombstone index for torn-down TCP redirects. When a redirect association
/// is removed from <see cref="TcpRedirectTable"/>, a tombstone remembers both of its lookup keys
/// (the original flow key and the reverse redirect tuple) for a fixed grace window, so stragglers
/// of the finished handshake — the client's final ACK, retransmitted FIN/ACK, TIME_WAIT residue —
/// resolve as a tombstone hit and are silently dropped instead of falling into the
/// NotRelevant→Pass fallback, which would send them toward the real server and bounce an RST back
/// at the client. Tombstones occupy neither the redirect table's claim capacity nor the
/// coordinator's session budget; the table is bounded with the same capacity as the session
/// budget and evicts the oldest entry when full, so the worst case degrades to the pre-tombstone
/// behavior instead of failing or growing without bound. Thread-safe via a single gate lock,
/// matching <see cref="TcpRedirectTable"/>.
/// </summary>
internal sealed class TcpRedirectTombstoneTable
{
    private readonly Dictionary<FlowKey, TombstoneEntry> _byForward = [];
    private readonly Dictionary<ReverseTuple, TombstoneEntry> _byReverse = [];
    private readonly Queue<TombstoneEntry> _insertionOrder = new();
    private readonly Lock _gate = new();
    private readonly int _capacity;

    public TcpRedirectTombstoneTable(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        _capacity = capacity;
    }

    /// <summary>The number of live tombstones (distinct forward keys); for tests and diagnostics.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _byForward.Count;
        }
    }

    /// <summary>
    /// Records both lookup keys of a torn-down redirect with a shared expiry timestamp. The write
    /// never fails: when the table is full the oldest tombstone is evicted first, and a key pair
    /// torn down again is refreshed with the newest expiry. Both indexes stay in lockstep — every
    /// live entry owns exactly one key on each side.
    /// </summary>
    public void TryAdd(FlowKey forward, Endpoint reverseSource, Endpoint reverseDestination, DateTimeOffset expiryUtc)
    {
        var reverse = new ReverseTuple(reverseSource, reverseDestination);
        lock (_gate)
        {
            if (_byForward.TryGetValue(forward, out var oldByForward)) RemoveEntryUnderGate(oldByForward);
            if (_byReverse.TryGetValue(reverse, out var oldByReverse) && !ReferenceEquals(oldByReverse, oldByForward)) RemoveEntryUnderGate(oldByReverse);
            if (_byForward.Count >= _capacity) EvictOldestUnderGate();

            var entry = new TombstoneEntry(forward, reverse, expiryUtc);
            _byForward[forward] = entry;
            _byReverse[reverse] = entry;
            _insertionOrder.Enqueue(entry);
        }
    }

    /// <summary>Whether the original flow key of a torn-down redirect is still inside its grace window.</summary>
    public bool TryHit(FlowKey forward, DateTimeOffset now)
    {
        lock (_gate) return _byForward.TryGetValue(forward, out var entry) && now < entry.ExpiryUtc;
    }

    /// <summary>Whether the reverse redirect tuple of a torn-down redirect is still inside its grace window.</summary>
    public bool TryHit(Endpoint reverseSource, Endpoint reverseDestination, DateTimeOffset now)
    {
        var reverse = new ReverseTuple(reverseSource, reverseDestination);
        lock (_gate) return _byReverse.TryGetValue(reverse, out var entry) && now < entry.ExpiryUtc;
    }

    /// <summary>Removes tombstones whose grace window elapsed; returns how many were reclaimed.</summary>
    public int RemoveExpired(DateTimeOffset now)
    {
        lock (_gate)
        {
            var expired = _byForward.Values.Where(entry => now >= entry.ExpiryUtc).ToArray();
            foreach (var entry in expired) RemoveEntryUnderGate(entry);
            return expired.Length;
        }
    }

    private void EvictOldestUnderGate()
    {
        while (_insertionOrder.Count > 0)
        {
            var oldest = _insertionOrder.Dequeue();
            // A queue entry whose dictionary slot was since replaced by a newer TryAdd is stale.
            if (!ReferenceEquals(_byForward.GetValueOrDefault(oldest.Forward), oldest)) continue;
            RemoveEntryUnderGate(oldest);
            return;
        }
    }

    private void RemoveEntryUnderGate(TombstoneEntry entry)
    {
        if (ReferenceEquals(_byForward.GetValueOrDefault(entry.Forward), entry)) _byForward.Remove(entry.Forward);
        if (ReferenceEquals(_byReverse.GetValueOrDefault(entry.Reverse), entry)) _byReverse.Remove(entry.Reverse);
    }

    private sealed record TombstoneEntry(FlowKey Forward, ReverseTuple Reverse, DateTimeOffset ExpiryUtc);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ReverseTuple(Endpoint Source, Endpoint Destination);
}
