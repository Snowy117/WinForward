using System.Runtime.Versioning;
using System.Text.Json;
using WinForward.Configuration;
using WinForward.NdisApi;
using WinForward.Windows;

namespace WinForward.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("WinForward commands: validate --config <path>, adapters, run --config <path>");
            return 0;
        }

        if (args[0].Equals("validate", StringComparison.OrdinalIgnoreCase)) return Validate(args);
        if (args[0].Equals("adapters", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("Adapter discovery requires Windows 10 22H2 or later.");
                return 2;
            }
            return ListAdapters();
        }
        if (args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            var configPath = FindOption(args, "--config");
            if (configPath is null)
            {
                Console.Error.WriteLine("run requires --config <path>.");
                return 2;
            }
            if (!TryLoadConfig(configPath, out _)) return 1;
            if (!PlatformRequirements.TryCheck(out var platformErrors))
            {
                foreach (var error in platformErrors) Console.Error.WriteLine($"{error.Code}: {error.Message}");
                return 2;
            }
            Console.Error.WriteLine("Packet interception runtime is not initialized yet.");
            return 2;
        }

        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
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
            var ndisAdapters = driver.GetAdapters();
            var inventory = new WindowsAdapterInventory(() => ndisAdapters
                .Select(adapter => (adapter.InternalName, adapter.RuntimeHandle, adapter.MacAddress, adapter.Mtu))
                .ToArray());
            foreach (var adapter in inventory.GetCurrentAdapters())
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
}
