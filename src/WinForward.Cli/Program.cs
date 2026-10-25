using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WinForward.Cli.Backends;
using WinForward.Cli.Logging;
using WinForward.Configuration;
using WinForward.NdisApi;
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
            if (loaded.Validated.BackendDriver.Provider != BackendProvider.WinpkFilter)
            {
                // Refused before a backend opens NDISAPI: a Kernel-provider run must never fall
                // back to WinpkFilter's capture path and pretend the selection was honoured.
                StartupLog.KernelBackendUnavailable(logger);
                return 1;
            }
            await using var backend = new WinpkFilterBackend(loaded.Validated, loggerFactory, logger);
            return await RunBackendUntilCancelledAsync(backend).ConfigureAwait(false);
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
        StartupLog.BackendSelected(logger, configuration.BackendDriver.ProviderName, configuration.BackendDriver.DriverServiceName);

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

    /// <summary>
    /// Hosts one backend run under the console-cancel lifetime: the handler is subscribed for the
    /// whole call and removed before the source is disposed, so the backend's token readers can
    /// never observe a disposed source. The backend concludes the exit code and emits the
    /// interception-lifecycle events from inside its own run; this method only returns what it
    /// concluded.
    /// </summary>
#pragma warning disable CA1859 // The parameter stays IInterceptionBackend: it is the composition seam the provider selection dispatches through, and narrowing it to today's only implementation would make every future backend call site a rewrite.
    private static async Task<int> RunBackendUntilCancelledAsync(IInterceptionBackend backend)
#pragma warning restore CA1859
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
            return await backend.RunAsync(shutdown.Token).ConfigureAwait(false);
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
        Console.WriteLine($"Provider: {loaded.Validated.BackendDriver.ProviderName}");
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
