namespace WinForward.Core;

/// <summary>
/// Applies the process selector semantics used by the policy layer: filename-only
/// selectors match the executable name; path selectors match the normalized full path
/// exactly and, when the path denotes a directory, also match every executable located
/// in that directory or any of its subdirectories.
/// </summary>
public static class ProcessSelectorMatcher
{
    public static bool IsMatch(IReadOnlySet<string> selectors, string? processName, string? processPath)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        foreach (var selectorValue in selectors)
        {
            var selector = selectorValue.Trim();
            if (selector.Length == 0) continue;

            if (ContainsDirectorySeparator(selector))
            {
                if (processPath is null) continue;
                var normalizedSelector = NormalizePath(selector);
                var normalizedPath = NormalizePath(processPath);
                if (string.Equals(normalizedSelector, normalizedPath, StringComparison.OrdinalIgnoreCase)) return true;
                if (IsInsideDirectory(normalizedPath, normalizedSelector)) return true;
                continue;
            }

            if (processName is not null && string.Equals(selector, processName.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            if (processPath is not null && string.Equals(selector, GetFileName(processPath), StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string NormalizePath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Trim().Replace('/', '\\');
        if (OperatingSystem.IsWindows())
        {
            try { normalized = Path.GetFullPath(normalized); }
            catch (ArgumentException) { return normalized.TrimEnd('\\'); }
        }

        return normalized.TrimEnd('\\');
    }

    private static bool ContainsDirectorySeparator(string value) => value.IndexOfAny(['/', '\\']) >= 0;

    private static bool IsInsideDirectory(string normalizedPath, string normalizedDirectory)
    {
        var prefixLength = normalizedDirectory.Length;
        return normalizedPath.Length > prefixLength
            && normalizedPath[prefixLength] == '\\'
            && normalizedPath.AsSpan(0, prefixLength).Equals(normalizedDirectory.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFileName(string value)
    {
        var normalized = value.Trim().Replace('/', '\\');
        var separator = normalized.LastIndexOf('\\');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }
}
