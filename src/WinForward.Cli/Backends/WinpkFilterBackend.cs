using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinForward.Cli.Logging;
using WinForward.Configuration;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Windows;

namespace WinForward.Cli.Backends;

/// <summary>
/// The WinpkFilter (NDISAPI) backend: it owns the driver handle, the adapter pre-flight, the
/// capture scope, the durable capture bundle, the layered capture runner, the health monitor, the
/// high-resolution timer scope, the adapter watcher, the heartbeat and the frame-pool diagnostics.
/// <see cref="RunAsync"/> holds every one of them for the whole call and releases them in a single
/// teardown there; nothing outlives it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinpkFilterBackend(ValidatedConfiguration configuration, ILoggerFactory loggerFactory, ILogger logger) : IInterceptionBackend
{
    /// <summary>
    /// The provider this class implements, not the configured selection: the CLI refuses a
    /// Kernel-provider run before constructing a backend, so the two cannot disagree, and the
    /// interface names the implementation rather than the configuration that chose it.
    /// </summary>
    public string ProviderName => BackendDriverConfiguration.WinpkFilterProviderName;

    public async ValueTask<int> RunAsync(CancellationToken cancellationToken)
    {
        using var driver = NdisApiDriver.Open();
        // Startup pre-flight for the exit-code surface (exit 1 selector errors, exit 3 empty
        // enumeration): the layered capture runner re-enumerates and owns the live scope,
        // repeating this resolution fail-closed for generation 0.
        var adapters = EnumerateAdapters(driver);
        if (adapters.Count == 0)
        {
            StartupLog.NoCaptureAdapters(logger);
            return 3;
        }
        if (!CaptureAdapterScopeResolver.TryResolve(adapters, configuration.Policy, out var scope, out var scopeErrors))
        {
            foreach (var error in scopeErrors) StartupLog.CaptureScopeResolutionFailed(logger, error);
            return 1;
        }
        StartupLog.CaptureScopePreflightResolved(logger, scope.Count);

        // Shared by the durable bundle (its failure-reporting sites) and the capture runner
        // (its forced-refresh trigger).
        var healthMonitor = new InterceptionHealthMonitor(loggerFactory.CreateLogger<InterceptionHealthMonitor>());
        return await RunCaptureLoopAsync(driver, healthMonitor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// No-op, deliberately: <see cref="RunAsync"/> owns the driver and the whole capture runtime for
    /// its own duration, so no composed resource survives the call to be released here. The seam
    /// keeps the method for a backend whose composition is wider than one call.
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<int> RunCaptureLoopAsync(NdisApiDriver driver, InterceptionHealthMonitor healthMonitor, CancellationToken cancellationToken)
    {
        var selfTraffic = new SelfTrafficRegistry();
        var reinjector = new NdisPacketReinjector(driver);
        try
        {
            // Scoped 1 ms timer resolution for the whole run: the pump's empty-queue poll delay
            // rounds up to ~15.6 ms at the default resolution. Declared first so the scope
            // outlives the capture runtime and its teardown-time reinjections.
            using var timerScope = new HighResolutionTimerScope();
            if (!timerScope.IsEnabled)
            {
                StartupLog.HighResolutionTimerUnavailable(logger);
            }

            WireFramePoolDiagnostics();

            // The durable layer survives every adapter-list refresh; the runner disposes it exactly
            // once after the final generation. The local finally only covers failures around the
            // runner itself — bundle disposal is single-flight, so it never runs twice.
            // The driver's static-filter table is passed in even when the feature is off: the bundle
            // only reaches it when KernelPassCache is enabled, and the feature-off path touches no table.
            var bundle = await DurableCaptureBundle.CreateAsync(configuration, reinjector, selfTraffic, loggerFactory, healthSignal: healthMonitor, kernelFilterTable: driver).ConfigureAwait(false);
            try
            {
                // The degraded forwarder feeds error 87 into the runner's refresh channel; the
                // callback closure dereferences the runner only while a generation runs, after the
                // reference below is assigned.
                LayeredCaptureRunner? runnerRef = null;
                using var watcher = new NdisAdapterListWatcher(driver);
                var runner = CreateCaptureRunner(
                    driver, bundle, healthMonitor, watcher,
                    (adapter, nativeError) =>
                    {
                        // ReSharper disable once AccessToModifiedClosure // One-shot wiring: runnerRef is assigned before RunAsync starts, and the callback can fire only from a generation created inside RunAsync, so every read already sees the assigned runner.
                        runnerRef!.SignalDegraded(adapter, nativeError);
                        return ValueTask.CompletedTask;
                    });
                runnerRef = runner;

                // Observational periodic summary; the using disposes it before the bundle's
                // finally, so its last ticks never observe coordinator teardown.
                await using var heartbeat = StartHeartbeat(bundle, runner, healthMonitor);

                return await RunUntilCancelledAsync(runner, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await bundle.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // The shared injection buffer pool is drained after the capture runtime (and the
            // coordinator teardown injections it performs) has completed, releasing every idle
            // native buffer back to the heap before the driver handle closes.
            NdisPacketBufferPool.Shared.Dispose();
        }
    }

    /// <summary>
    /// Registers the shared native frame pool with the diagnostics counter registry: the heartbeat
    /// then reports the pool's rent and return deltas and its occupancy. The sink is set once,
    /// before any capture pump can rent, and only ever increments counters — it cannot throw and
    /// never touches packet disposition. It fires per injected packet on the hot path, so it must
    /// stay allocation-free: the counter key strings are built once here (the <c>RecordPoolRent</c>
    /// helpers rebuild their keys per call, which would allocate on every rent/return) and the sink
    /// increments the pre-created counter boxes through the cached references.
    /// </summary>
    private static void WireFramePoolDiagnostics()
    {
        const string framePoolName = "ndis.frame";
        var counters = RuntimeCounters.Shared;
        counters.RegisterPool(framePoolName);
        var rentKey = RuntimeCounters.PoolRentedKey(framePoolName);
        var returnKey = RuntimeCounters.PoolReturnedKey(framePoolName);
        NdisPacketBufferPool.Shared.AccountingSink = rented => counters.Increment(rented ? rentKey : returnKey);
    }

    /// <summary>
    /// Composes the layered capture runner over the durable bundle's shared packet processor: the
    /// generation factory logs <c>adapter.degraded</c>, restores the degraded adapter's mode through
    /// its own runtime, and forwards to <paramref name="onAdapterDegraded"/> so the runner can feed
    /// error 87 into its refresh channel; transient-read retries ride the shared log gate.
    /// </summary>
    private LayeredCaptureRunner CreateCaptureRunner(
        NdisApiDriver driver,
        DurableCaptureBundle bundle,
        InterceptionHealthMonitor healthMonitor,
        NdisAdapterListWatcher watcher,
        Func<WindowsAdapter, int, ValueTask> onAdapterDegraded)
    {
        var runnerLogger = loggerFactory.CreateLogger<LayeredCaptureRunner>();
        var retryLogGate = new AdapterTransientRetryLogGate(runnerLogger);
        var generations = new NdisCaptureGenerationFactory(
            driver,
            new CapturePacketProcessor(bundle.Dispatcher, bundle.AdapterSlots, runnerLogger, bundle.FlushPendingInjections),
            bundle.AdapterSlots,
            runnerLogger,
            onAdapterDegraded: onAdapterDegraded,
            onAdapterTransientRetry: (adapter, nativeError, attempt) => retryLogGate.Log(adapter.StableId, adapter.FriendlyName, nativeError, attempt))
        {
            WakeRegistry = bundle.WakeRegistry,
        };
        return new LayeredCaptureRunner(
            new NdisAdapterEnumerationProvider(driver, runnerLogger),
            generations,
            watcher,
            configuration.Policy,
            runnerLogger,
            disposeDurableAsync: _ => bundle.DisposeAsync(),
            onScopeInstalled: bundle.OnScopeInstalled, interceptionHealthMonitor: healthMonitor);
    }

    /// <summary>
    /// Starts the periodic <c>runner.heartbeat</c> info summary: flow/TCP/UDP usage against
    /// capacities, the current generation's pump counts, interception-health state, and
    /// per-counter deltas since the previous heartbeat. Purely observational — a fault while
    /// gathering usage is logged and retried on the next tick.
    /// </summary>
    private RuntimeHeartbeat StartHeartbeat(DurableCaptureBundle bundle, LayeredCaptureRunner runner, InterceptionHealthMonitor healthMonitor)
    {
        var heartbeat = new RuntimeHeartbeat(
            loggerFactory.CreateLogger<RuntimeHeartbeat>(),
            usage: () => new RuntimeHeartbeatUsage(
                bundle.Dispatcher.FlowCount, bundle.Dispatcher.FlowCapacity,
                bundle.Tcp.SessionCount, bundle.Tcp.Capacity,
                bundle.Udp.SessionCount, bundle.Udp.Capacity,
                runner.PumpState.Running, runner.PumpState.Degraded,
                (long)bundle.Udp.SessionCount * bundle.Udp.RelayReceiveBufferBytes),
            health: healthMonitor);
        heartbeat.Start();
        return heartbeat;
    }

    private async Task<int> RunUntilCancelledAsync(LayeredCaptureRunner runner, CancellationToken cancellationToken)
    {
        try
        {
            StartupLog.InterceptionStarted(logger);
            await runner.RunAsync(cancellationToken).ConfigureAwait(false);
            StartupLog.StoppedCleanly(logger);
            return 0;
        }
        catch (OperationCanceledException)
        {
            StartupLog.ShutdownRequested(logger);
            return 0;
        }
        catch (Exception exception)
        {
            StartupLog.RuntimeFailed(logger, exception);
            return 3;
        }
    }

    private static IReadOnlyList<WindowsAdapter> EnumerateAdapters(NdisApiDriver driver)
    {
        var inventory = new WindowsAdapterInventory(() => [.. driver.GetAdapters().Select(adapter => (adapter.InternalName, adapter.RuntimeHandle, adapter.MacAddress, adapter.Mtu))]);
        return inventory.GetCurrentAdapters();
    }
}
