using System.Globalization;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Tables;

namespace WinForward.E2E.Analysis.Release;

/// <summary>Renders <c>tables.release.md</c>, the compact human report of one release verdict.</summary>
/// <remarks>
/// The file is a reading aid, not a second decision: every state it prints is the one in
/// <c>verdict.release.json</c>, and the JSON remains the contract. It prints no number the verdict does
/// not publish, which is what keeps the two files from disagreeing after a gate or clock change.
/// </remarks>
internal static class ReleaseTables
{
    private const string NotMeasured = "n/a";

    /// <summary>Renders the report, ending in exactly one newline.</summary>
    /// <param name="inputs">The release inputs and pass selection.</param>
    /// <param name="report">The comparison and capability results.</param>
    /// <param name="clock">The clock section.</param>
    /// <param name="coverage">The rendered coverage JSON, or null when absent.</param>
    /// <param name="coverageError">Why the coverage file could not be read, or null.</param>
    /// <returns>The file's text.</returns>
    internal static string Markdown(
        ReleaseInputs inputs,
        ReleaseComparisonReport report,
        ReleaseClockReport clock,
        string? coverage,
        string? coverageError)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(clock);

        var lines = new List<string>
        {
            "# Release verdict",
            string.Empty,
            "Release rules are C#-only: `tables.md`, `verdict.json` and `plots/SKIPPED.md` remain the frozen legacy "
                + "output and the Python oracle does not cover this file. `verdict.release.json` is the contract; this "
                + "report is its compact reading.",
            string.Empty,
        };
        AddCaliber(lines, inputs);
        AddPasses(lines, inputs.Passes);
        AddPrimary(lines, report);
        AddCapability(lines, report.Capabilities);
        AddClock(lines, clock);
        AddGates(lines, report.Gates);
        AddExploratory(lines, report.Metrics);
        AddCoverage(lines, coverage, coverageError);
        AddLimits(lines);
        return string.Join('\n', lines);
    }

    private static void AddCaliber(List<string> lines, ReleaseInputs inputs) => Add(
        lines,
        "## Caliber",
        string.Empty,
        Table(
            ["item", "value"],
            [
                Row("plan", inputs.Caliber.Path),
                Row("plan sha256", inputs.CaliberSha256),
                Row("thresholds", inputs.ThresholdsPath),
                Row("thresholds sha256", inputs.ThresholdsSha256),
                Row("release", "true"),
            ]),
        string.Empty,
        Table(
            ["arm", "seconds"],
            [.. Model.RowProfiles.PlanArms["full"].Select(arm => Row(arm, Integer(inputs.Caliber.Seconds[arm])))]),
        string.Empty);

    private static void AddPasses(List<string> lines, ReleasePasses passes) => Add(
        lines,
        "## Passes",
        string.Empty,
        Table(
            ["total", "discarded", "measured", "n", "required", "insufficient"],
            [Row(
                Integer(passes.Total),
                Join(passes.Discarded),
                Join(passes.Measured),
                Integer(passes.N),
                Integer(passes.Required),
                passes.Insufficient ? "yes" : "no")]),
        string.Empty);

    private static void AddPrimary(List<string> lines, ReleaseComparisonReport report) => Add(
        lines,
        "## Primary comparisons",
        string.Empty,
        Primary(report),
        string.Empty,
        "The family is the reference row against each declared opponent that ran the metric on a comparable path; "
            + "an excluded pair is report-only. The decision reads the Bonferroni-adjusted interval "
            + $"({ReleaseComparison.AdjustmentLabel}), not the raw one.",
        string.Empty);

    private static void AddCapability(List<string> lines, IReadOnlyList<ReleaseCapabilityResult> capabilities) => Add(
        lines,
        "## REL capability",
        string.Empty,
        Capability(capabilities),
        string.Empty,
        "A capability gate is a bound, not a difference claim: it passes only with zero events and `3/n` at or below "
            + "the declared threshold, and a zero denominator is `not-measured` rather than a pass.",
        string.Empty);

    private static void AddClock(List<string> lines, ReleaseClockReport clock) => Add(
        lines,
        "## Clock",
        string.Empty,
        Clock(clock),
        string.Empty);

    private static void AddGates(List<string> lines, ReleaseGateMatrixResult gates) => Add(
        lines,
        "## Gates",
        string.Empty,
        Gates(gates),
        string.Empty);

    private static void AddExploratory(List<string> lines, IReadOnlyList<ReleaseMetricResult> metrics) => Add(
        lines,
        "## Exploratory",
        string.Empty,
        Exploratory(metrics),
        string.Empty);

    private static void AddCoverage(List<string> lines, string? coverage, string? coverageError) => Add(
        lines,
        "## Coverage",
        string.Empty,
        Coverage(coverage, coverageError),
        string.Empty);

    private static void AddLimits(List<string> lines)
    {
        lines.Add("## Limits");
        lines.Add(string.Empty);
        lines.AddRange(ReleaseLimits.All.Select(limit => "- " + limit));
        lines.Add(string.Empty);
    }

    private static void Add(List<string> lines, params string[] values) => lines.AddRange(values);

    private static string Primary(ReleaseComparisonReport report)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var metric in report.Metrics)
        {
            if (!string.Equals(metric.Role, ReleaseMetricRole.Primary, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var pair in metric.Pairs)
            {
                rows.Add(
                [
                    metric.Metric,
                    Band(metric),
                    Number(metric.Mdd, 4),
                    pair.RowId,
                    Number(pair.Median, 5),
                    Interval(pair.RawInterval),
                    Interval(pair.AdjustedInterval),
                    Integer(metric.FamilySize),
                    Integer(pair.PassesUsed),
                    pair.State,
                ]);
            }
        }

        return rows.Count > 0
            ? Table(
                ["metric", "band", "MDD", "opponent", "median", "raw CI", "adjusted CI", "family m", "passes", "state"],
                rows)
            : "No primary comparison was declared.";
    }

    private static string Capability(IReadOnlyList<ReleaseCapabilityResult> results)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var result in results)
        {
            rows.Add(
            [
                result.RowId,
                result.Metric,
                Number(result.Rate, 5),
                result is { Events: { } events, N: { } n } ? $"{Integer(events)} / {Integer(n)}" : NotMeasured,
                Number(result.UpperBound, 5),
                Number(result.Threshold, 5),
                result.Status,
            ]);
        }

        return rows.Count > 0
            ? Table(["row", "metric", "rate (pp)", "events / n", "3/n upper bound (pp)", "threshold (pp)", "status"], rows)
            : "No capability gate was declared.";
    }

    private static string Clock(ReleaseClockReport clock)
    {
        var summary = new List<IReadOnlyList<string>>
        {
            Row("status", ReleaseClockReport.StatusText(clock.Status)),
            Row("applied", clock.Applied ? "yes" : "no"),
            Row("source", clock.SourcePath),
            Row("probes / bad lines", $"{Integer(clock.Samples)} / {Integer(clock.BadLines)}"),
            Row("drift limit", $"{Number(clock.MaxDriftMs, 3)} ms"),
        };
        if (clock.Problem is not null)
        {
            summary.Add(["problem", clock.Problem]);
        }

        var corrected = clock.Corrected;
        var uncorrected = clock.Uncorrected;
        summary.Add(["corrected ledger", Ledger(corrected)]);
        summary.Add(["uncorrected ledger", Ledger(uncorrected)]);
        var lines = new List<string>
        {
            Table(["item", "value"], summary),
            string.Empty,
            Table(
                ["pass", "role", "status", "pre offset (ms)", "post offset (ms)", "drift (ms)", "residual (ms)", "applied offset (ms)"],
                [.. clock.Passes.Select(pass => Row(
                    pass.PassId,
                    pass.Role,
                    pass.Status,
                    Number(pass.PreOffsetMs, 3),
                    Number(pass.PostOffsetMs, 3),
                    Number(pass.DriftMs, 3),
                    Number(pass.ResidualMs, 3),
                    Number(pass.OffsetMs, 3)))]),
        };
        if (!clock.Applied)
        {
            lines.Add(string.Empty);
            lines.Add("The correction was not applied; both ledger reads are the uncorrected control. The status and the per-pass "
                + "evidence above are the disclosure, not a zero offset.");
        }

        return string.Join('\n', lines);
    }

    private static string Gates(ReleaseGateMatrixResult gates)
    {
        var lines = new List<string>();
        if (gates.Invalidations.Count == 0)
        {
            lines.Add("No gate invalidated a metric cell.");
        }
        else
        {
            var cells = gates.Invalidations
                .Select(entry => (entry.Metric, entry.RowId, entry.Pass))
                .Distinct()
                .Count();
            lines.Add($"{Integer(cells)} metric cell(s) were invalidated and excluded from their family.");
            lines.Add(string.Empty);
            lines.Add(Table(
                ["metric", "row", "pass", "arm", "gate", "reason"],
                [.. gates.Invalidations.Select(entry => Row(
                    entry.Metric,
                    entry.RowId,
                    entry.Pass,
                    entry.Arm.Length > 0 ? entry.Arm : "—",
                    entry.Gate,
                    entry.Reason))]));
        }

        if (gates.Caveats.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Campaign caveats (no product cell is invalidated):");
            lines.Add(string.Empty);
            foreach (var caveat in gates.Caveats)
            {
                lines.Add($"- `{caveat.Kind}` ({caveat.Scope}): {caveat.Detail}");
            }
        }

        return string.Join('\n', lines);
    }

    private static string Exploratory(IReadOnlyList<ReleaseMetricResult> metrics)
    {
        var exploratory = metrics
            .Where(metric => string.Equals(metric.Role, ReleaseMetricRole.Exploratory, StringComparison.Ordinal))
            .ToList();
        return exploratory.Count == 0
            ? "No exploratory metric is declared."
            : $"{Integer(exploratory.Count)} exploratory metric(s) report values and raw intervals only; their per-pass values, "
                + "per-opponent raw intervals and family exclusions are in `verdict.release.json` under `exploratory`. No "
                + "difference or equivalence claim is made for them.";
    }

    private static string Coverage(string? coverage, string? coverageError)
    {
        if (coverageError is not null)
        {
            return $"`coverage.json` is present but unreadable; the verdict publishes `coverage: null`. {coverageError}";
        }

        return coverage is null
            ? "`coverage.json` is absent beside the thresholds file; the verdict publishes `coverage: null` rather than "
                + "claiming a coverage the calibration did not write."
            : "```json\n" + coverage + "\n```";
    }

    private static string Ledger(ReleaseLedgerMismatch mismatch) => mismatch.Available
        ? $"{Integer(mismatch.Arms)} arm window(s) in {Integer(mismatch.Files)} file(s); "
            + $"{Integer(mismatch.ConnectionMismatches)} connection mismatch(es), "
            + $"{Integer(mismatch.DatagramMismatches)} datagram mismatch(es), "
            + $"{Integer(mismatch.Unattributable)} unattributable"
        : "no ledger found";

    private static string Band(ReleaseMetricResult metric) => metric.Kind switch
    {
        ThresholdKind.Ratio => metric.Band is { } ratio ? $"±{Number(ratio * 100.0, 2)} %" : NotMeasured,
        ThresholdKind.PercentagePoints => metric.Band is { } pp ? $"±{Number(pp, 4)} pp" : NotMeasured,
        _ => NotMeasured,
    };

    private static string Interval(ReleaseInterval? interval) =>
        interval is { Level: var level, Low: var low, High: var high }
            ? $"[{Number(low, 5)}, {Number(high, 5)}] ({Number(level * 100.0, 2)} %)"
            : NotMeasured;

    private static string[] Row(params string[] cells) => cells;

    private static string Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows) =>
        MarkdownTable.Render(headers, [.. rows.Select(row => Row([.. row.Select(Cell)]))]);

    private static string Cell(string value) => value
        .Replace('|', '/')
        .Replace('\n', ' ')
        .Replace('\r', ' ');

    private static string Number(double? value, int digits) => value is { } number
        ? VerbatimNumber.General(number, Math.Max(digits, 1))
        : NotMeasured;

    private static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Join(IReadOnlyList<int> values) => values.Count == 0 ? "—" : string.Join(", ", values);
}
