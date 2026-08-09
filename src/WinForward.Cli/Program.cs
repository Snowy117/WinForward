using System.Runtime.Versioning;
using System.Text.Json;
using WinForward.Configuration;
using WinForward.NdisApi;
using WinForward.Runtime;
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
        var logger = new ConsoleRuntimeLogger();
        try
        {
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
        var adapters = EnumerateAdapters(driver);
        if (adapters.Count == 0)
        {
            logger.Error("No MSTCP-bound adapters are available to capture.");
            return 3;
        }

        // ADAPTER-LIST CHANGE SEAM (deferred): the runtime resolves the capture scope once against
        // this startup snapshot. SetAdapterListChangeEvent-driven re-resolution and fail-closed
        // handling of a configured adapter disappearing after startup are a later milestone; the
        // resolver's TryResolve is the single point where a re-resolved snapshot would be injected.
        if (!CaptureAdapterScopeResolver.TryResolve(adapters, configuration.Policy, out var scope, out var scopeErrors))
        {
            foreach (var error in scopeErrors) logger.Error(error);
            return 1;
        }
        logger.Info($"Capture scope: {scope.Count} adapter(s) in tunnel mode.");

        return await RunCaptureLoopAsync(configuration, driver, scope, logger).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunCaptureLoopAsync(ValidatedConfiguration configuration, NdisApiDriver driver, IReadOnlyList<WindowsAdapter> scope, IRuntimeLogger logger)
    {
        var selfTraffic = new SelfTrafficRegistry();
        var reinjector = new NdisPacketReinjector(driver);
        await using var tcpCoordinator = new TcpProxyCoordinator(
            new TcpRedirectListenerFactory(),
            new TcpProxyRelayFactory(selfTraffic),
            new TcpRedirectInjector(reinjector),
            new TcpRedirectTable(),
            selfTraffic,
            logger);

        // UDP relay: the response reinjector rebuilds client-bound frames on the capture scope's first
        // adapter (host flows). Its MAC is the NDISAPI CurrentAddress of that adapter.
        await using var udpCoordinator = CreateUdpCoordinator(driver, scope, reinjector, selfTraffic, logger);

        var executor = new NdisPacketActionExecutor(reinjector, logger, tcpCoordinator, udpCoordinator);
        var dispatcher = new FlowDispatcher(
            configuration, selfTraffic, executor, new WindowsProcessAttributor(),
            reverseHandler: tcpCoordinator.HandleReverseIfApplicableAsync);
        var processor = new CapturePacketProcessor(dispatcher);
        var modeController = new NdisAdapterModeController(driver, scope);
        var captureLoop = new MultiAdapterCaptureLoop(driver, scope, processor);
        await using var runtime = new TransactionalCaptureRuntime(modeController, captureLoop);
        await using var idleExpirySweeper = new IdleExpirySweeper(dispatcher, tcpCoordinator, udpCoordinator);

        using var shutdown = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            idleExpirySweeper.Start();
            logger.Info("Interception started. Press Ctrl+C to stop.");
            await runtime.StartAsync(shutdown.Token).ConfigureAwait(false);
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

    /// <summary>
    /// Returns the NDISAPI CurrentAddress (MAC) of the adapter identified by
    /// <paramref name="adapterHandle"/>, or null when the adapter is not currently enumerated.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static byte[]? GetAdapterMac(NdisApiDriver driver, nint adapterHandle)
    {
        foreach (var adapter in driver.GetAdapters())
        {
            if (adapter.RuntimeHandle == adapterHandle) return adapter.MacAddress;
        }
        return null;
    }

    /// <summary>
    /// Builds the UDP relay coordinator for a run. The response reinjector targets the capture
    /// scope's first adapter (host flows); when no real MAC is accessible a zero placeholder is
    /// used and the hardware pass verifies whether SendToMstcp honors the destination MAC.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static UdpProxyCoordinator CreateUdpCoordinator(NdisApiDriver driver, IReadOnlyList<WindowsAdapter> scope, IPacketReinjector reinjector, SelfTrafficRegistry selfTraffic, IRuntimeLogger logger)
    {
        var hostAdapter = scope[0];
        var localMac = GetAdapterMac(driver, hostAdapter.RuntimeHandle);
        if (localMac is null)
        {
            logger.Warn("UDP response reinjection will use a zero MAC because the adapter MAC is unavailable; verify on the target host.");
            localMac = new byte[NdisApiAbi.EthernetAddressLength];
        }
        return new UdpProxyCoordinator(
            new Socks5UdpTransportFactory(selfTraffic),
            new UdpResponseReinjector(reinjector, hostAdapter.RuntimeHandle, localMac, logger));
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
            if (!TryLoadConfig(path, out _)) return 1;
            Console.WriteLine("Configuration is valid.");
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

    private sealed class ConsoleRuntimeLogger : IRuntimeLogger
    {
        public void Info(string message) => Console.Error.WriteLine($"[info] {message}");
        public void Warn(string message) => Console.Error.WriteLine($"[warn] {message}");
        public void Error(string message) => Console.Error.WriteLine($"[error] {message}");
    }
}