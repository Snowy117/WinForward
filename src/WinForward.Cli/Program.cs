using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WinForward.Cli.Logging;
using WinForward.Configuration;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.Logging;
using WinForward.Windows;

namespace WinForward.Cli;

internal static class Program
{
    // Exit-code contract:
    //   0  clean shutdown / success
    //   1  configuration or validation error, adapter-selector resolution error, or NDISAPI/driver access failure
    //   2  usage error, unknown command, or unsupported platform
    //   3  fatal runtime failure during capture (adapter modes are restored before exit)

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            await Console.Out.WriteLineAsync("WinForward commands: validate [--config <path>], adapters, run [--config <path>]").ConfigureAwait(false);
            return 0;
        }

        if (args[0].Equals("validate", StringComparison.OrdinalIgnoreCase)) return Validate(args);
        if (args[0].Equals("adapters", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsWindows())
            {
                await Console.Error.WriteLineAsync("Adapter discovery requires Windows 10 22H2 or later.").ConfigureAwait(false);
                return 2;
            }
            return ListAdapters();
        }
        if (args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            var configPath = FindOption(args, "--config");
            var exitCode = TryLoadConfiguration("run", configPath, out var loaded);
            if (exitCode != 0) return exitCode;
            if (!OperatingSystem.IsWindows())
            {
                await Console.Error.WriteLineAsync("run requires Windows 10 22H2 or later.").ConfigureAwait(false);
                return 2;
            }
            if (!PlatformRequirements.TryCheck(out var platformErrors))
            {
                foreach (var error in platformErrors)
                {
                    await Console.Error.WriteLineAsync($"{error.Code}: {error.Message}").ConfigureAwait(false);
                }
                return 2;
            }
            using var loggerFactory = TryCreateLoggerFactory(loaded!.Configuration, out var loggingError);
            if (loggerFactory is null)
            {
                await Console.Error.WriteLineAsync(loggingError).ConfigureAwait(false);
                return 1;
            }
            return await RunCaptureAsync(loaded, loggerFactory).ConfigureAwait(false);
        }

        await Console.Error.WriteLineAsync($"Unknown command '{args[0]}'.").ConfigureAwait(false);
        return 2;
    }

    /// <summary>
    /// Loads the effective configuration for one command, reporting its own diagnostics. A missing
    /// source is the command's usage error; everything else is a configuration error.
    /// </summary>
    private static int TryLoadConfiguration(string command, string? configPath, out LoadedConfiguration? loaded)
    {
        var outcome = ConfigurationLoader.TryLoad(AppContext.BaseDirectory, configPath, out loaded, out var diagnostics);
        return outcome switch
        {
            ConfigurationLoadOutcome.NoSource => MissingSource(command),
            ConfigurationLoadOutcome.Invalid => InvalidConfiguration(diagnostics),
            _ => RuntimeLogging.TryValidate(loaded!.Configuration, out var loggingDiagnostics) ? 0 : InvalidConfiguration(loggingDiagnostics),
        };
    }

    /// <summary>A missing source is a usage error: the command cannot run unconfigured.</summary>
    private static int MissingSource(string command)
    {
        Console.Error.WriteLine($"{command} requires {ConfigurationLoader.AppSettingsFileName} beside the executable or --config <path>.");
        return 2;
    }

    /// <summary>Diagnostics go to stderr, and the command reports one configuration error.</summary>
    private static int InvalidConfiguration(IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        PrintDiagnostics(diagnostics);
        return 1;
    }

    /// <summary>
    /// Builds the logger factory a run would build. The logging section is bound by the framework,
    /// so a value it cannot apply — an unknown log level, a malformed formatter option — surfaces
    /// here rather than as an unhandled exception, and <c>validate</c> can report it before a run.
    /// </summary>
    private static ILoggerFactory? TryCreateLoggerFactory(IConfiguration configuration, out string? error)
    {
        try
        {
            error = null;
            return RuntimeLogging.CreateLoggerFactory(configuration);
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int ListAdapters()
    {
        if (!PlatformRequirements.TryCheck(out var platformErrors))
        {
            foreach (var error in platformErrors) Console.Error.WriteLine($"{error.Code}: {error.Message}");
            return 2;
        }

        try
        {
            using var driver = NdisApiDriver.Open();
            foreach (var adapter in EnumerateAdapters(driver))
            {
                Console.WriteLine($"{adapter.StableId}\t{adapter.FriendlyName}\t{adapter.InternalName}\tHANDLE=0x{adapter.RuntimeHandle.ToInt64():X}");
            }
            return 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception or TypeLoadException)
        {
            Console.Error.WriteLine($"NDISAPI unavailable: {exception.Message}");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunCaptureAsync(LoadedConfiguration loaded, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(Program).FullName!);
        try
        {
            ReportResolvedRun(loaded, logger);
            foreach (var warning in loaded.Validated.Warnings) StartupLog.ConfigurationWarning(logger, warning.ToString());
            return await RunInterceptionAsync(loaded.Validated, loggerFactory, logger).ConfigureAwait(false);
        }
        catch (DllNotFoundException)
        {
            StartupLog.NdisApiLibraryMissing(logger);
            return 1;
        }
        catch (EntryPointNotFoundException)
        {
            StartupLog.NdisApiExportMissing(logger);
            return 1;
        }
        catch (TypeLoadException)
        {
            StartupLog.NdisApiAbiMismatch(logger);
            return 1;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            StartupLog.NdisApiDriverError(logger, exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            StartupLog.StartupFailed(logger, exception);
            return 3;
        }
    }

    private static void ReportResolvedRun(LoadedConfiguration loaded, ILogger logger)
    {
        var configuration = loaded.Validated;
        var logLevel = RuntimeLogging.ResolveLogLevel(loaded.Configuration).ToString();
        var formatter = RuntimeLogging.ResolveFormatterName(loaded.Configuration, Console.IsErrorRedirected);
        var sources = DescribeSources(loaded.Sources);
        var hostRules = configuration.Policy.HostRules.Count;
        var forwardedRules = configuration.Policy.ForwardedRules.Count;
        var relayBufferKiB = configuration.UdpRelayReceiveBufferBytes / 1024;
        var idleSeconds = (int)configuration.UdpSessionIdleTimeout.TotalSeconds;
        var processPaths = configuration.IncludeProcessPathInLogs ? "included" : "withheld";
        StartupLog.RunResolved(
            logger,
            sources,
            logLevel,
            formatter,
            configuration.Targets.Count,
            hostRules,
            forwardedRules,
            configuration.TcpFlowCapacity,
            configuration.UdpSessionCapacity,
            relayBufferKiB,
            idleSeconds,
            processPaths);

        foreach (var target in configuration.Targets.Values.OrderBy(static target => target.Name, StringComparer.Ordinal))
        {
            if (target.Socks5 is { } server)
            {
                var endpoint = $"{server.Host}:{server.Port}";
                var udpTransport = server.UdpOverTcp ? "UDP-over-TCP" : "the native relay";
                var authentication = server.Username is null ? "none" : "configured";
                StartupLog.Socks5TargetAvailable(logger, server.Name, endpoint, udpTransport, authentication);
            }
            else if (target.Local is { } local)
            {
                var endpoint = local.Endpoint.ToString();
                StartupLog.LocalTargetAvailable(logger, local.Name, endpoint);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunInterceptionAsync(ValidatedConfiguration configuration, ILoggerFactory loggerFactory, ILogger logger)
    {
        using var driver = NdisApiDriver.Open();
        // Startup pre-flight for the exit-code surface (exit 1 selector errors, exit 3 empty
        // enumeration): the layered capture runner re-enumerates and owns the live scope,
        // repeating this resolution fail-closed for generation 0 (design §3.5/§3.6).
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
        // (its forced-refresh trigger) — task 09-17 R1-B.
        var healthMonitor = new InterceptionHealthMonitor(loggerFactory.CreateLogger<InterceptionHealthMonitor>());
        return await RunCaptureLoopAsync(configuration, driver, loggerFactory, logger, healthMonitor).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunCaptureLoopAsync(ValidatedConfiguration configuration, NdisApiDriver driver, ILoggerFactory loggerFactory, ILogger logger, InterceptionHealthMonitor healthMonitor)
    {
        var selfTraffic = new SelfTrafficRegistry();
        var reinjector = new NdisPacketReinjector(driver);
        try
        {
            // Scoped 1 ms timer resolution for the whole run: the pump's empty-queue poll delay
            // rounds up to ~15.6 ms at the default resolution (task 08-28 R6). Declared first so
            // the scope outlives the capture runtime and its teardown-time reinjections.
            using var timerScope = new HighResolutionTimerScope();
            if (!timerScope.IsEnabled)
            {
                StartupLog.HighResolutionTimerUnavailable(logger);
            }

            WireFramePoolDiagnostics();

            // The durable layer survives every adapter-list refresh; the runner disposes it exactly
            // once after the final generation (design §3.6). The local finally only covers failures
            // around the runner itself — bundle disposal is single-flight, so it never runs twice.
            var bundle = await DurableCaptureBundle.CreateAsync(configuration, reinjector, selfTraffic, loggerFactory, healthSignal: healthMonitor).ConfigureAwait(false);
            try
            {
                // The degraded forwarder feeds error 87 into the runner's refresh channel (R3); the
                // callback closure dereferences the runner only while a generation runs, after the
                // reference below is assigned.
                LayeredCaptureRunner? runnerRef = null;
                using var watcher = new NdisAdapterListWatcher(driver);
                var runner = CreateCaptureRunner(
                    configuration, driver, bundle, loggerFactory.CreateLogger<LayeredCaptureRunner>(), healthMonitor, watcher,
                    (adapter, nativeError) =>
                    {
                        // ReSharper disable once AccessToModifiedClosure // One-shot wiring: runnerRef is assigned before RunAsync starts, and the callback can fire only from a generation created inside RunAsync, so every read already sees the assigned runner.
                        runnerRef!.SignalDegraded(adapter, nativeError);
                        return ValueTask.CompletedTask;
                    });
                runnerRef = runner;

                // Observational periodic summary (task 09-17 R2.3); the using disposes it before
                // the bundle's finally, so its last ticks never observe coordinator teardown.
                await using var heartbeat = StartHeartbeat(bundle, runner, healthMonitor, loggerFactory.CreateLogger<RuntimeHeartbeat>());

                return await RunUntilCancelledAsync(runner, logger).ConfigureAwait(false);
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
    /// Registers the shared native frame pool with the diagnostics counter registry (task
    /// 09-18 M1): the heartbeat then reports the pool's rent and return deltas and its occupancy
    /// (rents minus returns) in every periodic summary. The sink is set once, before any capture
    /// pump can rent, and only ever increments counters — it cannot throw and never touches
    /// packet disposition. The sink fires per injected packet on the hot path, so it must stay
    /// allocation-free: the counter key strings are built once here (the <c>RecordPoolRent</c>
    /// helpers rebuild their keys per call, which would allocate on every rent/return) and the
    /// sink increments the pre-created counter boxes through the cached references.
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
    /// error 87 into its refresh channel (R3); transient-read retries ride the shared log gate.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static LayeredCaptureRunner CreateCaptureRunner(
        ValidatedConfiguration configuration,
        NdisApiDriver driver,
        DurableCaptureBundle bundle,
        ILogger logger,
        InterceptionHealthMonitor healthMonitor,
        NdisAdapterListWatcher watcher,
        Func<WindowsAdapter, int, ValueTask> onAdapterDegraded)
    {
        var retryLogGate = new AdapterTransientRetryLogGate(logger);
        var generations = new NdisCaptureGenerationFactory(
            driver,
            new CapturePacketProcessor(bundle.Dispatcher, bundle.AdapterSlots, logger, bundle.FlushPendingInjections),
            bundle.AdapterSlots,
            logger,
            onAdapterDegraded: onAdapterDegraded,
            onAdapterTransientRetry: (adapter, nativeError, attempt) => retryLogGate.Log(adapter.StableId, adapter.FriendlyName, nativeError, attempt))
        {
            WakeRegistry = bundle.WakeRegistry,
        };
        return new LayeredCaptureRunner(
            new NdisAdapterEnumerationProvider(driver, logger),
            generations,
            watcher,
            configuration.Policy,
            logger,
            disposeDurableAsync: _ => bundle.DisposeAsync(),
            onScopeInstalled: bundle.OnScopeInstalled, interceptionHealthMonitor: healthMonitor);
    }

    /// <summary>
    /// Starts the periodic <c>runner.heartbeat</c> info summary (task 09-17 R2.3): flow/TCP/UDP
    /// usage against capacities, the current generation's pump counts, interception-health state,
    /// and per-counter deltas since the previous heartbeat. Purely observational — a fault while
    /// gathering usage is logged and retried on the next tick.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static RuntimeHeartbeat StartHeartbeat(DurableCaptureBundle bundle, LayeredCaptureRunner runner, InterceptionHealthMonitor healthMonitor, ILogger logger)
    {
        var heartbeat = new RuntimeHeartbeat(
            logger,
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

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunUntilCancelledAsync(LayeredCaptureRunner runner, ILogger logger)
    {
        using var shutdown = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            // ReSharper disable once AccessToDisposedClosure // OnCancel is reachable only through the Console.CancelKeyPress subscription, which the finally below removes before this method's using scope disposes shutdown; Cancel() can never observe a disposed source on that path.
            shutdown.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            StartupLog.InterceptionStarted(logger);
            await runner.RunAsync(shutdown.Token).ConfigureAwait(false);
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
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<WindowsAdapter> EnumerateAdapters(NdisApiDriver driver)
    {
        var inventory = new WindowsAdapterInventory(() => [.. driver.GetAdapters().Select(adapter => (adapter.InternalName, adapter.RuntimeHandle, adapter.MacAddress, adapter.Mtu))]);
        return inventory.GetCurrentAdapters();
    }

    private static int Validate(string[] args)
    {
        var exitCode = TryLoadConfiguration("validate", FindOption(args, "--config"), out var loaded);
        if (exitCode != 0) return exitCode;

        // The logging section is applied by the framework, so the only way to report a value it
        // cannot apply is to build the factory a run would build. Nothing is written: no record is
        // logged, so stdout keeps its confirmation line and stderr its warnings.
        using var loggerFactory = TryCreateLoggerFactory(loaded!.Configuration, out var loggingError);
        if (loggerFactory is null)
        {
            Console.Error.WriteLine(loggingError);
            return 1;
        }

        PrintSources(loaded.Sources);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Configuration is valid. tcpFlowCapacity: {loaded.Validated.TcpFlowCapacity}"));
        foreach (var warning in loaded.Validated.Warnings)
        {
            Console.Error.WriteLine($"warning {warning}");
        }
        return 0;
    }

    private static void PrintSources(IReadOnlyList<ConfigurationSource> sources)
    {
        Console.WriteLine("Configuration sources (applied in order, later wins):");
        foreach (var source in sources) Console.WriteLine($"  {source.Name}: {source.Path}");
    }

    private static string DescribeSources(IReadOnlyList<ConfigurationSource> sources) =>
        string.Join(" -> ", sources.Select(static source => $"{source.Name} ({source.Path})"));

    private static string? FindOption(string[] args, string option)
    {
        var index = Array.FindIndex(args, arg => arg.Equals(option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && args[index + 1].Length > 0 ? args[index + 1] : null;
    }

    private static void PrintDiagnostics(IEnumerable<ConfigDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics) Console.Error.WriteLine(diagnostic);
    }
}
