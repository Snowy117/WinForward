namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The TCP coordinator's observable counters as one immutable snapshot (P2): the concurrent-loser
/// total (the redirect-table exactly-once path), the capacity-gate rejection total, the
/// pending-SYN-setup counts (live entries, charged bytes, retention-TTL expiries, setup-failure
/// cooldowns), and the batched-injection lane state (frames pending in a lane, frames degraded to
/// the immediate single send because a lane cap was reached, and degraded batch flushes). Control
/// surfaces that mutate or drain state stay on the coordinator itself — this record is read-only.
/// The live session count deliberately stays on the coordinator (<c>SessionCount</c>), which
/// computes it under the store gate.
/// </summary>
internal sealed record TcpRedirectDiagnostics(
    long ConcurrentLoserCount,
    long CapacityRejectionCount,
    int PendingSetupActiveCount,
    long PendingSetupChargedBytes,
    long PendingSetupTtlExpiredCount,
    int PendingSetupCooldownCount,
    int RedirectPendingCount,
    long RedirectOverflowCount,
    long RedirectDegradedFlushCount);
