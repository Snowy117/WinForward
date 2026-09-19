namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The TCP coordinator's observable counters as one immutable snapshot (P2): the concurrent-loser
/// total (the redirect-table exactly-once path), the capacity-gate rejection total, and the
/// pending-SYN-setup counts (live entries, charged bytes, retention-TTL expiries, setup-failure
/// cooldowns). Control surfaces that mutate or drain state stay on the coordinator itself — this
/// record is read-only. The live session count deliberately stays on the coordinator
/// (<c>SessionCount</c>), which computes it under the store gate.
/// </summary>
internal sealed record TcpRedirectDiagnostics(
    long ConcurrentLoserCount,
    long CapacityRejectionCount,
    int PendingSetupActiveCount,
    long PendingSetupChargedBytes,
    long PendingSetupTtlExpiredCount,
    int PendingSetupCooldownCount);
