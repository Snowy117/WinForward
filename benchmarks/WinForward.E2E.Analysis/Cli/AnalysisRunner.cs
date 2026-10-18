using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Release;
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
/// <para><b>A release run validates its inputs before it writes anything.</b> The thresholds file, the
/// plan hash and the pass selection are checked first, so a refused release invocation leaves no
/// half-written artifact set behind; on success the legacy documents are written unchanged and
/// <c>release-inputs.json</c> is added beside them.</para>
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

        if (!TryBuildReleaseInputs(options, campaign, out var releaseInputs))
        {
            return ExitCodes.InputError;
        }

        var output = Directory.CreateDirectory(options.Out);
        Render(campaign, releaseInputs, output.FullName);
        Report(options, campaign, releaseInputs, output.FullName);
        return ExitCodes.Success;
    }

    /// <summary>
    /// The release path's share of one run: validate the thresholds file, the caliber hash and the pass
    /// tree before anything is written.
    /// </summary>
    private static bool TryBuildReleaseInputs(AnalysisOptions options, CampaignModel campaign, out ReleaseInputs? inputs)
    {
        inputs = null;
        var release = options.Release;
        if (!release.Release)
        {
            return true;
        }

        var outcome = ReleaseRun.Execute(release, options.WarmupSeconds, campaign.PassIds, Directory.GetCurrentDirectory());
        if (outcome.ExitCode != ExitCodes.Success || outcome.Inputs is null)
        {
            Console.Error.WriteLine($"e2e-analysis: {outcome.Error}");
            return false;
        }

        inputs = outcome.Inputs;
        return true;
    }

    /// <summary>Writes the two legacy documents, the plotting notice and the release inputs.</summary>
    private static void Render(CampaignModel campaign, ReleaseInputs? releaseInputs, string outputDirectory)
    {
        var findings = FindingsCollector.Collect(campaign);
        using (var tables = OpenOutput(Path.Combine(outputDirectory, "tables.md")))
        {
            TablesWriter.Write(tables, campaign, findings);
        }

        using (var verdict = OpenOutput(Path.Combine(outputDirectory, "verdict.json")))
        {
            VerdictWriter.Write(verdict, VerdictSections.Build(campaign, findings));
        }

        PlotsNotice.Write(Path.Combine(outputDirectory, "plots"));

        if (releaseInputs is not null)
        {
            using var document = OpenOutput(Path.Combine(outputDirectory, "release-inputs.json"));
            document.Write(releaseInputs.ToJson());
        }
    }

    /// <summary>The run's summary, for a human reading the console; none of it is compared.</summary>
    private static void Report(
        AnalysisOptions options,
        CampaignModel campaign,
        ReleaseInputs? releaseInputs,
        string outputDirectory)
    {
        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"e2e-analysis: {campaign.PassIds.Count} pass(es), {campaign.RowCount} row(s), "
            + $"{campaign.LedgerFileCount} ledger(s), {campaign.Rows.Count} loaded run(s) -> {Path.Combine(outputDirectory, "tables.md")}"));
        var minPasses = releaseInputs is null
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"min passes {AnalysisOptions.DefaultMinPasses}")
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"legacy min passes {AnalysisOptions.DefaultMinPasses}, release requires {releaseInputs.Passes.Required} with {releaseInputs.Passes.N} measured");
        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"e2e-analysis: verdict -> {Path.Combine(outputDirectory, "verdict.json")}; "
            + $"warmup {options.WarmupSeconds:0.0#} s, {options.Resamples} resamples, seed {options.Seed}, "
            + $"{minPasses}"));
        Console.WriteLine("e2e-analysis: plots/ is not rendered; wrote plots/SKIPPED.md");

        if (releaseInputs is not null)
        {
            var suffix = releaseInputs.Passes.Insufficient ? " (insufficient)" : string.Empty;
            Console.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"e2e-analysis: release inputs -> {Path.Combine(outputDirectory, "release-inputs.json")}; "
                + $"{releaseInputs.Passes.N} measured pass(es) of {releaseInputs.Passes.Total}, required {releaseInputs.Passes.Required}{suffix}"));
        }

        foreach (var passId in campaign.PassIds)
        {
            var ledgers = campaign.LedgerPaths.TryGetValue(passId, out var paths)
                ? string.Join(", ", paths)
                : "none";
            Console.WriteLine($"e2e-analysis: {passId} ledger(s): {ledgers}");
        }
    }

    /// <summary>
    /// Opens one output file for writing, truncating it, as UTF-8 without a byte-order mark and with
    /// <c>\n</c> line endings: both are part of the bytes the oracle compares.
    /// </summary>
    private static StreamWriter OpenOutput(string path) =>
        new(path, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            NewLine = "\n",
        };
}
