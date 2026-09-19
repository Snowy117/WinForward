namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// The UDP coordinator's observable counters as one immutable snapshot (P2): live setup
/// cooldowns, aggregate setup-queue bytes, and the setup-queue rejection / flush-TTL /
/// dial-start re-stamp totals. Control surfaces that mutate or drain state stay on the
/// coordinator itself — this record is read-only.
/// </summary>
internal sealed record UdpProxyDiagnostics(
    int SetupCooldownCount,
    long PendingSetupBytes,
    long SetupBudgetRejectionCount,
    long SetupTtlExpiredCount,
    long SetupStampsRefreshedCount);
