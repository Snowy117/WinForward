namespace WinForward.NdisApi;

/// <summary>
/// The capture pump's observable state as one immutable snapshot (P2): whether this pump exited
/// through the degraded path, the native error of that exit (0 while it has not degraded), and
/// the transient read-retry / incident totals. Control surfaces that act on the pump
/// (<c>PumpThread</c>, <c>RunIterationForTests</c>) stay on the pump itself — this record is
/// read-only.
/// </summary>
internal sealed record NdisPumpDiagnostics(
    bool IsDegraded,
    int LastDegradedNativeErrorCode,
    long TransientReadRetryCount,
    long TransientReadIncidentCount);
