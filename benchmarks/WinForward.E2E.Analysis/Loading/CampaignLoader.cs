using WinForward.E2E.Analysis.Cli;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Loading;

/// <summary>
/// Reads a campaign tree into the model the tables are rendered from.
/// </summary>
/// <remarks>
/// <b>Completed in batch 1a.</b> What is here is the pass/row census and the ledger scan — enough for
/// a caller to see which tree it was pointed at — while the model the reference builds (each row's
/// arms with their result, sample, summary and failure records, the dual phase's two lanes, the
/// environment block and the plan provenance) is the batch's own work. None of it is guessed at here:
/// the reference's discovery rules (which directory is a pass, which is a row, what <c>--flat</c>
/// changes, and in what order rows are read) are compared as text through §2 and §15, so a
/// half-implemented loader would be worse than a documented one.
/// </remarks>
internal static class CampaignLoader
{
    /// <summary>The tree's census, or null when <c>--raw</c> is not a directory a caller can look at.</summary>
    internal static CampaignInventory? Load(AnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var root = new DirectoryInfo(options.Raw);
        if (!root.Exists)
        {
            return null;
        }

        var passes = new List<string>();
        var rows = 0;
        if (options.Flat)
        {
            passes.Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(root.FullName)));
            rows = LooksLikeRow(root) ? 1 : root.EnumerateDirectories().Count(LooksLikeRow);
        }
        else
        {
            foreach (var pass in root.EnumerateDirectories().OrderBy(directory => directory.Name, StringComparer.Ordinal))
            {
                passes.Add(pass.Name);
                rows += pass.EnumerateDirectories().Count(LooksLikeRow);
            }
        }

        var ledgers = LedgerLocator.Locate(options);
        var records = ledgers.Sum(path => RecordScanner.ScanJsonl(new FileInfo(path)).Lines);
        return new CampaignInventory(passes, rows, ledgers, records);
    }

    /// <summary>
    /// Whether a directory is a row: it holds a <c>run.json</c> or at least one <c>*.jsonl</c>, which is
    /// what tells a row apart from the dual phase's own directory and from scaffolding.
    /// </summary>
    private static bool LooksLikeRow(DirectoryInfo directory) =>
        File.Exists(Path.Combine(directory.FullName, "run.json"))
        || directory.EnumerateFiles("*.jsonl").Any();
}
