using WinForward.Configuration;
using WinForward.Core;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// The cross-flow bound on aggregate setup-queue memory (R4): every buffered setup datagram is
/// charged against one global byte budget before the per-flow enqueue, and every charged byte is
/// credited back exactly once at whichever sink dequeues it (flush send/TTL drop, drop-oldest
/// eviction, per-flow-bounds rejection rollback, slot drain, dispose drain). Pure Interlocked
/// accounting — no lock interaction with the coordinator gate, so charge/credit ordering against
/// slot-state transitions stays exactly the coordinator's. Also owns the setup-drop bookkeeping:
/// the per-drop trace, the running total, and a rate-limited debug summary.
/// </summary>
internal sealed class UdpSetupQueueBudget
{
    /// <summary>
    /// The default cross-flow bound on aggregate setup-queue memory (~250 full 32 KiB queues;
    /// DNS-sized traffic is ~100k datagrams). Per-flow bounds alone would permit
    /// capacity × 32 KiB to accumulate while every setup parks on the 8-wide limiter.
    /// </summary>
    internal const long SetupQueueGlobalByteBudget = 8 * 1024 * 1024;

    /// <summary>Interval between rate-limited drop-summary debug logs.</summary>
    private static readonly TimeSpan DropLogInterval = TimeSpan.FromSeconds(5);

    private readonly long _byteBudget;
    private readonly IRuntimeLogger _logger;
    private long _pendingBytes;
    private long _rejectionCount;
    private long _droppedTotal;
    private long _lastDropLogTicks;

    public UdpSetupQueueBudget(long byteBudget, IRuntimeLogger logger)
    {
        _byteBudget = byteBudget;
        _logger = logger;
    }

    /// <summary>The aggregate setup-queue bytes currently charged; for tests and diagnostics.</summary>
    internal long PendingBytes => Interlocked.Read(ref _pendingBytes);

    /// <summary>The total datagrams rejected because the global budget was exhausted; for tests and diagnostics.</summary>
    internal long RejectionCount => Interlocked.Read(ref _rejectionCount);

    /// <summary>
    /// Charges <paramref name="length"/> bytes against the global budget; on exhaustion the
    /// charge is rolled back, the rejection is counted, and the datagram must not be enqueued.
    /// A transient overshoot from racing adders is accepted (bounded by the in-flight adders).
    /// </summary>
    internal bool TryCharge(int length)
    {
        if (Interlocked.Add(ref _pendingBytes, length) > _byteBudget)
        {
            Interlocked.Add(ref _pendingBytes, -length);
            Interlocked.Increment(ref _rejectionCount);
            return false;
        }

        return true;
    }

    /// <summary>Releases <paramref name="length"/> bytes back to the budget — exactly once per charged datagram, at its dequeue sink.</summary>
    internal void Credit(int length) => Interlocked.Add(ref _pendingBytes, -length);

    /// <summary>
    /// Records setup-queue drops (per-drop trace plus running total) behind a rate-limited debug
    /// summary so a drop storm cannot flood the log.
    /// </summary>
    internal void NoteDrop(FlowKey flow, int dropped)
    {
        if (dropped <= 0) return;
        Interlocked.Add(ref _droppedTotal, dropped);
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) UdpProxyLogging.LogTrace(_logger, "udp.setupqueue.dropped", flow, new RuntimeLogField("dropped", dropped));
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastDropLogTicks);
        if (now - last >= DropLogInterval.Ticks && Interlocked.CompareExchange(ref _lastDropLogTicks, now, last) == last)
        {
            _logger.Debug($"UDP session setup queues dropped {Interlocked.Read(ref _droppedTotal)} datagram(s) total (drop-oldest).");
        }
    }

    /// <summary>Adds to the drop total without per-drop logging; the dispose drain counts silently.</summary>
    internal void AddDroppedTotal(int dropped) => Interlocked.Add(ref _droppedTotal, dropped);
}
