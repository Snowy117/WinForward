using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Tables;
using WinForward.E2E.Analysis.Verdict;

namespace WinForward.E2E.Analysis.Cli;

/// <summary>
/// One analysis run: read the tree, render the two documents, write the plotting notice, report what is
/// still owed by which batch.
/// </summary>
/// <remarks>
/// <para><b>The outputs are written unconditionally.</b> <c>tables.md</c> and <c>verdict.json</c> are
/// the oracle's two files and <c>plots/SKIPPED.md</c> is the statement that no plot was drawn; a run
/// that wrote only what it could render would make "this batch is not done" indistinguishable from
/// "the analysis produced nothing", which is the one distinction the differ's three exit codes exist
/// to draw.</para>
/// <para><b>stdout is not part of the contract.</b> The dump of what each batch still owes is for a
/// human running the skeleton; the compared bytes are the two files below, and nothing about the
/// summary lines is diffed.</para>
/// </remarks>
internal static class AnalysisRunner
{
    /// <summary>Runs one analysis and returns the process exit code.</summary>
    internal static int Run(AnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!CampaignLoader.TryLoad(options, out var campaign, out var error) || campaign is null)
        {
            Console.Error.WriteLine($"e2e-analysis: {error}");
            Console.Error.WriteLine($"usage: {AnalysisOptions.Usage}");
            return ExitCodes.InputError;
        }

        var output = Directory.CreateDirectory(options.Out);
        var findings = FindingsCollector.Collect(campaign);
        using (var tables = OpenOutput(Path.Combine(output.FullName, "tables.md")))
        {
            TablesWriter.Write(tables, campaign, findings);
        }

        using (var verdict = OpenOutput(Path.Combine(output.FullName, "verdict.json")))
        {
            VerdictWriter.Write(verdict, VerdictSections.Build(campaign, findings));
        }

        PlotsNotice.Write(Path.Combine(output.FullName, "plots"));

        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"e2e-analysis: {campaign.PassIds.Count} pass(es), {campaign.RowCount} row(s), "
            + $"{campaign.LedgerFileCount} ledger(s), {campaign.Rows.Count} loaded run(s) -> {Path.Combine(output.FullName, "tables.md")}"));
        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"e2e-analysis: verdict -> {Path.Combine(output.FullName, "verdict.json")}; "
            + $"warmup {options.WarmupSeconds:0.0#} s, {options.Resamples} resamples, seed {options.Seed}, "
            + $"min passes {AnalysisOptions.DefaultMinPasses}"));
        Console.WriteLine("e2e-analysis: plots/ is not rendered; wrote plots/SKIPPED.md");
        foreach (var passId in campaign.PassIds)
        {
            var ledgers = campaign.LedgerPaths.TryGetValue(passId, out var paths)
                ? string.Join(", ", paths)
                : "none";
            Console.WriteLine($"e2e-analysis: {passId} ledger(s): {ledgers}");
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// Opens one output file for writing, truncating it, as UTF-8 without a byte-order mark and with
    /// <c>\n</c> line endings: both are part of the bytes the oracle compares (D20.5).
    /// </summary>
    private static StreamWriter OpenOutput(string path) =>
        new(path, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            NewLine = "\n",
        };
}
