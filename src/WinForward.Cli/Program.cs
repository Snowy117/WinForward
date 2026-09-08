using System.Runtime.Versioning;
using System.Text.Json;
using WinForward.Configuration;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
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
            await Console.Out.WriteLineAsync("WinForward commands: validate --config <path>, adapters, run --config <path>").ConfigureAwait(false);
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
            if (configPath is null)
            {
                await Console.Error.WriteLineAsync("run requires --config <path>.").ConfigureAwait(false);
                return 2;
            }
            if (!TryLoadConfig(configPath, out var configuration)) return 1;
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
            return await RunCaptureAsync(configuration!).ConfigureAwait(false);
        }

        await Console.Error.WriteLineAsync($"Unknown command '{args[0]}'.").ConfigureAwait(false);
        return 2;
    }

    private static bool TryLoadConfig(string path, out ValidatedConfiguration? configuration)
    {
        configuration = null;
        try
        {
            if (!ConfigurationLoader.TryParse(File.ReadAllText(path), out var dto, out var parseErrors) || dto is null)
            {
                PrintDiagnostics(parseErrors);
                return false;
            }
            if (!ConfigurationLoader.TryValidate(dto, out configuration, out var validationErrors))
            {
                PrintDiagnostics(validationErrors);
                return false;
            }
            return true;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Cannot read configuration: {exception.Message}");
            return false;
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
    private static async Task<int> RunCaptureAsync(ValidatedConfiguration configuration)
    {
        var logger = new ConsoleRuntimeLogger(configuration.LogLevel);
        try
        {
            logger.Info($"Runtime log level: {configuration.LogLevel.ToString().ToLowerInvariant()}.");
            foreach (var warning in configuration.Warnings)
            {
                logger.Warn($"Configuration warning: {warning}");
            }
            return await RunInterceptionAsync(configuration, logger).ConfigureAwait(false);
        }
        catch (DllNotFoundException)
        {
            logger.Error("NDISAPI unavailable: ndisapi.dll was not found in the application directory.");
            return 1;
        }
        catch (EntryPointNotFoundException)
        {
            logger.Error("NDISAPI unavailable: ndisapi.dll is missing a required export.");
            return 1;
        }
        catch (TypeLoadException)
        {
            logger.Error("NDISAPI ABI mismatch: the native library is incompatible with this build.");
            return 1;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            logger.Error($"NDISAPI driver error: {exception.Message}");
            return 1;
        }
        catch (Exception exception)
        {
            logger.Error($"Startup failed: {exception.Message}");
            return 3;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunInterceptionAsync(ValidatedConfiguration configuration, IRuntimeLogger logger)
    {
        using var driver = NdisApiDriver.Open();
        // Startup pre-flight for the exit-code surface (exit 1 selector errors, exit 3 empty
        // enumeration): the layered capture runner re-enumerates and owns the live scope,
        // repeating this resolution fail-closed for generation 0 (design §3.5/§3.6).
        var adapters = EnumerateAdapters(driver);
        if (adapters.Count == 0)
        {
            logger.Error("No MSTCP-bound adapters are available to capture.");
            return 3;
        }
        if (!CaptureAdapterScopeResolver.TryResolve(adapters, configuration.Policy, out var scope, out var scopeErrors))
        {
            foreach (var error in scopeErrors) logger.Error(error);
            return 1;
        }
        logger.Info($"Capture scope: {scope.Count} adapter(s) in tunnel mode.");

        return await RunCaptureLoopAsync(configuration, driver, logger).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunCaptureLoopAsync(ValidatedConfiguration configuration, NdisApiDriver driver, IRuntimeLogger logger)
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
                logger.Warn("High-resolution timer resolution was not applied; the empty-queue poll granularity stays at about 15.6 ms instead of about 1 ms.");
            }

            // The durable layer survives every adapter-list refresh; the runner disposes it exactly
            // once after the final generation (design §3.6). The local finally only covers failures
            // around the runner itself — bundle disposal is single-flight, so it never runs twice.
            var bundle = await DurableCaptureBundle.CreateAsync(configuration, reinjector, selfTraffic, logger).ConfigureAwait(false);
            try
            {
                // The degraded forwarder feeds error 87 into the runner's refresh channel (R3); the
                // factory logs adapter.degraded and restores the adapter's mode through its own
                // runtime. The closure dereferences the runner only while a generation runs, after
                // the reference below is assigned.
                LayeredCaptureRunner? runnerRef = null;
                var retryLogGate = new AdapterTransientRetryLogGate(logger);
                using var watcher = new NdisAdapterListWatcher(driver);
                var runner = new LayeredCaptureRunner(
                    new NdisAdapterEnumerationProvider(driver),
                    new NdisCaptureGenerationFactory(
                        driver,
                        new CapturePacketProcessor(bundle.Dispatcher, logger, bundle.Executor.FlushPendingPasses),
                        logger,
                        onAdapterDegraded: (adapter, nativeError) =>
                        {
                            runnerRef!.SignalDegraded(adapter, nativeError);
                            return ValueTask.CompletedTask;
                        },
                        onAdapterTransientRetry: (adapter, nativeError, attempt) => retryLogGate.Log(adapter.StableId, adapter.FriendlyName, nativeError, attempt)),
                    watcher,
                    configuration.Policy,
                    logger,
                    disposeDurableAsync: _ => bundle.DisposeAsync(),
                    onScopeInstalled: bundle.OnScopeInstalled);
                runnerRef = runner;

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

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunUntilCancelledAsync(LayeredCaptureRunner runner, IRuntimeLogger logger)
    {
        using var shutdown = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            logger.Info("Interception started. Press Ctrl+C to stop.");
            await runner.RunAsync(shutdown.Token).ConfigureAwait(false);
            logger.Info("WinForward stopped cleanly.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            logger.Info("Shutdown requested; restoring adapter modes.");
            return 0;
        }
        catch (Exception exception)
        {
            logger.Error($"Runtime failure: {exception.Message}");
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
        var inventory = new WindowsAdapterInventory(() => driver.GetAdapters()
            .Select(adapter => (adapter.InternalName, adapter.RuntimeHandle, adapter.MacAddress, adapter.Mtu))
            .ToArray());
        return inventory.GetCurrentAdapters();
    }

    private static int Validate(string[] args)
    {
        var path = FindOption(args, "--config");
        if (path is null)
        {
            Console.Error.WriteLine("validate requires --config <path>.");
            return 2;
        }

        try
        {
            if (!TryLoadConfig(path, out var configuration)) return 1;
            Console.WriteLine($"Configuration is valid. tcpFlowCapacity: {configuration!.TcpFlowCapacity}");
            foreach (var warning in configuration.Warnings)
            {
                Console.Error.WriteLine($"warning {warning}");
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Cannot read configuration: {exception.Message}");
            return 1;
        }
    }

    private static string? FindOption(string[] args, string option)
    {
        var index = Array.FindIndex(args, arg => arg.Equals(option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void PrintDiagnostics(IEnumerable<ConfigDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics) Console.Error.WriteLine(diagnostic);
    }
}
