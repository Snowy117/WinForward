using System.Globalization;
using WinForward.E2E.Analysis.Metrics;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// The release comparison and decision model: per metric, the reference row against every row the
/// campaign measured, the raw 95 % interval, the Bonferroni-adjusted family-wise interval, and the state
/// those intervals support.
/// </summary>
/// <remarks>
/// <para><b>There is no p-value and no <c>same</c>.</b> The decision is an interval exclusion: a pair is
/// <c>different</c> only when its adjusted interval lies wholly outside the decision band. An interval
/// that overlaps the band is <c>inconclusive</c>; a zero-width or degenerate interval is
/// <c>unevaluable</c>; and a positive equivalence claim exists only where the thresholds file
/// pre-registered a TOST family, in which case the raw interval is read against the declared margin and
/// the claim is marked unadjusted.</para>
/// <para><b>The adjustment is Bonferroni, not Holm.</b> For one metric with <c>m</c> in-family pairs,
/// every adjusted interval covers <c>1 - 0.05 / m</c>; the report names it
/// <see cref="AdjustmentLabel"/>. A per-pair p-value would be a different statistic and is deliberately
/// absent.</para>
/// </remarks>
public static class ReleaseComparison
{
    /// <summary>The family-wise error rate the adjusted intervals control.</summary>
    public const double FamilywiseAlpha = 0.05;

    /// <summary>The label every adjusted decision and the report use instead of a p-value name.</summary>
    public const string AdjustmentLabel = "Bonferroni-adjusted, family-wise 95 %";

    private static readonly IReadOnlyDictionary<string, double> s_emptyValues =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>
    /// Compares every declared primary and exploratory metric and evaluates every capability gate.
    /// </summary>
    /// <param name="thresholds">The pre-registered thresholds file the release run is judged against.</param>
    /// <param name="inputs">The release inputs that selected the measured passes and the family.</param>
    /// <param name="dataset">The per-pass readings for the campaign's measured passes.</param>
    /// <returns>The decision document, before any report file is written.</returns>
    /// <exception cref="ArgumentException">
    /// The inputs are not the record built from this thresholds file, or the dataset's measured passes are
    /// not the inputs' measured passes.
    /// </exception>
    /// <param name="gates">The gate matrix result to apply, or null when the campaign's gates are clean.</param>
    public static ReleaseComparisonReport Evaluate(
        ThresholdsFile thresholds,
        ReleaseInputs inputs,
        ReleaseDataset dataset,
        ReleaseGateMatrixResult? gates = null)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(dataset);

        gates ??= ReleaseGateMatrixResult.Empty;
        ReleaseComparisonValidation.InputsAgree(thresholds, inputs);
        ReleaseComparisonValidation.Dataset(inputs, dataset);
        dataset = ReleaseGateFilter.Apply(dataset, gates);

        var reference = dataset.Rows.FirstOrDefault(row =>
            string.Equals(row.RowId, thresholds.ReferenceRow, StringComparison.Ordinal));
        var rowOrder = OrderRows(dataset.Rows, thresholds.ReferenceRow, thresholds.Opponents);
        var equivalence = thresholds.Equivalence.ToDictionary(
            family => family.Metric,
            family => family.Margin,
            StringComparer.Ordinal);

        var metrics = new List<ReleaseMetricResult>(thresholds.Primary.Count + thresholds.Exploratory.Count);
        foreach (var primary in thresholds.Primary)
        {
            var spec = Spec(primary.Metric);
            metrics.Add(
                EvaluatePrimary(
                    spec,
                    primary,
                    thresholds.ReferenceRow,
                    reference,
                    rowOrder,
                    thresholds.Opponents,
                    inputs,
                    dataset,
                    equivalence.GetValueOrDefault(primary.Metric),
                    gates));
        }

        foreach (var metric in thresholds.Exploratory)
        {
            var spec = Spec(metric);
            metrics.Add(EvaluateExploratory(spec, thresholds.ReferenceRow, reference, rowOrder, inputs, dataset, gates));
        }

        var capabilities = ReleaseCapabilityGate.Evaluate(thresholds, dataset, reference, rowOrder);
        return new ReleaseComparisonReport(
            thresholds.ReferenceRow,
            thresholds.Opponents,
            dataset.MeasuredPasses,
            inputs.Passes.N,
            inputs.Passes.Required,
            inputs.Passes.Insufficient,
            FamilywiseAlpha,
            metrics,
            capabilities)
        {
            Gates = gates,
        };
    }

    private static ReleaseMetricResult EvaluatePrimary(
        MetricSpec spec,
        PrimaryMetric primary,
        string referenceRow,
        ReleaseRowReadings? reference,
        List<ReleaseRowReadings> rowOrder,
        IReadOnlyList<string> opponents,
        ReleaseInputs inputs,
        ReleaseDataset dataset,
        double? equivalenceMargin,
        ReleaseGateMatrixResult gates)
    {
        var referenceReadings = ReleaseGateFilter.Reading(reference, spec.Key);
        var referenceValues = referenceReadings?.Values ?? s_emptyValues;
        var familySize = rowOrder.Count(row =>
            IsDeclaredOpponent(opponents, row.RowId)
            && ReleasePairDecision.IsInFamily(spec, primary, referenceRow, reference, row, declaredOpponent: true)
            && ReleaseGateFilter.Exclusion(spec, row) is null);
        var pairs = EvaluatePairs(
            spec,
            primary,
            referenceRow,
            reference,
            rowOrder,
            opponents,
            inputs,
            dataset,
            familySize,
            equivalenceMargin,
            gates);

        var (state, reason) = AggregatePrimary(pairs, familySize, reference, referenceReadings, referenceRow);
        if (ReleaseGateFilter.Note(spec, reference, gates, dataset.MeasuredPasses) is { } referenceGateNote)
        {
            reason = $"{reason}; {referenceGateNote}";
        }

        var (zeroEventN, zeroEventUpperBound) = ZeroEventBound(reference, spec, dataset.MeasuredPasses);
        reason = ZeroEventReason(reason, state, zeroEventN, zeroEventUpperBound);

        var decision = DecisionPair(pairs, state);

        return new ReleaseMetricResult(
            spec.Key,
            ReleaseMetricRole.Primary,
            primary.Kind,
            primary.Band,
            primary.Business,
            primary.Mdd,
            referenceRow,
            DescriptiveStats.Median([.. referenceValues.Values]),
            referenceValues,
            familySize,
            equivalenceMargin,
            zeroEventN,
            zeroEventUpperBound,
            pairs,
            state,
            reason)
        {
            GateInvalidations = ReleaseGateFilter.Collect(spec.Key, dataset.Rows, gates),
            DecisionPair = decision?.RowId,
            Median = decision?.Median,
            RawInterval = decision?.RawInterval,
            AdjustedInterval = decision?.AdjustedInterval,
        };
    }

    /// <summary>Evaluates every row against the reference, with the family size already fixed.</summary>
    private static List<ReleasePairResult> EvaluatePairs(
        MetricSpec spec,
        PrimaryMetric primary,
        string referenceRow,
        ReleaseRowReadings? reference,
        List<ReleaseRowReadings> rowOrder,
        IReadOnlyList<string> opponents,
        ReleaseInputs inputs,
        ReleaseDataset dataset,
        int familySize,
        double? equivalenceMargin,
        ReleaseGateMatrixResult gates)
    {
        var pairs = new List<ReleasePairResult>(rowOrder.Count);
        foreach (var row in rowOrder)
        {
            pairs.Add(
                ReleasePairDecision.Evaluate(
                    spec,
                    primary,
                    referenceRow,
                    reference,
                    row,
                    IsDeclaredOpponent(opponents, row.RowId),
                    dataset.MeasuredPasses,
                    inputs.Passes.Required,
                    inputs.Passes.Insufficient,
                    familySize,
                    equivalenceMargin,
                    dataset.Resamples,
                    dataset.Seed,
                    ReleaseGateFilter.Exclusion(spec, row),
                    ReleaseGateFilter.Note(spec, row, gates, dataset.MeasuredPasses),
                    ReleaseGateFilter.InvalidatedPasses(spec, row, dataset.MeasuredPasses)));
        }

        return pairs;
    }

    /// <summary>Adds the rule-of-three bound to an unevaluable zero-event rate's reason.</summary>
    private static string ZeroEventReason(string reason, string state, long? n, double? upperBound) =>
        string.Equals(state, ReleaseState.Unevaluable, StringComparison.Ordinal)
            && upperBound is { } bound
            && n is { } exposure
            ? string.Concat(
                reason,
                "; zero-event rate: 3/",
                exposure.ToString(CultureInfo.InvariantCulture),
                " upper bound ",
                bound.ToString("0.#####", CultureInfo.InvariantCulture),
                " pp")
            : reason;

    private static ReleaseMetricResult EvaluateExploratory(
        MetricSpec spec,
        string referenceRow,
        ReleaseRowReadings? reference,
        List<ReleaseRowReadings> rowOrder,
        ReleaseInputs inputs,
        ReleaseDataset dataset,
        ReleaseGateMatrixResult gates)
    {
        var referenceValues = ReleaseGateFilter.Reading(reference, spec.Key)?.Values ?? s_emptyValues;
        var pairs = new List<ReleasePairResult>(rowOrder.Count);
        foreach (var row in rowOrder)
        {
            pairs.Add(
                ReleasePairDecision.Evaluate(
                    spec,
                    primary: null,
                    referenceRow,
                    reference,
                    row,
                    declaredOpponent: false,
                    dataset.MeasuredPasses,
                    inputs.Passes.Required,
                    inputs.Passes.Insufficient,
                    familySize: 0,
                    equivalenceMargin: null,
                    dataset.Resamples,
                    dataset.Seed,
                    ReleaseGateFilter.Exclusion(spec, row),
                    ReleaseGateFilter.Note(spec, row, gates, dataset.MeasuredPasses),
                    ReleaseGateFilter.InvalidatedPasses(spec, row, dataset.MeasuredPasses)));
        }

        var referenceMedian = DescriptiveStats.Median([.. referenceValues.Values]);

        return new ReleaseMetricResult(
            spec.Key,
            ReleaseMetricRole.Exploratory,
            Kind: null,
            Band: null,
            Business: null,
            Mdd: null,
            referenceRow,
            referenceMedian,
            referenceValues,
            FamilySize: 0,
            EquivalenceMargin: null,
            ZeroEventN: null,
            ZeroEventUpperBound: null,
            pairs,
            ReleaseState.ReportOnly,
            "exploratory metric: values and raw intervals are reported, and no difference or equivalence claim is made")
        {
            GateInvalidations = ReleaseGateFilter.Collect(spec.Key, dataset.Rows, gates),
            Median = referenceMedian,
        };
    }

    /// <summary>The metric-level state over the in-family pairs; outside pairs never contribute.</summary>
    private static (string State, string Reason) AggregatePrimary(
        IReadOnlyList<ReleasePairResult> pairs,
        int familySize,
        ReleaseRowReadings? reference,
        ReleaseMetricReadings? referenceReadings,
        string referenceRow)
    {
        if (reference is null)
        {
            return (ReleaseState.Unevaluable, $"the reference row '{referenceRow}' was not measured");
        }

        if (referenceReadings is null)
        {
            return (ReleaseState.Unevaluable, $"the reference row '{referenceRow}' was not read for this metric");
        }

        if (referenceReadings.Status is not (ReleaseReadingStatus.Ok or ReleaseReadingStatus.DnsCarriage))
        {
            return (
                ReleaseState.Unevaluable,
                $"the reference row '{referenceRow}' did not run the metric: {referenceReadings.StatusReason ?? referenceReadings.Status}");
        }

        if (referenceReadings.Values.Count == 0 && referenceReadings.InvalidatedPasses > 0)
        {
            return (
                ReleaseState.Unevaluable,
                $"a release gate invalidated every measured pass of the reference row '{referenceRow}' for this metric");
        }

        var inFamily = pairs.Where(pair => pair.InFamily).ToList();
        if (inFamily.Count == 0 || familySize == 0)
        {
            return (ReleaseState.Unevaluable, "no comparable declared opponent contributed to the pre-registered family");
        }

        var state = AggregateState(inFamily);
        return (state, AggregateReason(state, inFamily, familySize));
    }

    /// <summary>The aggregate state, by severity: different, then inconclusive, then unevaluable, then equivalent.</summary>
    /// <remarks>
    /// The order is the decision the family can actually support: one different pair is a difference even
    /// when another pair is unevaluable; one inconclusive pair keeps the family inconclusive even when
    /// another is unevaluable; equivalence is the weakest claim and is returned only when every in-family
    /// pair is equivalent. A mixture therefore never becomes a state no pair carried.
    /// </remarks>
    private static string AggregateState(IReadOnlyList<ReleasePairResult> inFamily)
    {
        if (inFamily.Any(pair => string.Equals(pair.State, ReleaseState.Different, StringComparison.Ordinal)))
        {
            return ReleaseState.Different;
        }

        if (inFamily.Any(pair => string.Equals(pair.State, ReleaseState.Inconclusive, StringComparison.Ordinal)))
        {
            return ReleaseState.Inconclusive;
        }

        if (inFamily.Any(pair => string.Equals(pair.State, ReleaseState.Unevaluable, StringComparison.Ordinal)))
        {
            return ReleaseState.Unevaluable;
        }

        return inFamily.All(pair => string.Equals(pair.State, ReleaseState.Equivalent, StringComparison.Ordinal))
            ? ReleaseState.Equivalent
            : ReleaseState.Unevaluable;

    }

    private static string AggregateReason(string state, IReadOnlyList<ReleasePairResult> inFamily, int familySize)
    {
        var reason = state switch
        {
            ReleaseState.Different => $"{AdjustmentLabel}: at least one of {familySize.ToString(CultureInfo.InvariantCulture)} adjusted intervals excludes the decision band",
            ReleaseState.Inconclusive => $"no adjusted interval excludes the band; at least one of {familySize.ToString(CultureInfo.InvariantCulture)} in-family pair(s) is inconclusive",
            ReleaseState.Unevaluable => $"no pair is different or inconclusive; at least one of {familySize.ToString(CultureInfo.InvariantCulture)} in-family pair(s) is unevaluable",
            _ => $"every one of {familySize.ToString(CultureInfo.InvariantCulture)} in-family raw interval(s) lies inside the pre-registered TOST margin; unadjusted and declared",
        };

        var others = new[] { ReleaseState.Different, ReleaseState.Inconclusive, ReleaseState.Unevaluable, ReleaseState.Equivalent }
            .Where(candidate => !string.Equals(candidate, state, StringComparison.Ordinal))
            .Select(candidate => (State: candidate, Count: inFamily.Count(pair => string.Equals(pair.State, candidate, StringComparison.Ordinal))))
            .Where(entry => entry.Count > 0)
            .Select(entry => $"{entry.Count} pair(s) are {entry.State}")
            .ToList();
        return others.Count == 0 ? reason : $"{reason}; {string.Join(", ", others)}";
    }

    /// <summary>The pair whose interval is the evidence for the aggregate state.</summary>
    /// <remarks>
    /// The aggregate state is chosen first; this then picks the first in-family pair carrying that state,
    /// preferring a pair that has an adjusted interval and tie-breaking by row id. The interval the
    /// metric publishes is therefore never a pair that did not carry the aggregate and never a pair with
    /// no interval when one with an interval exists.
    /// </remarks>
    private static ReleasePairResult? DecisionPair(IReadOnlyList<ReleasePairResult> pairs, string state) =>
        pairs
            .Where(pair => pair.InFamily && string.Equals(pair.State, state, StringComparison.Ordinal))
            .OrderByDescending(pair => pair.AdjustedInterval is not null)
            .ThenBy(pair => pair.RowId, StringComparer.Ordinal)
            .FirstOrDefault()
        ?? pairs
            .Where(pair => pair.InFamily)
            .OrderByDescending(pair => pair.AdjustedInterval is not null)
            .ThenBy(pair => pair.RowId, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>The rule-of-three exposure behind a zero-event rate, when the reference row has counts.</summary>
    private static (long? N, double? UpperBound) ZeroEventBound(
        ReleaseRowReadings? reference,
        MetricSpec spec,
        IReadOnlyList<string> passIds)
    {
        if (reference is null
            || !reference.Metrics.TryGetValue(spec.Key, out var readings)
            || readings.Counts.Count == 0)
        {
            return (null, null);
        }

        long events = 0;
        long attempts = 0;
        foreach (var passId in passIds)
        {
            if (!readings.Counts.TryGetValue(passId, out var count)
                || count.Numerator < 0
                || count.Denominator < 0
                || events > long.MaxValue - count.Numerator
                || attempts > long.MaxValue - count.Denominator)
            {
                return (null, null);
            }

            events += count.Numerator;
            attempts += count.Denominator;
        }

        return events == 0 && attempts > 0 ? (attempts, 300.0 / attempts) : (null, null);
    }

    /// <summary>Declared opponents first, in the thresholds file's order, then every other measured row.</summary>
    private static List<ReleaseRowReadings> OrderRows(
        IReadOnlyList<ReleaseRowReadings> rows,
        string referenceRow,
        IReadOnlyList<string> opponents)
    {
        var byId = rows.ToDictionary(row => row.RowId, StringComparer.Ordinal);
        var ordered = new List<ReleaseRowReadings>(rows.Count);
        ordered.AddRange(
            opponents
                .Select(byId.GetValueOrDefault)
                .OfType<ReleaseRowReadings>()
                .Where(row => !string.Equals(row.RowId, referenceRow, StringComparison.Ordinal)));
        ordered.AddRange(
            rows.Where(row =>
                !string.Equals(row.RowId, referenceRow, StringComparison.Ordinal)
                && !IsDeclaredOpponent(opponents, row.RowId)));

        return ordered;
    }

    private static bool IsDeclaredOpponent(IReadOnlyList<string> opponents, string rowId) =>
        opponents.Any(opponent => string.Equals(opponent, rowId, StringComparison.Ordinal));

    private static MetricSpec Spec(string key) =>
        MetricCatalogue.Specs.FirstOrDefault(spec => string.Equals(spec.Key, key, StringComparison.Ordinal))
        ?? throw new ArgumentException($"release: the thresholds file names an unknown metric '{key}'", nameof(key));



}
