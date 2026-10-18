using System.Globalization;

namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// Evaluates the declared capability gates: a pooled event rate, its exposure and the rule-of-three
/// upper bound, never a reference-vs-opponent comparison.
/// </summary>
/// <remarks>
/// <b>The bound is the claim.</b> A zero-event sample is the only sample the rule of three can bound, so
/// the gate passes only when there are zero events and <c>3/n</c> is at or below the declared threshold.
/// A single event means the zero-event <c>3/n</c> bound no longer applies; the gate fails and reports
/// no upper bound. A zero denominator is <c>not-measured</c> instead of a vacuous pass.
/// </remarks>
internal static class ReleaseCapabilityGate
{
    /// <summary>Every declared capability metric, in the thresholds file's metric order, for every row.</summary>
    internal static IReadOnlyList<ReleaseCapabilityResult> Evaluate(
        ThresholdsFile thresholds,
        ReleaseDataset dataset,
        ReleaseRowReadings? reference,
        IReadOnlyList<ReleaseRowReadings> orderedRows)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(orderedRows);

        var rows = new List<ReleaseRowReadings>(orderedRows.Count + 1);
        if (reference is not null)
        {
            rows.Add(reference);
        }

        rows.AddRange(orderedRows);

        var results = new List<ReleaseCapabilityResult>();
        foreach (var rule in thresholds.Capability.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var row in rows)
            {
                results.Add(EvaluateOne(rule.Value, dataset.MeasuredPasses, row));
            }
        }

        return results;
    }

    private static ReleaseCapabilityResult EvaluateOne(
        CapabilityRule rule,
        IReadOnlyList<string> passIds,
        ReleaseRowReadings row)
    {
        var read = ReadCounts(rule, passIds, row);
        if (read.Error is not null)
        {
            return NotMeasured(rule, row, read.Error);
        }

        return read.Attempts == 0
            ? NotMeasured(rule, row, "no exposure: the summed denominator is zero")
            : Decide(rule, row, read);
    }

    /// <summary>The summed counts and the reason a row cannot supply them.</summary>
    private readonly record struct CountRead(
        long Events,
        long Attempts,
        IReadOnlyList<ReleaseCapabilityPass> Passes,
        string? Error);

    private static CountRead ReadCounts(
        CapabilityRule rule,
        IReadOnlyList<string> passIds,
        ReleaseRowReadings row)
    {
        if (rule.NFrom is not null
            && !string.Equals(rule.NFrom, "connectAttempts", StringComparison.Ordinal))
        {
            return new CountRead(
                0,
                0,
                [],
                $"the release count reader does not sample the declared nFrom '{rule.NFrom}'");
        }

        if (!row.Metrics.TryGetValue(rule.Metric, out var readings))
        {
            return new CountRead(0, 0, [], "the row was not read for this metric");
        }

        if (readings.Status is ReleaseReadingStatus.NotInPlan
            or ReleaseReadingStatus.DeclaredAbsent
            or ReleaseReadingStatus.NotCarried)
        {
            return new CountRead(0, 0, [], readings.StatusReason ?? readings.Status);
        }

        if (readings.Counts.Count == 0)
        {
            return new CountRead(0, 0, [], "the metric has no per-pass counts");
        }

        var passes = new List<ReleaseCapabilityPass>(passIds.Count);
        long events = 0;
        long attempts = 0;
        foreach (var passId in passIds)
        {
            if (!readings.Counts.TryGetValue(passId, out var count))
            {
                return new CountRead(0, 0, [], $"no count was read for {passId}");
            }

            if (count.Numerator < 0 || count.Denominator < 0)
            {
                return new CountRead(0, 0, [], $"the counts for {passId} are negative");
            }

            if (events > long.MaxValue - count.Numerator || attempts > long.MaxValue - count.Denominator)
            {
                return new CountRead(0, 0, [], "the counts overflow a 64-bit integer");
            }

            events += count.Numerator;
            attempts += count.Denominator;
            passes.Add(new ReleaseCapabilityPass(passId, count.Numerator, count.Denominator));
        }
        return new CountRead(Events: events, Attempts: attempts, Passes: passes, Error: null);
    }

    private static ReleaseCapabilityResult Decide(CapabilityRule rule, ReleaseRowReadings row, CountRead read)
    {
        var rate = read.Events * 100.0 / read.Attempts;
        if (read.Events > 0)
        {
            return Measured(
                rule,
                row,
                rate,
                read.Events,
                read.Attempts,
                upper: null,
                ReleaseCapabilityStatus.Fail,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{read.Events} event(s) in {read.Attempts} attempt(s); the zero-event 3/n upper bound does not apply, so the gate fails"),
                read.Passes);
        }

        var upper = 300.0 / read.Attempts;
        if (upper <= rule.UpperBound)
        {
            return Measured(
                rule,
                row,
                rate,
                read.Events,
                read.Attempts,
                upper,
                ReleaseCapabilityStatus.Pass,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"zero events; 3/{read.Attempts} upper bound {upper:0.#####} pp <= {rule.UpperBound:0.#####} pp"),
                read.Passes);
        }

        return Measured(
            rule,
            row,
            rate,
            read.Events,
            read.Attempts,
            upper,
            ReleaseCapabilityStatus.Fail,
            string.Create(
                CultureInfo.InvariantCulture,
                $"zero events, but 3/{read.Attempts} upper bound {upper:0.#####} pp exceeds {rule.UpperBound:0.#####} pp"),
            read.Passes);
    }

    private static ReleaseCapabilityResult Measured(
        CapabilityRule rule,
        ReleaseRowReadings row,
        double rate,
        long events,
        long attempts,
        double? upper,
        string status,
        string reason,
        IReadOnlyList<ReleaseCapabilityPass> passes) =>
        new(rule.Metric, row.RowId, rule.NFrom, rate, events, attempts, upper, rule.UpperBound, status, reason, passes);

    private static ReleaseCapabilityResult NotMeasured(
        CapabilityRule rule,
        ReleaseRowReadings row,
        string reason) =>
        new(
            rule.Metric,
            row.RowId,
            rule.NFrom,
            Rate: null,
            Events: null,
            N: null,
            UpperBound: null,
            rule.UpperBound,
            ReleaseCapabilityStatus.NotMeasured,
            reason,
            []);
}
