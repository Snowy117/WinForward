namespace WinForward.Runtime.Capture;

/// <summary>
/// The capture runner's refresh workers (task 09-17 R1, split out by task
/// 09-20-structured-concurrency C4 to keep <see cref="LayeredCaptureRunner"/> within the
/// effective-line cap): the adapter-list monitor that owns a dedicated blocking OS thread, and the
/// periodic link-state re-check tick. Both are tracked by the run's <see cref="QuiescenceScope"/>,
/// whose drain is the join for them.
/// </summary>
internal sealed class CaptureRefreshWorkers(
    IAdapterListChangeSource changeSource,
    Action signal,
    TimeSpan periodicRefreshInterval,
    TimeProvider time)
{
    /// <summary>
    /// Starts the adapter-list monitor on a dedicated background OS thread. The body blocks in
    /// <see cref="IAdapterListChangeSource.WaitOne"/> for the whole run, so it cannot be a
    /// <c>QuiescenceScope.Run</c> child — <c>Run</c> invokes its body inline on the caller's
    /// thread — and it must not occupy a ThreadPool thread. The lease it takes is what makes the
    /// scope's drain the join.
    /// </summary>
    public void StartMonitor(QuiescenceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var thread = new Thread(() => RunMonitor(scope))
        {
            IsBackground = true,
            Name = "wf-capture-monitor",
        };
        thread.Start();
    }

    /// <summary>
    /// Periodic link-state re-check (task 09-17 R1-A): the NDISRD bound-adapter list is not
    /// rebuilt by host address changes (IPv6 temporary-address rotation), so a timer raises a
    /// NON-forced refresh demand every interval. An unchanged enumeration still resolves as the
    /// no-op skip, and the storm guard absorbs races with NDISRD signals; the demand gate sees
    /// tick signals exactly like watcher signals, so no new state machine exists. The timer is
    /// TimeProvider-backed to stay fake-time testable. The caller keeps the positive-interval
    /// guard outside this method: <see cref="PeriodicTimer"/> rejects a non-positive period.
    /// </summary>
    public async Task PeriodicRefreshTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(periodicRefreshInterval, time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                signal();
            }
        }
        catch (OperationCanceledException)
        {
            // Run shutdown; a signal that can no longer be consumed changes nothing.
        }
    }

    private void RunMonitor(QuiescenceScope scope)
    {
        if (!scope.TryEnter(out var lease)) return;
        try
        {
            MonitorLoop(scope.Token);
        }
        catch (Exception exception)
        {
            scope.RecordFault(exception, "capture.monitor");
        }
        finally
        {
            lease.Dispose();
        }
    }

    private void MonitorLoop(CancellationToken cancellationToken)
    {
        try
        {
            // Blocking wait: this loop owns its dedicated thread by design. A false return (the
            // change source was disposed/unblocked) ends the monitor loop.
            while (changeSource.WaitOne(cancellationToken)) signal();
        }
        catch (OperationCanceledException)
        {
            // Monitor shutdown via its cancellation token.
        }
        catch (ObjectDisposedException)
        {
            // The change source was disposed concurrently with shutdown; no signal can follow.
        }
    }
}
