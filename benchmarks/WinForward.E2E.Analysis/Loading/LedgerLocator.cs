using WinForward.E2E.Analysis.Cli;

namespace WinForward.E2E.Analysis.Loading;

/// <summary>
/// Finds the campaign's ledgers, in the order §2 prints their paths and §14 prints their rows: the
/// pass directory, then <c>--raw</c>, then <c>--raw</c>'s parent, and inside each of them the four
/// names a shipped launcher writes before the <c>*ledger*.jsonl</c> files sorted by path. A path
/// seen twice is kept once, at its first position.
/// </summary>
/// <remarks>
/// A campaign runs more than one target instance — the shipped launcher starts a proxied target and
/// a separate direct-lane target, each with its own ledger — so "the ledger" is never a single file,
/// and a first-seen order that disagrees with the reference would change §2's text and §14's rows.
/// <c>--ledger</c> may be repeated and is used verbatim, filtered to the paths that exist.
/// </remarks>
internal static class LedgerLocator
{
    private static readonly string[] s_names =
        ["target-ledger.jsonl", "ledger.jsonl", "ledger-main.jsonl", "ledger-direct.jsonl"];

    private const string Pattern = "*ledger*.jsonl";

    /// <summary>The ledgers belonging to one pass, or to the whole tree when no pass is given.</summary>
    internal static IReadOnlyList<string> Locate(AnalysisOptions options, string? passDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Ledgers.Count > 0)
        {
            return [.. options.Ledgers.Where(File.Exists)];
        }

        var bases = new List<string>(3);
        if (passDirectory is not null)
        {
            bases.Add(passDirectory);
        }

        bases.Add(options.Raw);
        var parent = Path.GetDirectoryName(Path.GetFullPath(options.Raw));
        if (parent is not null)
        {
            bases.Add(parent);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<string>();
        foreach (var directory in bases)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            List<string> candidates = [.. s_names.Select(name => Path.Combine(directory, name))];
            candidates.AddRange(Directory.EnumerateFiles(directory, Pattern).Order(StringComparer.Ordinal));
            found.AddRange(candidates.Where(candidate => File.Exists(candidate) && seen.Add(candidate)));
        }

        return found;
    }
}
