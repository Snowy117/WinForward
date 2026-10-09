using System.Runtime.Versioning;
using WinForward.NdisApi;
using WinForward.Windows;

namespace WinForward.Runtime.Capture;

/// <summary>
/// One in-scope adapter paired with the slot its identity was interned under. The pair is produced
/// once when a capture generation is built, so classification never consults the interning table on
/// a packet path.
/// </summary>
public readonly record struct AdapterCaptureBinding(WindowsAdapter Adapter, ushort Slot);

/// <summary>
/// An <see cref="IPacketCaptureLoop"/> that runs one <see cref="NdisCapturePump"/> per in-scope
/// adapter concurrently. Graceful cancellation (the linked token) lets every pump exit normally. An
/// unexpected failure in any pump cancels the shared token so sibling pumps stop promptly, then the
/// exception propagates for the runtime to restore adapter modes (fail-closed). A degraded pump exit
/// (transient-read retries exhausted, or a permanent native read error) is NOT a failure: the pump
/// returns normally, siblings keep running uncanceled, and the optional
/// <c>onAdapterDegraded(adapter, nativeError)</c> callback is forwarded so the wiring can restore
/// just that adapter's mode while interception continues elsewhere. The driver stays owned by the
/// caller for the whole run, so <see cref="DisposeAsync"/> only stops the pumps, releases the
/// per-generation packet-arrival signals it was given, and then joins any degradation forward the
/// pumps admitted before they stopped.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultiAdapterCaptureLoop : IPacketCaptureLoop
{
    private readonly NdisCapturePump[] _pumps;
    private readonly IReadOnlyList<INdisPacketArrivalSignal?>? _arrivalSignals;
    private readonly Func<WindowsAdapter, int, ValueTask>? _onAdapterDegraded;
    private readonly QuiescenceScope _scope = new();
    private long _degradedAdapterCount;
    private int _disposeStarted;

    /// <summary>
    /// Builds one pump per binding over the shared driver, each with its own adapter handle, batch
    /// callback and optional arrival signal, and rejects a signal list that does not pair one-to-one
    /// with the bindings.
    /// </summary>
    /// <param name="driver">The packet reader every pump reads through; the caller keeps it open for the loop's lifetime.</param>
    /// <param name="bindings">The generation's adapters, one pump each, in this order.</param>
    /// <param name="processor">The packet processor the pumps dispatch through, and whose batch-completed hook they share.</param>
    /// <param name="pollDelay">Idle pacing for a pump without an arrival signal; null keeps the pump's own default.</param>
    /// <param name="onAdapterDegraded">Forwarded per adapter when its pump stops intercepting; null disables.</param>
    /// <param name="onAdapterTransientRetry">Forwarded per adapter on each bounded transient-read retry; null disables.</param>
    /// <param name="arrivalSignals">
    /// One optional packet-arrival signal per binding, positionally paired with
    /// <paramref name="bindings"/> — the pairing is visible here rather than hidden in a factory
    /// delegate. A count mismatch is rejected. The loop owns the list and disposes every signal after
    /// the pumps have stopped; null keeps the pump's sleep-poll shape for every adapter.
    /// </param>
    public MultiAdapterCaptureLoop(INdisPacketReader driver, IReadOnlyList<AdapterCaptureBinding> bindings, CapturePacketProcessor processor, TimeSpan? pollDelay = null, Func<WindowsAdapter, int, ValueTask>? onAdapterDegraded = null, Action<WindowsAdapter, int, int>? onAdapterTransientRetry = null, IReadOnlyList<INdisPacketArrivalSignal?>? arrivalSignals = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(processor);
        if (arrivalSignals is not null && arrivalSignals.Count != bindings.Count)
        {
            throw new ArgumentException($"arrivalSignals holds {arrivalSignals.Count} entries for {bindings.Count} bindings; the list is positionally paired with bindings.", nameof(arrivalSignals));
        }

        _onAdapterDegraded = onAdapterDegraded;
        _arrivalSignals = arrivalSignals;
        var onBatchCompleted = processor.OnBatchCompleted;
        _pumps = [.. bindings
            .Select((binding, index) => new NdisCapturePump(
                driver,
                binding.Adapter.RuntimeHandle,
                (packet, cancellationToken) => processor.ProcessAsync(packet, binding.Adapter, binding.Slot, cancellationToken),
                new NdisCapturePumpOptions
                {
                    PollDelay = pollDelay,
                    PacketArrivalSignal = arrivalSignals?[index],
                    OnBatchCompleted = onBatchCompleted is null ? null : () => onBatchCompleted(binding.Adapter.RuntimeHandle),
                    OnTransientRetry = onAdapterTransientRetry is null ? null : (nativeError, attempt) => onAdapterTransientRetry(binding.Adapter, nativeError, attempt),
                    OnDegraded = nativeError => OnPumpDegraded(binding.Adapter, nativeError),
                }))];
    }

    /// <summary>How many adapter pumps exited through the degraded path (telemetry).</summary>
    internal long DegradedAdapterCount => Interlocked.Read(ref _degradedAdapterCount);

    /// <summary>
    /// Live pump counts for the heartbeat: running excludes pumps that exited through the degraded
    /// path, because a degraded pump never returns to its loop. Both reads are lock-free monotonic
    /// counters — the value is observational telemetry only.
    /// </summary>
    internal CapturePumpState PumpState
    {
        get
        {
            var degraded = (int)Interlocked.Read(ref _degradedAdapterCount);
            return new CapturePumpState(_pumps.Length - degraded, degraded);
        }
    }

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

    public ValueTask DisposeAsync()
    {
        // The one-shot claim owns the teardown and every caller joins the drain. The pumps are
        // disposed before the drain on purpose: a pump that degrades while it is being disposed is
        // still admitted by the scope, then joined by the drain, so its forward is awaited rather
        // than orphaned.
        return Interlocked.Exchange(ref _disposeStarted, 1) != 0
            ? new ValueTask(_scope.DrainAsync())
            : new ValueTask(DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        foreach (var pump in _pumps)
        {
            await pump.DisposeAsync().ConfigureAwait(false);
        }

        // Only after every pump has stopped: a pump parked in its arrival wait must never observe a
        // disposed handle. Each signal releases its driver registration before closing the event.
        if (_arrivalSignals is not null)
        {
            foreach (var signal in _arrivalSignals) signal?.Dispose();
        }

        await _scope.DrainAsync().ConfigureAwait(false);
    }

    private static async Task RunPumpAsync(NdisCapturePump pump, CancellationTokenSource linked)
    {
        try
        {
            await pump.RunAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Graceful shutdown: the pump is stopping because the run is being cancelled.
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
        if (_onAdapterDegraded is null) return;
        // Tracked child: the degraded pump has already exited its loop, so the forward cannot delay
        // it; the scope's drain joins the forward instead of letting it outlive the loop.
        _scope.Run(
            _ => ForwardDegradationAsync(_onAdapterDegraded, adapter, nativeError),
            "capture.degrade-forward");
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
