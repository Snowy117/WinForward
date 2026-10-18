using System.Globalization;
using System.Text;
using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Release;

/// <summary>The three release files, rendered before anything is written.</summary>
/// <param name="VerdictJson"><c>verdict.release.json</c>.</param>
/// <param name="TablesMarkdown"><c>tables.release.md</c>.</param>
/// <param name="FindingsJson"><c>findings.release.json</c>.</param>
internal sealed record ReleaseArtifacts(string VerdictJson, string TablesMarkdown, string FindingsJson);

/// <summary>Builds and writes the release artifacts from an already-loaded campaign.</summary>
/// <remarks>
/// <b>All three documents are rendered before the first byte is written.</b> A release refusal therefore
/// leaves the output directory exactly as the legacy path would have left it, and a writer failure cannot
/// produce a verdict beside a missing report. The legacy documents are the caller's, unchanged; this class
/// only adds files with the <c>.release.</c> names.
/// </remarks>
internal static class ReleaseWriter
{
    /// <summary>The verdict file's name.</summary>
    private const string VerdictFileName = "verdict.release.json";

    /// <summary>The report file's name.</summary>
    private const string TablesFileName = "tables.release.md";

    /// <summary>The findings file's name.</summary>
    private const string FindingsFileName = "findings.release.json";

    /// <summary>Builds every release artifact, or returns the input error that prevented it.</summary>
    /// <param name="campaign">The campaign the legacy path just loaded.</param>
    /// <param name="campaignRoot">The campaign root, for the clock log and the ledgers.</param>
    /// <param name="thresholds">The parsed thresholds file the inputs were built from.</param>
    /// <param name="inputs">The accepted release inputs.</param>
    /// <param name="legacyFindings">The findings the legacy collector produced.</param>
    /// <param name="artifacts">The rendered files.</param>
    /// <param name="error">Why they could not be built, when they could not.</param>
    /// <returns>True when the comparison, the clock section and all three documents were rendered.</returns>
    internal static bool TryBuild(
        CampaignModel campaign,
        string campaignRoot,
        ThresholdsFile thresholds,
        ReleaseInputs inputs,
        IReadOnlyList<Finding> legacyFindings,
        out ReleaseArtifacts? artifacts,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentException.ThrowIfNullOrEmpty(campaignRoot);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(legacyFindings);

        artifacts = null;
        if (!ReleaseDatasetReader.TryBuild(campaign, inputs, out var dataset, out error) || dataset is null)
        {
            error = $"release: {error ?? "the campaign could not be read into a release dataset"}";
            return false;
        }

        var gates = MeasuredOnly(ReleaseGateMatrix.Evaluate(ReleaseGateEvidenceReader.Read(campaign)), inputs.Passes.Measured);
        var clock = ReleaseClockReport.Build(campaignRoot, thresholds, inputs);
        ReleaseCoverage.TryRead(inputs.ThresholdsPath, out var coverage, out var coverageError);
        var report = ReleaseComparison.Evaluate(thresholds, inputs, dataset, gates);
        var findings = ReleaseFindings.Build(legacyFindings, gates, clock, coverageError);
        artifacts = new ReleaseArtifacts(
            VerdictJson: ReleaseVerdict.ToJson(inputs, report, clock, findings, coverage),
            TablesMarkdown: ReleaseTables.Markdown(inputs, report, clock, coverage, coverageError),
            FindingsJson: ReleaseFindings.ToJson(findings));
        error = null;
        return true;
    }

    /// <summary>Writes the three files into an existing output directory.</summary>
    /// <param name="outputDirectory">The directory the legacy documents were written to.</param>
    /// <param name="artifacts">The rendered files.</param>
    internal static void Write(string outputDirectory, ReleaseArtifacts artifacts)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentNullException.ThrowIfNull(artifacts);

        WriteFile(Path.Combine(outputDirectory, VerdictFileName), artifacts.VerdictJson);
        WriteFile(Path.Combine(outputDirectory, TablesFileName), artifacts.TablesMarkdown);
        WriteFile(Path.Combine(outputDirectory, FindingsFileName), artifacts.FindingsJson);
    }

    private static ReleaseGateMatrixResult MeasuredOnly(ReleaseGateMatrixResult gates, IReadOnlyList<int> measuredPasses)
    {
        var measured = measuredPasses
            .Select(number => string.Create(CultureInfo.InvariantCulture, $"pass{number}"))
            .ToHashSet(StringComparer.Ordinal);
        return new ReleaseGateMatrixResult
        {
            Invalidations = [.. gates.Invalidations.Where(entry => measured.Contains(entry.Pass))],
            Caveats = gates.Caveats,
        };
    }

    private static void WriteFile(string path, string text)
    {
        using var writer = new StreamWriter(
            path,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n";
        writer.Write(text);
    }
}
