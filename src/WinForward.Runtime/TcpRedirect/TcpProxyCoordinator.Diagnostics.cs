using WinForward.Runtime.Logging;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The observability half of <see cref="TcpProxyCoordinator"/>: the read-only counter snapshot and
/// the periodic capacity-rejection summary the idle sweeper drives. One coordinator type split
/// across files purely for the repository's effective-line budget (directory-structure.md,
/// "文件行数上限"); every member keeps its single source of truth in the coordinator's own state —
/// this file only reads it.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global // The type is instantiated outside this partial file through target-typed new (TcpRedirectComposer.cs:42, TcpCoordinatorFakes.cs:105); only its constructor-less diagnostics part lives here.
public sealed partial class TcpProxyCoordinator
{
    /// <summary>
    /// The coordinator's observable counters as one snapshot: the concurrent-loser total from the
    /// redirect-table exactly-once path (a non-zero value after a concurrent burst proves that path
    /// was exercised under genuine concurrency), the capacity-gate rejection total (explicit budget
    /// management, not setup failure), the pending-SYN-setup counts (live entries, charged
    /// bytes, retention-TTL expiries, setup-failure cooldowns), and the deferred-injection lane
    /// state (frames pending in a lane, frames degraded to an immediate single send because a lane
    /// cap was reached, and batch flushes degraded to per-frame sends); for tests and diagnostics.
    /// </summary>
    internal TcpRedirectDiagnostics Diagnostics => new(
        _setup.ConcurrentLoserCount,
        Interlocked.Read(ref _capacityRejectionCount),
        _pendingSyn.ActiveCount,
        _pendingSyn.ChargedBytes,
        _pendingSyn.TtlExpiredCount,
        _pendingSyn.CooldownCount,
        _redirectLanes.PendingCount,
        _redirectLanes.OverflowCount,
        Interlocked.Read(ref _redirectDegradedFlushCount));

    /// <summary>
    /// Emits an info-level summary of capacity-gate rejections, but only when the count advanced
    /// since the previous call. The idle-expiry sweeper invokes this on its existing periodic tick
    /// so no dedicated timer is introduced. Per-rejection trace events already exist
    /// (<c>tcp.redirect.rejected reason=capacity</c>); this is the info-level aggregate.
    /// </summary>
    internal void LogCapacitySummary()
    {
        var total = Interlocked.Read(ref _capacityRejectionCount);
        var previouslyReported = Interlocked.Exchange(ref _reportedCapacityRejectionCount, total);
        if (total == previouslyReported) return;
        TcpRedirectLog.TcpRedirectCapacity(_logger, Capacity, total, total - previouslyReported);
    }
}
