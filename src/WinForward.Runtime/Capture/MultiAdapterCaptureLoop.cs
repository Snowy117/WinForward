using System.Runtime.Versioning;
using WinForward.NdisApi;
using WinForward.Windows;

namespace WinForward.Runtime.Capture;

/// <summary>
/// An <see cref="IPacketCaptureLoop"/> that runs one <see cref="NdisCapturePump"/> per in-scope
/// adapter concurrently. Graceful cancellation (the linked token) lets every pump exit normally. An
/// unexpected failure in any pump cancels the shared token so sibling pumps stop promptly, then the
/// exception propagates for the runtime to restore adapter modes (fail-closed). A degraded pump exit
/// (R7: transient-read retries exhausted, or a permanent native read error) is NOT a failure: the
/// pump returns normally, siblings keep running uncanceled, and the optional
/// <c>onAdapterDegraded(adapter, nativeError)</c> callback is forwarded so the wiring can restore
/// just that adapter's mode while interception continues elsewhere. The driver stays owned by the
/// caller for the whole run, so <see cref="DisposeAsync"/> only stops the pumps.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultiAdapterCaptureLoop : IPacketCaptureLoop
{
    private readonly NdisCapturePump[] _pumps;
    private readonly Func<WindowsAdapter, int, ValueTask>? _onAdapterDegraded;
    private long _degradedAdapterCount;

    public MultiAdapterCaptureLoop(INdisPacketReader driver, IReadOnlyList<WindowsAdapter> adapters, CapturePacketProcessor processor, TimeSpan? pollDelay = null, Func<WindowsAdapter, int, ValueTask>? onAdapterDegraded = null, Action<WindowsAdapter, int, int>? onAdapterTransientRetry = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(processor);
        _onAdapterDegraded = onAdapterDegraded;
        var onBatchCompleted = processor.OnBatchCompleted;
        _pumps = adapters
            .Select(adapter => new NdisCapturePump(
                driver,
                adapter.RuntimeHandle,
                (packet, cancellationToken) => processor.ProcessAsync(packet, adapter, cancellationToken),
                new NdisCapturePumpOptions
                {
                    PollDelay = pollDelay,
                    OnBatchCompleted = onBatchCompleted is null ? null : () => onBatchCompleted(adapter.RuntimeHandle),
                    OnTransientRetry = onAdapterTransientRetry is null ? null : (nativeError, attempt) => onAdapterTransientRetry(adapter, nativeError, attempt),
                    OnDegraded = nativeError => OnPumpDegraded(adapter, nativeError),
                }))
            .ToArray();
    }

    /// <summary>How many adapter pumps exited through the degraded path (telemetry, R7).</summary>
    internal long DegradedAdapterCount => Interlocked.Read(ref _degradedAdapterCount);

    public async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = _pumps.Select(pump => RunPumpAsync(pump, linked)).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            await linked.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pump in _pumps)
        {
            await pump.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunPumpAsync(NdisCapturePump pump, CancellationTokenSource linked)
    {
        try
        {
            await pump.RunAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Graceful shutdown path: the pump is stopping because the run is being cancelled.
        }
        catch
        {
            await linked.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void OnPumpDegraded(WindowsAdapter adapter, int nativeError)
    {
        Interlocked.Increment(ref _degradedAdapterCount);
        if (_onAdapterDegraded is not { } callback) return;
        // Fire-and-forget with observation: the degraded pump has already exited its loop, so the
        // callback cannot delay it; a faulting callback must not surface as an unobserved task
        // exception (the wiring performs its own logging and best-effort mode restore).
        _ = ForwardDegradationAsync(callback, adapter, nativeError);
    }

    private static async Task ForwardDegradationAsync(Func<WindowsAdapter, int, ValueTask> callback, WindowsAdapter adapter, int nativeError)
    {
        try
        {
            await callback(adapter, nativeError).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Observation only: the degraded-exit error log is the wired callback's own concern.
            GC.KeepAlive(exception);
        }
    }
}
