using WinForward.E2E.Analysis.Metrics;

namespace WinForward.E2E.Analysis.Release;

/// <summary>Applies a gate matrix to a release dataset and explains each removed cell to the comparison.</summary>
/// <remarks>
/// The filter owns the one translation from a gate-invalidated pass to the per-pass readings the
/// comparison consumes. Keeping it out of <see cref="ReleaseComparison"/> makes the family-size and
/// interval rules read as decisions, not as dataset surgery.
/// </remarks>
internal static class ReleaseGateFilter
{
    /// <summary>Removes every gate-invalidated measured pass from the per-row metric readings.</summary>
    /// <param name="dataset">The measured readings.</param>
    /// <param name="gates">The matrix result.</param>
    /// <returns>The filtered dataset, or the same instance when nothing was invalidated.</returns>
    internal static ReleaseDataset Apply(ReleaseDataset dataset, ReleaseGateMatrixResult gates)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(gates);

        if (gates.Invalidations.Count == 0)
        {
            return dataset;
        }

        var measured = dataset.MeasuredPasses.ToHashSet(StringComparer.Ordinal);
        var rows = new List<ReleaseRowReadings>(dataset.Rows.Count);
        foreach (var row in dataset.Rows)
        {
            var metrics = new Dictionary<string, ReleaseMetricReadings>(StringComparer.Ordinal);
            foreach (var (metric, readings) in row.Metrics)
            {
                var byPass = gates.For(metric, row.RowId)
                    .Where(entry => measured.Contains(entry.Pass))
                    .GroupBy(entry => entry.Pass, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
                if (byPass.Count == 0)
                {
                    metrics[metric] = readings;
                    continue;
                }

                var values = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (passId, value) in readings.Values)
                {
                    if (!byPass.ContainsKey(passId))
                    {
                        values[passId] = value;
                    }
                }

                var reasons = new Dictionary<string, string>(readings.Reasons, StringComparer.Ordinal);
                foreach (var (passId, entries) in byPass)
                {
                    reasons[passId] = CellReason(entries);
                }

                var lost = byPass.Keys.Count(readings.Values.ContainsKey);
                metrics[metric] = readings with
                {
                    Values = values,
                    Reasons = reasons,
                    InvalidatedPasses = lost,
                    InvalidatedPassIDs = [.. byPass.Keys.Where(readings.Values.ContainsKey)],
                    MissingPasses = readings.MissingPasses + lost,
                };
            }

            rows.Add(row with { Metrics = metrics });
        }

        return dataset with { Rows = rows };
    }

    /// <summary>One row's readings for one metric, or null when the metric was not read for it.</summary>
    /// <param name="row">The row, or null.</param>
    /// <param name="metric">The metric identifier.</param>
    /// <returns>The readings, or null.</returns>
    internal static ReleaseMetricReadings? Reading(ReleaseRowReadings? row, string metric) =>
        row is not null && row.Metrics.TryGetValue(metric, out var readings) ? readings : null;

    /// <summary>The reason a pair leaves the family because a gate emptied its row for this metric.</summary>
    /// <param name="spec">The metric's declaration.</param>
    /// <param name="row">The compared row.</param>
    /// <returns>The exclusion reason, or null when the row still has a value.</returns>
    internal static string? Exclusion(MetricSpec spec, ReleaseRowReadings? row)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (row is null)
        {
            return null;
        }

        var readings = Reading(row, spec.Key);
        if (readings is null || readings.InvalidatedPasses == 0 || readings.Values.Count > 0)
        {
            return null;
        }

        var passes = readings.InvalidatedPassIDs;
        return $"a release gate invalidated all {passes.Count} measured pass(es) that supplied '{row.RowId}' for {spec.Key} ({string.Join(", ", passes)}), so the pair is outside the family";
    }

    /// <summary>The gate evidence a pair's reason carries when only some of its passes were removed.</summary>
    /// <param name="spec">The metric's declaration.</param>
    /// <param name="row">The compared row.</param>
    /// <param name="gates">The matrix result.</param>
    /// <param name="measuredPasses">The measured pass ids, in natural-key order.</param>
    /// <returns>The note, or null when no measured pass was invalidated.</returns>
    internal static string? Note(
        MetricSpec spec,
        ReleaseRowReadings? row,
        ReleaseGateMatrixResult gates,
        IReadOnlyList<string> measuredPasses)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(measuredPasses);

        if (row is null)
        {
            return null;
        }

        var passes = InvalidatedPasses(spec, row, measuredPasses);
        if (passes.Count == 0)
        {
            return null;
        }

        var removed = passes.ToHashSet(StringComparer.Ordinal);
        var used = gates.For(spec.Key, row.RowId)
            .Where(entry => removed.Contains(entry.Pass))
            .Select(entry => entry.Gate)
            .Distinct(StringComparer.Ordinal);
        return $"a release gate invalidated {passes.Count} measured pass(es) ({string.Join(", ", passes)}) for this pair: {string.Join(", ", used)}";
    }

    /// <summary>The measured passes the matrix removed from one pair, in natural-key order.</summary>
    /// <param name="spec">The metric's declaration.</param>
    /// <param name="row">The pair's row.</param>
    /// <param name="measuredPasses">The measured pass ids.</param>
    /// <returns>The invalidated pass ids, or an empty list.</returns>
    internal static IReadOnlyList<string> InvalidatedPasses(
        MetricSpec spec,
        ReleaseRowReadings row,
        IReadOnlyList<string> measuredPasses)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(measuredPasses);

        var readings = Reading(row, spec.Key);
        if (readings is null || readings.InvalidatedPassIDs.Count == 0)
        {
            return [];
        }

        var invalidated = readings.InvalidatedPassIDs.ToHashSet(StringComparer.Ordinal);
        return [.. measuredPasses.Where(invalidated.Contains)];
    }

    /// <summary>Every invalidation of one metric, row by row, in dataset order.</summary>
    /// <param name="metric">The metric identifier.</param>
    /// <param name="rows">The rows whose cells to collect.</param>
    /// <param name="gates">The matrix result.</param>
    /// <returns>The invalidations.</returns>
    internal static List<ReleaseGateInvalidation> Collect(
        string metric,
        IReadOnlyList<ReleaseRowReadings> rows,
        ReleaseGateMatrixResult gates)
    {
        ArgumentException.ThrowIfNullOrEmpty(metric);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(gates);

        var collected = new List<ReleaseGateInvalidation>();
        foreach (var row in rows)
        {
            collected.AddRange(gates.For(metric, row.RowId));
        }

        return collected;
    }

    private static string CellReason(IEnumerable<ReleaseGateInvalidation> entries) =>
        "gate invalidated: " + string.Join(
            "; ",
            entries.Select(entry => $"{entry.Gate} on {entry.Arm}: {entry.Reason}"));
}
