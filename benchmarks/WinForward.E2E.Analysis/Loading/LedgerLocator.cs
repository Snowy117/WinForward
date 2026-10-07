using WinForward.E2E.Analysis.Cli;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Loading;

/// <summary>
/// Finds a pass's ledgers, in the order §2 prints their paths and §14 prints their rows: the pass
/// directory, then <c>--raw</c>, then <c>--raw</c>'s parent, and inside each of them the four names a
/// shipped launcher writes before the <c>*ledger*.jsonl</c> files sorted by path. A path seen twice is
/// kept once, at its first position.
/// </summary>
/// <remarks>
/// <para><b>A campaign runs more than one target instance</b> — the shipped launcher starts a proxied
/// target and a separate direct-lane target, each with its own ledger — so "the ledger" is never a
/// single file, and a first-seen order that disagrees with the reference would change §2's text and
/// §14's rows.</para>
/// <para><b><c>--ledger</c> replaces the search rather than extending it</b>, may be repeated, and is
/// used verbatim, filtered to the paths that exist: a caller that names a ledger which is not there
/// gets no ledger rather than a fallback to the tree's own.</para>
/// </remarks>
internal static class LedgerLocator
{
    private static readonly string[] s_names =
        ["target-ledger.jsonl", "ledger.jsonl", "ledger-main.jsonl", "ledger-direct.jsonl"];

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
        bases.Add(PosixPathText.Parent(options.Raw));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<string>();
        foreach (var directory in bases)
        {
            var candidates = new List<string>(s_names.Length + 2);
            foreach (var name in s_names)
            {
                candidates.Add(PosixPathText.Join(directory, name));
            }

            candidates.AddRange(PythonGlob.LedgerFiles(directory));
            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate) || !seen.Add(candidate))
                {
                    continue;
                }

                found.Add(candidate);
            }
        }

        return found;
    }
}
