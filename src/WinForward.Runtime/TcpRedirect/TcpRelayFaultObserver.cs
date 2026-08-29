using WinForward.Configuration;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// Observes relay completions on paths that discard a relay without awaiting its
/// <see cref="ITcpRelay.Completion"/> — <see cref="TcpProxyRelay.DisposeAsync"/> and the
/// acceptor's attach-failure branch (S3). A faulted completion there would never be observed
/// and would later surface as an unobserved task exception; this helper hooks a continuation
/// that swallows and debug-logs the fault instead. The success-path observer in
/// <see cref="TcpRedirectAcceptor"/> (which owns teardown) already awaits Completion and is
/// deliberately unchanged. The continuation runs synchronously on the fault transition, so by
/// the time the completion is observably faulted the exception is already observed — no
/// finalizer or GC timing is involved.
/// </summary>
internal static class TcpRelayFaultObserver
{
    /// <summary>
    /// Hooks the fault-swallowing continuation onto a relay's completion. Cold path (relay
    /// discard or dispose, once per relay); the logging continuation itself only executes when
    /// the completion actually faults.
    /// </summary>
    public static void Observe(ITcpRelay relay, IRuntimeLogger logger)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(logger);
        _ = relay.Completion.ContinueWith(
            task =>
            {
                // Only reading Exception (or awaiting the task) marks a fault observed — the
                // continuation alone does not — and the production default threshold (info)
                // disables debug logging, so the read must precede the logging gate.
                var exception = task.Exception?.InnerException ?? task.Exception;
                if (!logger.IsEnabled(RuntimeLogLevel.Debug)) return;
                logger.Event(RuntimeLogLevel.Debug, "tcp.relay.faulted",
                    new RuntimeLogField("error", exception?.GetType().Name));
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
