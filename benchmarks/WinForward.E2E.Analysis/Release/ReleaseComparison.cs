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
    public static ReleaseComparisonReport Evaluate(
        ThresholdsFile thresholds,
        ReleaseInputs inputs,
        ReleaseDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(dataset);

        EnsureInputsAgree(thresholds, inputs);
        EnsureDataset(inputs, dataset);

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
                    equivalence.GetValueOrDefault(primary.Metric)));
        }

        foreach (var metric in thresholds.Exploratory)
        {
            var spec = Spec(metric);
            metrics.Add(EvaluateExploratory(spec, thresholds.ReferenceRow, reference, rowOrder, inputs, dataset));
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
            capabilities);
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
        double? equivalenceMargin)
    {
        var referenceReadings = Read(reference, spec.Key);
        var referenceValues = referenceReadings?.Values ?? s_emptyValues;
        var familySize = rowOrder.Count(row =>
            IsDeclaredOpponent(opponents, row.RowId)
            && ReleasePairDecision.IsInFamily(spec, primary, referenceRow, reference, row, declaredOpponent: true));
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
                    dataset.Seed));
        }

        var (state, reason) = AggregatePrimary(pairs, familySize, reference, referenceReadings, referenceRow);
        var (zeroEventN, zeroEventUpperBound) = ZeroEventBound(reference, spec, dataset.MeasuredPasses);
        if (string.Equals(state, ReleaseState.Unevaluable, StringComparison.Ordinal)
            && zeroEventUpperBound is { } bound
            && zeroEventN is { } exposure)
        {
            reason = string.Concat(
                reason,
                "; zero-event rate: 3/",
                exposure.ToString(CultureInfo.InvariantCulture),
                " upper bound ",
                bound.ToString("0.#####", CultureInfo.InvariantCulture),
                " pp");
        }

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
            reason);
    }

    private static ReleaseMetricResult EvaluateExploratory(
        MetricSpec spec,
        string referenceRow,
        ReleaseRowReadings? reference,
        List<ReleaseRowReadings> rowOrder,
        ReleaseInputs inputs,
        ReleaseDataset dataset)
    {
        var referenceValues = Read(reference, spec.Key)?.Values ?? s_emptyValues;
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
                    dataset.Seed));
        }

        return new ReleaseMetricResult(
            spec.Key,
            ReleaseMetricRole.Exploratory,
            Kind: null,
            Band: null,
            Business: null,
            Mdd: null,
            referenceRow,
            DescriptiveStats.Median([.. referenceValues.Values]),
            referenceValues,
            FamilySize: 0,
            EquivalenceMargin: null,
            ZeroEventN: null,
            ZeroEventUpperBound: null,
            pairs,
            ReleaseState.ReportOnly,
            "exploratory metric: values and raw intervals are reported, and no difference or equivalence claim is made");
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

        var inFamily = pairs.Where(pair => pair.InFamily).ToList();
        if (inFamily.Count == 0 || familySize == 0)
        {
            return (ReleaseState.Unevaluable, "no comparable declared opponent contributed to the pre-registered family");
        }

        if (inFamily.TrueForAll(pair => string.Equals(pair.State, ReleaseState.Unevaluable, StringComparison.Ordinal)))
        {
            return (ReleaseState.Unevaluable, JoinReasons(inFamily));
        }

        if (inFamily.Exists(pair => string.Equals(pair.State, ReleaseState.Different, StringComparison.Ordinal)))
        {
            return (
                ReleaseState.Different,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{AdjustmentLabel}: at least one of {familySize} adjusted intervals excludes the decision band"));
        }

        if (inFamily.TrueForAll(pair => string.Equals(pair.State, ReleaseState.Equivalent, StringComparison.Ordinal)))
        {
            return (
                ReleaseState.Equivalent,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"every one of {familySize} in-family raw interval(s) lies inside the pre-registered TOST margin; unadjusted and declared"));
        }

        return (
            ReleaseState.Inconclusive,
            string.Create(
                CultureInfo.InvariantCulture,
                $"no adjusted interval excludes the band; at least one of {familySize} in-family pair(s) is inconclusive"));
    }

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

    private static ReleaseMetricReadings? Read(ReleaseRowReadings? row, string metric) =>
        row is not null && row.Metrics.TryGetValue(metric, out var readings) ? readings : null;

    private static string JoinReasons(IEnumerable<ReleasePairResult> pairs)
    {
        var reasons = pairs
            .Select(pair => pair.Reason)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();
        return reasons.Count == 0 ? "no pair produced a reason" : string.Join("; ", reasons);
    }

    private static void EnsureInputsAgree(ThresholdsFile thresholds, ReleaseInputs inputs)
    {
        if (!string.Equals(inputs.ReferenceRow, thresholds.ReferenceRow, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"the release inputs name reference row '{inputs.ReferenceRow}', but the thresholds file names '{thresholds.ReferenceRow}'",
                nameof(inputs));
        }

        if (!inputs.Opponents.SequenceEqual(thresholds.Opponents, StringComparer.Ordinal))
        {
            throw new ArgumentException("the release inputs' opponents are not the thresholds file's opponents", nameof(inputs));
        }

        if (!inputs.Primary.SequenceEqual(thresholds.Primary))
        {
            throw new ArgumentException("the release inputs' primary family is not the thresholds file's primary family", nameof(inputs));
        }
    }

    private static void EnsureDataset(ReleaseInputs inputs, ReleaseDataset dataset)
    {
        if (dataset.Resamples < 1)
        {
            throw new ArgumentException("the release dataset needs at least one resample", nameof(dataset));
        }

        if (dataset.MeasuredPasses.Count == 0)
        {
            throw new ArgumentException("the release dataset holds no measured pass", nameof(dataset));
        }

        EnsurePassCounts(inputs);
        EnsureMeasuredPasses(inputs, dataset);
        EnsureRows(dataset);
    }

    private static void EnsurePassCounts(ReleaseInputs inputs)
    {
        if (inputs.Passes.N != inputs.Passes.Measured.Count || inputs.Passes.N == 0)
        {
            throw new ArgumentException("the release inputs' measured pass count does not match their measured pass list", nameof(inputs));
        }

        if (inputs.Passes.Required < ReleaseOptions.MinimumMinPasses)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the release inputs require {inputs.Passes.Required} passes, below the minimum of {ReleaseOptions.MinimumMinPasses}"),
                nameof(inputs));
        }

        var duplicate = inputs.Passes.Measured
            .GroupBy(number => number)
            .FirstOrDefault(group => group.Skip(1).Any())?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"the release inputs list pass {duplicate} twice"),
                nameof(inputs));
        }
    }

    private static void EnsureMeasuredPasses(ReleaseInputs inputs, ReleaseDataset dataset)
    {
        var numbers = dataset.MeasuredPasses
            .Select(passId => ParsePass(passId, nameof(dataset)))
            .ToList();
        var duplicate = numbers
            .GroupBy(number => number)
            .FirstOrDefault(group => group.Skip(1).Any())?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"the release dataset lists pass {duplicate} twice"),
                nameof(dataset));
        }

        if (!numbers.ToHashSet().SetEquals(inputs.Passes.Measured))
        {
            throw new ArgumentException("the release dataset's measured passes are not the release inputs' measured passes", nameof(dataset));
        }
    }

    private static void EnsureRows(ReleaseDataset dataset)
    {
        var duplicate = dataset.Rows
            .GroupBy(row => row.RowId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any())?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"the release dataset lists row '{duplicate}' twice",
                nameof(dataset));
        }
    }

    private static int ParsePass(string passId, string parameterName) =>
        ReleasePasses.TryNumber(passId, out var number)
            ? number
            : throw new ArgumentException(
                $"the release dataset holds an unnumbered pass '{passId}'",
                parameterName);

}
