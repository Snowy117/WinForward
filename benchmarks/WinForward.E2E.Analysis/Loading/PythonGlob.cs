using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Loading;

/// <summary>
/// The three patterns the loader expands inside a directory, with the reference's own glob rules:
/// matching is case-sensitive, a name that starts with a dot is an ordinary name, and the result is
/// sorted by path.
/// </summary>
/// <remarks>
/// <para><b>A wildcard does not hide a dot.</b> The shell, <see cref="System.IO.Directory"/>'s search
/// patterns and <c>glob.glob</c> all refuse to match a name that starts with one; <c>Path.glob</c>,
/// which is what the reference calls, does not — so a hidden arm file is an arm, and a hidden
/// <c>*ledger*.jsonl</c> is a ledger.</para>
/// <para><b>A pattern matches entries, not files.</b> <c>Path.glob</c> yields directories as readily as
/// files, and the reference filters them only where it checks <c>is_file()</c>: never for
/// <c>*.jsonl</c> — a directory named <c>sub.jsonl</c> is an arm that published nothing — and once for
/// <c>config*</c>. Expanding the name pattern over
/// <see cref="Directory.EnumerateFileSystemEntries(string)"/> is therefore the closer translation than
/// <see cref="Directory.EnumerateFiles(string, string)"/>.</para>
/// </remarks>
internal static class PythonGlob
{
    private const string JsonlSuffix = ".jsonl";

    private const string LedgerMarker = "ledger";

    private const string ConfigPrefix = "config";

    /// <summary>Expands <c>*.jsonl</c>: every arm entry of a run, in path order.</summary>
    internal static List<string> ArmFiles(string directory) =>
        Expand(directory, name => name.EndsWith(JsonlSuffix, StringComparison.Ordinal));

    /// <summary>Expands <c>config*</c>: the row's published configuration files, in path order.</summary>
    internal static List<string> ConfigFiles(string directory) =>
        [.. Expand(directory, name => name.StartsWith(ConfigPrefix, StringComparison.Ordinal)).Where(File.Exists)];

    /// <summary>Expands <c>*ledger*.jsonl</c>: every ledger file beside the raw directory, in path order.</summary>
    internal static List<string> LedgerFiles(string directory) =>
        [.. Expand(directory, name => name.EndsWith(JsonlSuffix, StringComparison.Ordinal)
            && name.Contains(LedgerMarker, StringComparison.Ordinal)).Where(File.Exists)];

    private static List<string> Expand(string directory, Func<string, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(matches);

        var found = new List<string>();
        if (!Directory.Exists(directory))
        {
            return found;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(path);
            if (matches(name))
            {
                found.Add(PosixPathText.Join(directory, name));
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }
}
