namespace WinForward.Core;

/// <summary>
/// Applies the exact process selector semantics used by the policy layer.
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
                if (processPath is not null && string.Equals(NormalizePath(selector), NormalizePath(processPath), StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }

            if (processName is not null && string.Equals(selector, processName.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            if (processPath is not null && string.Equals(selector, GetFileName(processPath), StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public static string NormalizePath(string value)
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

    private static string GetFileName(string value)
    {
        var normalized = value.Trim().Replace('/', '\\');
        var separator = normalized.LastIndexOf('\\');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }
}
