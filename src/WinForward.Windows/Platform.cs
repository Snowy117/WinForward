using System.Runtime.Versioning;
using WinForward.Core;

namespace WinForward.Windows;

public sealed record PlatformDiagnostic(string Code, string Message);

public static class PlatformRequirements
{
    public static bool TryCheck(out IReadOnlyList<PlatformDiagnostic> diagnostics)
    {
        var errors = new List<PlatformDiagnostic>();
        if (!OperatingSystem.IsWindows()) errors.Add(new("unsupported_os", "WinForward requires Windows."));
        if (OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19045)) errors.Add(new("unsupported_os_version", "WinForward requires Windows 10 22H2 or later."));
        if (OperatingSystem.IsWindows() && !Environment.Is64BitOperatingSystem) errors.Add(new("unsupported_architecture", "WinForward requires a 64-bit Windows operating system."));
        if (OperatingSystem.IsWindows() && !IsElevated()) errors.Add(new("administrator_required", "Run WinForward from an elevated administrator console."));
        diagnostics = errors;
        return errors.Count == 0;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}

public sealed record WindowsAdapter(
    string StableId,
    string FriendlyName,
    string InternalName,
    nint RuntimeHandle,
    long Generation);

public static class AdapterSelector
{
    public static bool Matches(WindowsAdapter adapter, string? stableId, string? friendlyName) =>
        (stableId is null || string.Equals(adapter.StableId, stableId, StringComparison.OrdinalIgnoreCase)) &&
        (friendlyName is null || string.Equals(adapter.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));

    public static bool TryResolve(IEnumerable<WindowsAdapter> adapters, string? stableId, string? friendlyName, out WindowsAdapter? resolved, out string? error)
    {
        var matches = adapters.Where(adapter => Matches(adapter, stableId, friendlyName)).ToArray();
        if (matches.Length == 1)
        {
            resolved = matches[0];
            error = null;
            return true;
        }

        resolved = null;
        error = matches.Length == 0 ? "No adapter matches the configured selector." : "Adapter selector is ambiguous.";
        return false;
    }
}

public readonly record struct ProcessIdentity(uint ProcessId, DateTime CreationTimeUtc, string? Name, string? FullPath);

public interface IProcessAttributor
{
    ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken cancellationToken);
}

public sealed class UnsupportedProcessAttributor : IProcessAttributor
{
    public ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken cancellationToken) => ValueTask.FromResult<ProcessIdentity?>(null);
}
