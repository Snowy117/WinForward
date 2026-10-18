using System.Globalization;
using WinForward.E2E.Analysis.Metrics;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// Decides one reference-vs-row pair: whether it belongs to the pre-registered family, the raw and
/// Bonferroni-adjusted intervals, and the state those intervals support.
/// </summary>
/// <remarks>
/// <b>The family is decided before the adjustment.</b> A pair enters the family only when the row is a
/// declared opponent and both sides ran the metric on a comparable carriage; the resulting family size
/// is the <c>m</c> every in-family interval is adjusted by. A row outside the family still gets its raw
/// interval where one can be drawn, but its state is <c>report-only</c> and it never changes a decision.
/// </remarks>
internal static class ReleasePairDecision
{
    private static readonly IReadOnlyDictionary<string, double> s_emptyValues =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Whether one pair belongs to the metric's pre-registered family.</summary>
    internal static bool IsInFamily(
        MetricSpec spec,
        PrimaryMetric? primary,
        string referenceRow,
        ReleaseRowReadings? reference,
        ReleaseRowReadings row,
        bool declaredOpponent) =>
        ReleasePairEligibility.Ineligibility(spec, primary, referenceRow, reference, row, declaredOpponent) is null;

    /// <summary>One pair's result, including every reason it could not make a claim.</summary>
    internal static ReleasePairResult Evaluate(
        MetricSpec spec,
        PrimaryMetric? primary,
        string referenceRow,
        ReleaseRowReadings? reference,
        ReleaseRowReadings row,
        bool declaredOpponent,
        IReadOnlyList<string> passIds,
        int minimumPasses,
        bool insufficient,
        int familySize,
        double? equivalenceMargin,
        int resamples,
        int seed,
        string? gateExclusion = null,
        string? gateNote = null,
        IReadOnlyList<string>? gateInvalidatedPasses = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(passIds);

        var facts = Facts(
            spec,
            primary,
            referenceRow,
            reference,
            row,
            declaredOpponent,
            passIds,
            equivalenceMargin,
            resamples,
            seed);

        if (gateExclusion is not null)
        {
            return Tagged(
                Result(
                    row.RowId,
                    ReleaseState.Unevaluable,
                    gateExclusion,
                    inFamily: false,
                    gateExclusion,
                    default,
                    adjusted: null,
                    facts.ReferenceMedian,
                    facts.RowMedian,
                    facts.PassesUsed,
                    margin: null),
                gateNote: null,
                gateInvalidatedPasses);
        }

        ReleasePairResult result;
        if (primary is null)
        {
            result = ReportOnly(
                row,
                facts,
                "exploratory metric: values and the raw interval are reported, and no difference or equivalence claim is made",
                "exploratory metric: no decision family");
        }
        else if (!declaredOpponent)
        {
            result = ReportOnly(
                row,
                facts,
                "row is not a declared opponent: internal and extra rows are exploratory and report-only",
                "row is not a declared opponent: internal and extra rows are exploratory");
        }
        else
        {
            result = facts.Included
                ? InFamily(row, primary, facts, minimumPasses, insufficient, familySize)
                : Excluded(row, facts);
        }
        return Tagged(result, gateNote, gateInvalidatedPasses);
    }


    private static PairFacts Facts(
        MetricSpec spec,
        PrimaryMetric? primary,
        string referenceRow,
        ReleaseRowReadings? reference,
        ReleaseRowReadings row,
        bool declaredOpponent,
        IReadOnlyList<string> passIds,
        double? equivalenceMargin,
        int resamples,
        int seed)
    {
        var referenceValues = ReleasePairEligibility.Reading(reference, spec.Key)?.Values ?? s_emptyValues;
        var rowValues = ReleasePairEligibility.Reading(row, spec.Key)?.Values ?? s_emptyValues;
        var ineligibility = ReleasePairEligibility.Ineligibility(spec, primary, referenceRow, reference, row, declaredOpponent);
        var inFamily = primary is not null && ineligibility is null;
        return new PairFacts(
            referenceValues,
            rowValues,
            DescriptiveStats.Median([.. referenceValues.Values]),
            DescriptiveStats.Median([.. rowValues.Values]),
            passIds.Count(passId => referenceValues.ContainsKey(passId) && rowValues.ContainsKey(passId)),
            ineligibility,
            inFamily,
            inFamily ? equivalenceMargin : null,
            ReleasePairEligibility.KindFor(spec, primary),
            resamples,
            seed);
    }

    private static ReleasePairResult ReportOnly(
        ReleaseRowReadings row,
        PairFacts facts,
        string reason,
        string ineligibility)
    {
        var draw = TryDrawRaw(facts.Kind, facts.ReferenceValues, facts.RowValues, facts.PassesUsed, facts.Resamples, facts.Seed);
        return Result(
            row.RowId,
            ReleaseState.ReportOnly,
            AppendDrawReason(reason, draw),
            inFamily: false,
            ineligibility,
            new PairDraw(Median: draw.Median, Raw: draw.Interval, Adjusted: null),
            adjusted: null,
            facts.ReferenceMedian,
            facts.RowMedian,
            facts.PassesUsed,
            margin: null);
    }

    private static ReleasePairResult Excluded(ReleaseRowReadings row, PairFacts facts) =>
        Result(
            row.RowId,
            ReleaseState.Unevaluable,
            facts.Exclusion ?? "the pair is outside the pre-registered family",
            inFamily: false,
            facts.Exclusion,
            default,
            adjusted: null,
            facts.ReferenceMedian,
            facts.RowMedian,
            facts.PassesUsed,
            margin: null);

    private static ReleasePairResult InFamily(
        ReleaseRowReadings row,
        PrimaryMetric primary,
        PairFacts facts,
        int minimumPasses,
        bool insufficient,
        int familySize)
    {
        if (insufficient)
        {
            return Underpowered(row, facts, minimumPasses, familySize);
        }

        if (facts.ReferenceValues.Count == 0 || facts.RowValues.Count == 0)
        {
            return UnevaluablePair(
                row,
                facts,
                "one side has no per-pass value for this metric; a missing denominator is unevaluable");
        }

        if (facts.PassesUsed < minimumPasses)
        {
            return Underpowered(row, facts, minimumPasses, familySize);
        }

        var pair = DrawBoth(facts.Kind, facts.ReferenceValues, facts.RowValues, facts.Resamples, facts.Seed, familySize);
        if (pair.Error is { } error)
        {
            return UnevaluablePair(row, facts, error, pair);
        }

        if (pair.Raw is not { } raw || pair.Adjusted is not { } adjusted)
        {
            return UnevaluablePair(row, facts, "the percentile interval could not be drawn", pair);
        }

        if (pair.Degenerate || raw.Low >= raw.High || adjusted.Low >= adjusted.High)
        {
            var reason = IsZeroEvent(facts.ReferenceValues, facts.RowValues)
                ? "zero-variance or degenerate bootstrap interval: the rate has no spread across passes; a zero-event rate is unevaluable, never equivalent"
                : "degenerate bootstrap interval (zero variance across passes)";
            return UnevaluablePair(row, facts, reason, pair);
        }

        var (verdictState, verdictReason) = Decide(primary, raw, adjusted, facts.Margin, familySize);

        return Result(
            row.RowId,
            verdictState,
            verdictReason,
            inFamily: true,
            ineligibility: null,
            new PairDraw(pair.Median, raw, adjusted),
            adjusted,
            facts.ReferenceMedian,
            facts.RowMedian,
            facts.PassesUsed,
            facts.Margin);
    }

    private static ReleasePairResult Underpowered(
        ReleaseRowReadings row,
        PairFacts facts,
        int minimumPasses,
        int familySize)
    {
        var draw = facts.CanDraw
            ? DrawBoth(facts.Kind, facts.ReferenceValues, facts.RowValues, facts.Resamples, facts.Seed, familySize)
            : default;
        var reason = string.Create(
            CultureInfo.InvariantCulture,
            $"only {facts.PassesUsed} measured pass(es) contribute; at least {minimumPasses} are needed");
        return Result(
            row.RowId,
            ReleaseState.Inconclusive,
            reason,
            inFamily: true,
            ineligibility: null,
            draw.Raw is not null ? new PairDraw(draw.Median, draw.Raw, draw.Adjusted) : default,
            draw.Adjusted,
            facts.ReferenceMedian,
            facts.RowMedian,
            facts.PassesUsed,
            facts.Margin);
    }

    private static ReleasePairResult UnevaluablePair(
        ReleaseRowReadings row,
        PairFacts facts,
        string reason,
        PairDraw draw = default) =>
        Result(
            row.RowId,
            ReleaseState.Unevaluable,
            reason,
            inFamily: true,
            ineligibility: null,
            draw,
            draw.Adjusted,
            facts.ReferenceMedian,
            facts.RowMedian,
            facts.PassesUsed,
            facts.Margin);

    /// <summary>One pair's prepared values, so the dispatch and the drawing share one computation.</summary>
    private sealed record PairFacts(
        IReadOnlyDictionary<string, double> ReferenceValues,
        IReadOnlyDictionary<string, double> RowValues,
        double? ReferenceMedian,
        double? RowMedian,
        int PassesUsed,
        string? Exclusion,
        bool Included,
        double? Margin,
        string Kind,
        int Resamples,
        int Seed)
    {
        internal bool CanDraw => PassesUsed >= 2 && ReferenceValues.Count > 0 && RowValues.Count > 0;
    }




    /// <summary>The state one in-family pair supports, and the evidence in the report's words.</summary>
    private static (string State, string Reason) Decide(
        PrimaryMetric primary,
        ReleaseInterval raw,
        ReleaseInterval adjusted,
        double? equivalenceMargin,
        int familySize)
    {
        var isRatio = string.Equals(primary.Kind, ThresholdKind.Ratio, StringComparison.Ordinal);
        var bandLow = isRatio ? 1.0 - primary.Band : -primary.Band;
        var bandHigh = isRatio ? 1.0 + primary.Band : primary.Band;
        if (adjusted.Low > bandHigh || adjusted.High < bandLow)
        {
            return (
                ReleaseState.Different,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{ReleaseComparison.AdjustmentLabel} (m={familySize}): adjusted CI [{adjusted.Low:0.#####}, {adjusted.High:0.#####}] "
                    + $"excludes the band [{bandLow:0.#####}, {bandHigh:0.#####}]"));
        }

        if (equivalenceMargin is { } margin && Inside(raw, isRatio, margin))
        {
            return (
                ReleaseState.Equivalent,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"pre-registered TOST family, unadjusted: raw CI [{raw.Low:0.#####}, {raw.High:0.#####}] "
                    + $"lies inside the declared ±{margin:0.#####} margin"));
        }

        var bandText = string.Create(
            CultureInfo.InvariantCulture,
            $"adjusted CI [{adjusted.Low:0.#####}, {adjusted.High:0.#####}]");
        var insideBand = adjusted.Low >= bandLow && adjusted.High <= bandHigh;
        if (equivalenceMargin is { } declared)
        {
            return (
                ReleaseState.Inconclusive,
                string.Concat(
                    bandText,
                    insideBand ? " lies inside the band" : " straddles the band",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"; the raw CI is outside the pre-registered ±{declared:0.#####} TOST margin, so equivalence is not claimed")));
        }

        return (
            ReleaseState.Inconclusive,
            string.Concat(
                bandText,
                insideBand
                    ? " lies inside the band; no pre-registered TOST family covers this pair"
                    : " straddles the band"));
    }

    /// <summary>Whether the unadjusted interval lies wholly inside the declared equivalence margin.</summary>
    private static bool Inside(ReleaseInterval interval, bool isRatio, double margin) =>
        isRatio
            ? interval.Low >= 1.0 - margin && interval.High <= 1.0 + margin
            : interval.Low >= -margin && interval.High <= margin;

    /// <summary>Draws the raw and adjusted intervals with the same generator seed.</summary>
    private static PairDraw DrawBoth(
        string kind,
        IReadOnlyDictionary<string, double> valuesA,
        IReadOnlyDictionary<string, double> valuesB,
        int resamples,
        int seed,
        int familySize)
    {
        var raw = ReleaseStatistics.Draw(kind, valuesA, valuesB, resamples, seed, ReleaseComparison.FamilywiseAlpha);
        if (raw.Error is not null)
        {
            return new PairDraw(Median: null, Raw: null, Adjusted: null, Degenerate: false, Error: raw.Error);
        }

        var divisor = familySize > 0 ? familySize : 1;
        var adjusted = ReleaseStatistics.Draw(kind, valuesA, valuesB, resamples, seed, ReleaseComparison.FamilywiseAlpha / divisor);
        return adjusted.Error is not null
            ? new PairDraw(Median: raw.Median, Raw: raw.Interval, Adjusted: null, Degenerate: false, Error: adjusted.Error)
            : new PairDraw(Median: raw.Median, Raw: raw.Interval, Adjusted: adjusted.Interval, Degenerate: raw.Degenerate || adjusted.Degenerate, Error: null);
    }

    /// <summary>Draws the raw interval only, when a report-only pair has enough passes to support one.</summary>
    private static ReleaseStatistics.DrawResult TryDrawRaw(
        string kind,
        IReadOnlyDictionary<string, double> valuesA,
        IReadOnlyDictionary<string, double> valuesB,
        int passesUsed,
        int resamples,
        int seed) =>
        passesUsed >= 2 && valuesA.Count > 0 && valuesB.Count > 0
            ? ReleaseStatistics.Draw(kind, valuesA, valuesB, resamples, seed, ReleaseComparison.FamilywiseAlpha)
            : default;

    private static string AppendDrawReason(string reason, ReleaseStatistics.DrawResult draw) =>
        draw.Error is null ? reason : $"{reason}; {draw.Error}";


    private static bool IsZeroEvent(
        IReadOnlyDictionary<string, double> referenceValues,
        IReadOnlyDictionary<string, double> rowValues) =>
        referenceValues.Count > 0
        && rowValues.Count > 0
        && referenceValues.Values.All(value => value.Equals(0.0))
        && rowValues.Values.All(value => value.Equals(0.0));


    private static ReleasePairResult Result(
        string rowId,
        string state,
        string reason,
        bool inFamily,
        string? ineligibility,
        PairDraw draw,
        ReleaseInterval? adjusted,
        double? referenceMedian,
        double? rowMedian,
        int passesUsed,
        double? margin) =>
        new(
            rowId,
            inFamily,
            ineligibility,
            passesUsed,
            referenceMedian,
            rowMedian,
            draw.Median,
            draw.Raw,
            adjusted,
            margin,
            state,
            reason);

    /// <summary>Adds the gate evidence a cell lost to an otherwise complete pair result.</summary>
    private static ReleasePairResult Tagged(
        ReleasePairResult result,
        string? gateNote,
        IReadOnlyList<string>? invalidatedPasses) =>
        result with
        {
            Reason = gateNote is null ? result.Reason : $"{result.Reason}; {gateNote}",
            InvalidatedPasses = invalidatedPasses ?? [],
        };
    /// <summary>One pair's draws and the reason a draw failed.</summary>
    private readonly record struct PairDraw(
        double? Median,
        ReleaseInterval? Raw,
        ReleaseInterval? Adjusted,
        bool Degenerate = false,
        string? Error = null);
}
