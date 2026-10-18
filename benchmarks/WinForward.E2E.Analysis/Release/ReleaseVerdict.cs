using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Json;

namespace WinForward.E2E.Analysis.Release;

/// <summary>Renders <c>verdict.release.json</c>, schema <c>release-verdict/1</c>.</summary>
/// <remarks>
/// The document is a value, not a log: every number it publishes has the pre-registered threshold it was
/// judged against beside it, the gate cells that were removed are named, and the clock evidence is shown
/// with both ledger reads. It is written after the legacy files and never changes them.
/// </remarks>
internal static class ReleaseVerdict
{
    /// <summary>The schema name of the document this writer renders.</summary>
    private const string SchemaName = "release-verdict/1";

    /// <summary>Renders the complete verdict document, ending in exactly one newline.</summary>
    /// <param name="inputs">The release inputs and pass selection.</param>
    /// <param name="report">The comparison and capability results.</param>
    /// <param name="clock">The clock section.</param>
    /// <param name="findings">The release findings.</param>
    /// <param name="coverage">The rendered coverage JSON, or null when absent.</param>
    /// <returns>The file's text.</returns>
    internal static string ToJson(
        ReleaseInputs inputs,
        ReleaseComparisonReport report,
        ReleaseClockReport clock,
        IReadOnlyList<Finding> findings,
        string? coverage)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(findings);

        var primary = report.Metrics
            .Where(metric => string.Equals(metric.Role, ReleaseMetricRole.Primary, StringComparison.Ordinal))
            .Select(RenderMetric)
            .ToList();
        var exploratory = report.Metrics
            .Where(metric => string.Equals(metric.Role, ReleaseMetricRole.Exploratory, StringComparison.Ordinal))
            .Select(RenderMetric)
            .ToList();
        var bySeverity = FindingsCollector.BySeverity(findings);
        var counts = Severity.Order
            .Select(severity => (severity, VerbatimJson.Integer(bySeverity[severity].Count)))
            .ToList();
        var document = VerbatimJson.Object(
            0,
            ("schema", VerbatimJson.String(SchemaName)),
            ("release", VerbatimJson.Boolean(value: true)),
            ("caliber", RenderCaliber(inputs)),
            ("passes", RenderPasses(inputs.Passes)),
            ("referenceRow", VerbatimJson.String(inputs.ReferenceRow)),
            ("opponents", VerbatimJson.StringArray(1, inputs.Opponents)),
            ("primary", VerbatimJson.Array(1, primary)),
            ("exploratory", VerbatimJson.Array(1, exploratory)),
            ("capability", RenderCapabilities(report.Capabilities)),
            ("clock", clock.ToJson()),
            ("gates", RenderGates(report.Gates)),
            ("findings", RenderFindings(findings)),
            ("findingsBySeverity", VerbatimJson.Object(1, [.. counts])),
            ("coverage", coverage ?? VerbatimJson.Null),
            ("limits", VerbatimJson.StringArray(1, ReleaseLimits.All)));
        return document + "\n";
    }

    private static string RenderCaliber(ReleaseInputs inputs)
    {
        var seconds = VerbatimJson.Object(
            2,
            [.. Model.RowProfiles.PlanArms["full"].Select(arm => (arm, VerbatimJson.Integer(inputs.Caliber.Seconds[arm])))]);
        return VerbatimJson.Object(
            1,
            ("path", VerbatimJson.String(inputs.Caliber.Path)),
            ("sha256", VerbatimJson.String(inputs.CaliberSha256)),
            ("thresholdsPath", VerbatimJson.String(inputs.ThresholdsPath)),
            ("thresholdsSha256", VerbatimJson.String(inputs.ThresholdsSha256)),
            ("seconds", seconds));
    }

    private static string RenderPasses(ReleasePasses passes) => VerbatimJson.Object(
        1,
        ("total", VerbatimJson.Integer(passes.Total)),
        ("discarded", VerbatimJson.Array(1, [.. passes.Discarded.Select(value => VerbatimJson.Integer(value))])),
        ("measured", VerbatimJson.Array(1, [.. passes.Measured.Select(value => VerbatimJson.Integer(value))])),
        ("n", VerbatimJson.Integer(passes.N)),
        ("required", VerbatimJson.Integer(passes.Required)),
        ("insufficient", VerbatimJson.Boolean(passes.Insufficient)));

    private static string RenderMetric(ReleaseMetricResult metric)
    {
        var values = new List<(string Key, string Value)>();
        foreach (var (passId, value) in metric.ReferenceValues)
        {
            values.Add((passId, VerbatimJson.Number(value)));
        }

        var members = new List<(string Key, string Value)>
        {
            ("metric", VerbatimJson.String(metric.Metric)),
            ("role", VerbatimJson.String(metric.Role)),
            ("kind", Text(metric.Kind)),
            ("band", Number(metric.Band)),
            ("business", Number(metric.Business)),
            ("mdd", Number(metric.Mdd)),
            ("referenceRow", VerbatimJson.String(metric.ReferenceRow)),
            ("referenceMedian", Number(metric.ReferenceMedian)),
            ("referenceValues", VerbatimJson.Object(2, [.. values])),
            ("decisionPair", Text(metric.DecisionPair)),
            ("median", Number(metric.Median)),
            ("rawCi", RenderInterval(metric.RawInterval)),
            ("adjustedCi", RenderInterval(metric.AdjustedInterval)),
            ("familySize", VerbatimJson.Integer(metric.FamilySize)),
            ("equivalenceMargin", Number(metric.EquivalenceMargin)),
            ("zeroEventN", metric.ZeroEventN is { } n ? VerbatimJson.Integer(n) : VerbatimJson.Null),
            ("zeroEventUpperBound", Number(metric.ZeroEventUpperBound)),
            ("state", VerbatimJson.String(metric.State)),
            ("reason", VerbatimJson.String(metric.Reason)),
            ("gateInvalidations", RenderInvalidations(metric.GateInvalidations)),
            ("pairs", VerbatimJson.Array(2, [.. metric.Pairs.Select(RenderPair)])),
        };
        return VerbatimJson.Object(1, [.. members]);
    }

    private static string RenderPair(ReleasePairResult pair) => VerbatimJson.Object(
        2,
        ("rowId", VerbatimJson.String(pair.RowId)),
        ("inFamily", VerbatimJson.Boolean(pair.InFamily)),
        ("ineligibility", Text(pair.Ineligibility)),
        ("passesUsed", VerbatimJson.Integer(pair.PassesUsed)),
        ("referenceMedian", Number(pair.ReferenceMedian)),
        ("rowMedian", Number(pair.RowMedian)),
        ("median", Number(pair.Median)),
        ("rawCi", RenderInterval(pair.RawInterval)),
        ("adjustedCi", RenderInterval(pair.AdjustedInterval)),
        ("equivalenceMargin", Number(pair.EquivalenceMargin)),
        ("state", VerbatimJson.String(pair.State)),
        ("reason", VerbatimJson.String(pair.Reason)),
        ("invalidatedPasses", VerbatimJson.StringArray(3, pair.InvalidatedPasses)));

    private static string RenderInterval(ReleaseInterval? interval) =>
        interval is { Level: var level, Low: var low, High: var high }
            ? VerbatimJson.Object(
                2,
                ("level", VerbatimJson.Number(level)),
                ("low", VerbatimJson.Number(low)),
                ("high", VerbatimJson.Number(high)))
        : VerbatimJson.Null;

    private static string RenderCapabilities(IReadOnlyList<ReleaseCapabilityResult> results)
    {
        var rendered = new List<string>(results.Count);
        foreach (var result in results)
        {
            var passes = new List<string>(result.Passes.Count);
            foreach (var pass in result.Passes)
            {
                passes.Add(VerbatimJson.Object(
                    3,
                    ("pass", VerbatimJson.String(pass.PassId)),
                    ("events", VerbatimJson.Integer(pass.Events)),
                    ("attempts", VerbatimJson.Integer(pass.Attempts))));
            }

            rendered.Add(VerbatimJson.Object(
                2,
                ("metric", VerbatimJson.String(result.Metric)),
                ("rowId", VerbatimJson.String(result.RowId)),
                ("nFrom", Text(result.NFrom)),
                ("rate", Number(result.Rate)),
                ("events", result.Events is { } events ? VerbatimJson.Integer(events) : VerbatimJson.Null),
                ("n", result.N is { } n ? VerbatimJson.Integer(n) : VerbatimJson.Null),
                ("upperBound", Number(result.UpperBound)),
                ("threshold", VerbatimJson.Number(result.Threshold)),
                ("status", VerbatimJson.String(result.Status)),
                ("reason", VerbatimJson.String(result.Reason)),
                ("passes", VerbatimJson.Array(3, passes))));
        }

        return VerbatimJson.Array(1, rendered);
    }

    private static string RenderGates(ReleaseGateMatrixResult gates)
    {
        var cells = gates.Invalidations
            .Select(entry => (entry.Metric, entry.RowId, entry.Pass))
            .Distinct()
            .Count();
        var caveats = new List<(string Key, string Value)>(gates.Caveats.Count);
        foreach (var caveat in gates.Caveats)
        {
            caveats.Add((caveat.Kind, VerbatimJson.Object(
                2,
                ("scope", VerbatimJson.String(caveat.Scope)),
                ("detail", VerbatimJson.String(caveat.Detail)))));
        }

        return VerbatimJson.Object(
            1,
            ("invalidatedCells", VerbatimJson.Integer(cells)),
            ("invalidations", RenderInvalidations(gates.Invalidations)),
            ("caveats", VerbatimJson.Object(2, [.. caveats])));
    }

    private static string RenderInvalidations(IReadOnlyList<ReleaseGateInvalidation> entries)
    {
        var rendered = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            rendered.Add(VerbatimJson.Object(
                2,
                ("metric", VerbatimJson.String(entry.Metric)),
                ("rowId", VerbatimJson.String(entry.RowId)),
                ("pass", VerbatimJson.String(entry.Pass)),
                ("arm", VerbatimJson.String(entry.Arm)),
                ("gate", VerbatimJson.String(entry.Gate)),
                ("reason", VerbatimJson.String(entry.Reason))));
        }

        return VerbatimJson.Array(1, rendered);
    }

    private static string RenderFindings(IReadOnlyList<Finding> findings)
    {
        var rendered = new List<string>(findings.Count);
        foreach (var finding in findings)
        {
            rendered.Add(VerbatimJson.Object(
                2,
                ("severity", VerbatimJson.String(finding.Severity)),
                ("kind", VerbatimJson.String(finding.Kind)),
                ("scope", VerbatimJson.String(finding.Scope)),
                ("detail", VerbatimJson.String(finding.Detail))));
        }

        return VerbatimJson.Array(1, rendered);
    }

    private static string Number(double? value) => value is { } number ? VerbatimJson.Number(number) : VerbatimJson.Null;

    private static string Text(string? value) => value is null ? VerbatimJson.Null : VerbatimJson.String(value);
}
