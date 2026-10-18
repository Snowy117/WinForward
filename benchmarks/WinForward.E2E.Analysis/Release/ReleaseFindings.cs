using System.Globalization;
using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Json;

namespace WinForward.E2E.Analysis.Release;

/// <summary>Builds the release findings list and renders <c>findings.release.json</c>.</summary>
/// <remarks>
/// The release list starts from the legacy collector's findings and adds only what the legacy path cannot
/// know: the gate cells the release matrix invalidated, the BASE path-loss caveat that is not a legacy
/// finding, and the clock correction's status, drift and both ledger reads. The legacy documents keep
/// their own list untouched.
/// </remarks>
internal static class ReleaseFindings
{
    /// <summary>The schema name of the document this writer renders.</summary>
    private const string SchemaName = "release-findings/1";

    /// <summary>Builds the deduplicated release findings list.</summary>
    /// <param name="legacy">The findings the legacy collector produced.</param>
    /// <param name="gates">The gate matrix result.</param>
    /// <param name="clock">The clock report.</param>
    /// <param name="coverageError">Why the optional coverage file could not be read, or null.</param>
    /// <returns>The release findings, legacy entries first.</returns>
    internal static List<Finding> Build(
        IReadOnlyList<Finding> legacy,
        ReleaseGateMatrixResult gates,
        ReleaseClockReport clock,
        string? coverageError)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(clock);

        var findings = new List<Finding>(legacy);
        AddGateInvalidations(findings, gates);
        AddGateCaveats(findings, gates);
        AddClock(findings, clock);
        if (coverageError is not null)
        {
            findings.Add(new Finding(
                Severity.MeasurementCaveat,
                "coverage-unreadable",
                "campaign",
                coverageError));
        }

        var seen = new HashSet<(string, string, string, string)>();
        return [.. findings.Where(finding => seen.Add((finding.Severity, finding.Kind, finding.Scope, finding.Detail)))];
    }

    /// <summary>Renders the release findings document, ending in exactly one newline.</summary>
    /// <param name="findings">The release findings, in collection order.</param>
    /// <returns>The file's text.</returns>
    internal static string ToJson(IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

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

        var bySeverity = FindingsCollector.BySeverity(findings);
        var counts = Severity.Order
            .Select(severity => (severity, VerbatimJson.Integer(bySeverity[severity].Count)))
            .ToList();
        var document = VerbatimJson.Object(
            0,
            ("schema", VerbatimJson.String(SchemaName)),
            ("findings", VerbatimJson.Array(1, rendered)),
            ("findingsBySeverity", VerbatimJson.Object(1, [.. counts])),
            ("limits", VerbatimJson.StringArray(1, ReleaseLimits.All)));
        return document + "\n";
    }

    private static void AddGateInvalidations(List<Finding> findings, ReleaseGateMatrixResult gates)
    {
        foreach (var entry in gates.Invalidations)
        {
            var scope = entry.Arm.Length > 0
                ? $"{entry.Pass}/{entry.RowId} {entry.Arm} {entry.Metric}"
                : $"{entry.Pass}/{entry.RowId} {entry.Metric}";
            findings.Add(new Finding(
                Severity.HarnessError,
                "gate-invalidation",
                scope,
                $"{entry.Gate}: {entry.Reason}. The release comparison excludes this pass from the metric's interval and family rather than averaging it in"));
        }
    }

    private static void AddGateCaveats(List<Finding> findings, ReleaseGateMatrixResult gates)
    {
        foreach (var caveat in gates.Caveats)
        {
            // Control drift, a foreign connection and a direct leak already have a legacy finding; the
            // BASE path-loss floor does not, and it is the one campaign caveat that has no other home.
            if (!string.Equals(caveat.Kind, "base-floor", StringComparison.Ordinal))
            {
                continue;
            }

            findings.Add(new Finding(
                Severity.MeasurementCaveat,
                "base-floor",
                caveat.Scope,
                caveat.Detail));
        }
    }
    private static Finding PartialClockFinding(ReleaseClockReport clock)
    {
        var partial = clock.Passes
            .Where(pass => pass.Status is "pre-only" or "post-only")
            .Select(pass => pass.PassId);
        return new Finding(
            Severity.MeasurementCaveat,
            "clock-partial",
            "campaign",
            $"the clock log has one phase for {string.Join(", ", partial)}; those passes use the phase they have and the rest of the campaign is corrected normally");
    }


    private static void AddClock(List<Finding> findings, ReleaseClockReport clock)
    {
        var statusFinding = clock.Status switch
        {
            ClockStatus.Absent => new Finding(
                Severity.Informational,
                "clock-absent",
                "campaign",
                $"no '{ClockCorrection.FileName}' at the campaign root: every ledger window stays on the client's clock, so the corrected and uncorrected reads are the same and the negative control has nothing to compare"),
            ClockStatus.Incomplete => new Finding(
                Severity.MeasurementCaveat,
                "clock-incomplete",
                "campaign",
                clock.Problem ?? "the clock log holds no usable probe, so no ledger window was corrected"),
            ClockStatus.Partial => PartialClockFinding(clock),
            ClockStatus.Complete => null,
            // ReSharper disable once RedundantSwitchExpressionArms // ClockStatus is public; CS8524 still requires an arm for unnamed values.
            _ => null,
        };
        if (statusFinding is not null)
        {
            findings.Add(statusFinding);
        }

        foreach (var pass in clock.Passes)
        {
            if (pass.DriftExceeded)
            {
                findings.Add(new Finding(
                    Severity.MeasurementCaveat,
                    "clock-drift",
                    pass.PassId,
                    $"clock drift |post - pre| = {VerbatimNumber.Cell(pass.DriftMs)} ms exceeds the declared {VerbatimNumber.Cell(clock.MaxDriftMs)} ms; the pass offset is still the pre/post midpoint and the correction is not rescaled"));
            }
        }

        AddLedgerFinding(findings, clock, clock.Corrected, corrected: true);
        AddLedgerFinding(findings, clock, clock.Uncorrected, corrected: false);
        if (clock.CorrectedError is not null || clock.UncorrectedError is not null)
        {
            var errors = new[] { clock.CorrectedError, clock.UncorrectedError }.OfType<string>();
            findings.Add(new Finding(
                Severity.HarnessError,
                "clock-ledger-unreadable",
                "campaign",
                string.Join("; ", errors)));
        }
    }

    private static void AddLedgerFinding(
        List<Finding> findings,
        ReleaseClockReport clock,
        ReleaseLedgerMismatch mismatch,
        bool corrected)
    {
        var read = corrected ? "corrected" : "uncorrected";
        if (!mismatch.Available)
        {
            findings.Add(new Finding(
                Severity.MeasurementCaveat,
                "clock-ledger-unavailable",
                "campaign",
                $"the {read} ledger read found no ledger; the clock evidence cannot be checked against an independent count"));
            return;
        }

        findings.Add(new Finding(
            Severity.Informational,
            corrected ? "clock-corrected-ledger" : "clock-uncorrected-ledger",
            "campaign",
            string.Create(
                CultureInfo.InvariantCulture,
                $"the {read} ledger read attributed {mismatch.Arms} arm window(s) in {mismatch.Files} file(s): {mismatch.ConnectionMismatches} TCP connection mismatch(es), {mismatch.DatagramMismatches} datagram mismatch(es), {mismatch.Unattributable} unattributable window(s)")));

        if (string.Equals(read, "corrected", StringComparison.Ordinal)
            && (mismatch.ConnectionMismatches > clock.Uncorrected.ConnectionMismatches
                || mismatch.DatagramMismatches > clock.Uncorrected.DatagramMismatches))
        {
            findings.Add(new Finding(
                Severity.MeasurementCaveat,
                "clock-correction-worse",
                "campaign",
                "the corrected ledger read disagrees with the client on more windows than the uncorrected read; the correction is evidence, not an improvement to assume"));
        }
    }
}
