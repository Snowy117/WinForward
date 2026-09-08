using System.Runtime.Versioning;

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
