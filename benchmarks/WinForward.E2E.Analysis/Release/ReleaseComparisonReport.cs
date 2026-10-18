namespace WinForward.E2E.Analysis.Release;

/// <summary>The states a release comparison can be in.</summary>
/// <remarks>
/// <b>There is no <c>same</c>.</b> An interval that fails to exclude the decision band makes no
/// difference claim, but it does not prove equality either; only a pre-registered equivalence family
/// can say <see cref="Equivalent"/>, and a degenerate interval is <see cref="Unevaluable"/> rather than
/// a zero-width <c>same</c>.
/// </remarks>
public static class ReleaseState
{
    /// <summary>The adjusted interval excludes the decision band.</summary>
    public const string Different = "different";

    /// <summary>The adjusted interval overlaps the band, or too few measured passes contribute.</summary>
    public const string Inconclusive = "inconclusive";

    /// <summary>The interval cannot be drawn, is degenerate, or the metric has no denominator.</summary>
    public const string Unevaluable = "unevaluable";

    /// <summary>The raw interval lies inside a pre-registered TOST margin.</summary>
    public const string Equivalent = "equivalent";

    /// <summary>The metric or pair makes no decision; values and the raw interval are reported only.</summary>
    public const string ReportOnly = "report-only";
}

/// <summary>The two roles a metric can play in the release report.</summary>
public static class ReleaseMetricRole
{
    /// <summary>The pre-registered inferential family.</summary>
    public const string Primary = "primary";

    /// <summary>Every published metric the family does not claim; values and raw intervals only.</summary>
    public const string Exploratory = "exploratory";
}

/// <summary>The three outcomes of the capability gate.</summary>
public static class ReleaseCapabilityStatus
{
    /// <summary>The upper bound is at or below the declared threshold.</summary>
    public const string Pass = "pass";

    /// <summary>The upper bound exceeds the threshold, or the sample contains an event.</summary>
    public const string Fail = "fail";

    /// <summary>The denominator is missing or zero, or the arm was not measured.</summary>
    public const string NotMeasured = "not-measured";
}

/// <summary>One opponent's comparison with the reference row for one metric.</summary>
/// <param name="RowId">The row compared with the reference.</param>
/// <param name="InFamily">Whether this pair is in the pre-registered reference-vs-opponent family.</param>
/// <param name="Ineligibility">Why the pair is outside the family, or null when it is inside.</param>
/// <param name="PassesUsed">How many measured passes contribute a value to both sides.</param>
/// <param name="ReferenceMedian">The reference row's median across the per-pass values.</param>
/// <param name="RowMedian">The compared row's median across the per-pass values.</param>
/// <param name="Median">The pair estimate: a ratio, or a difference in percentage points.</param>
/// <param name="RawInterval">The unadjusted 95 % percentile interval, or null when it cannot be drawn.</param>
/// <param name="AdjustedInterval">
/// The Bonferroni-adjusted interval at <c>1 - 0.05 / m</c>, or null outside the family or when it cannot
/// be drawn.
/// </param>
/// <param name="EquivalenceMargin">The pre-registered TOST margin for the metric, or null.</param>
/// <param name="State">One of the <see cref="ReleaseState"/> names.</param>
/// <param name="Reason">The evidence behind <paramref name="State"/>, in the report's words.</param>
/// <remarks>
/// <b>Raw and adjusted intervals are both published.</b> The raw interval describes the pair; the
/// adjusted interval is what the family-wise decision reads. A pair outside the family still carries its
/// raw interval when both sides have values, which is what makes an exploratory pair reportable without
/// turning it into a claim.
/// </remarks>
public sealed record ReleasePairResult(
    string RowId,
    bool InFamily,
    string? Ineligibility,
    int PassesUsed,
    double? ReferenceMedian,
    double? RowMedian,
    double? Median,
    ReleaseInterval? RawInterval,
    ReleaseInterval? AdjustedInterval,
    double? EquivalenceMargin,
    string State,
    string Reason);

/// <summary>One metric's release result: its family, its reference readings and every row's pair.</summary>
/// <param name="Metric">The metric's catalogue identifier.</param>
/// <param name="Role">One of the <see cref="ReleaseMetricRole"/> names.</param>
/// <param name="Kind">The declared band unit for a primary metric, or null for an exploratory one.</param>
/// <param name="Band">The declared decision band, or null for an exploratory metric.</param>
/// <param name="Business">The declared smallest effect worth acting on, when one was declared.</param>
/// <param name="Mdd">The declared minimum detectable difference, when one was declared.</param>
/// <param name="ReferenceRow">The reference row the family is drawn against.</param>
/// <param name="ReferenceMedian">The reference row's median across the measured passes.</param>
/// <param name="ReferenceValues">The reference row's per-pass values, in measured-pass order.</param>
/// <param name="FamilySize">How many pairs are in the pre-registered family, which is the <c>m</c> of the adjustment.</param>
/// <param name="EquivalenceMargin">The pre-registered TOST margin, or null when the metric has none.</param>
/// <param name="ZeroEventN">The exposure behind a zero-event rate, when a rule-of-three bound was available.</param>
/// <param name="ZeroEventUpperBound">The 3/n upper bound in percentage points, when a zero-event rate was available.</param>
/// <param name="Pairs">Every row's pair result, opponents first and then the other measured rows.</param>
/// <param name="State">One of the <see cref="ReleaseState"/> names, aggregated over the in-family pairs.</param>
/// <param name="Reason">The evidence behind <paramref name="State"/>, in the report's words.</param>
public sealed record ReleaseMetricResult(
    string Metric,
    string Role,
    string? Kind,
    double? Band,
    double? Business,
    double? Mdd,
    string ReferenceRow,
    double? ReferenceMedian,
    IReadOnlyDictionary<string, double> ReferenceValues,
    int FamilySize,
    double? EquivalenceMargin,
    long? ZeroEventN,
    double? ZeroEventUpperBound,
    IReadOnlyList<ReleasePairResult> Pairs,
    string State,
    string Reason);

/// <summary>One measured pass's counts for the capability gate.</summary>
/// <param name="PassId">The pass the counts belong to.</param>
/// <param name="Events">The unexpected-end-of-file count the pass published.</param>
/// <param name="Attempts">The connect-attempt exposure the pass published.</param>
public sealed record ReleaseCapabilityPass(string PassId, long Events, long Attempts);

/// <summary>One capability gate's result for one row: a bound, never a difference claim.</summary>
/// <param name="Metric">The capability metric's catalogue identifier.</param>
/// <param name="RowId">The row the bound describes.</param>
/// <param name="NFrom">The declared denominator key the exposure is read from, when one was declared.</param>
/// <param name="Rate">The pooled observed rate in percentage points, or null when not measured.</param>
/// <param name="Events">The summed event count, or null when not measured.</param>
/// <param name="N">The summed exposure, or null when not measured.</param>
/// <param name="UpperBound">The zero-event 3/n upper bound in percentage points, or null when not measured or when events &gt; 0.</param>
/// <param name="Threshold">The declared upper bound the gate compares with, in percentage points.</param>
/// <param name="Status">One of the <see cref="ReleaseCapabilityStatus"/> names.</param>
/// <param name="Reason">The evidence behind <paramref name="Status"/>, in the report's words.</param>
/// <param name="Passes">The per-pass counts the pooled rate was read from.</param>
/// <remarks>
/// <b>A capability gate is not a comparison.</b> It answers whether the observed exposure supports a
/// declared upper bound, not whether one solution differs from another. A zero-event sample is the only
/// sample the rule of three can bound, so a nonzero event count fails the gate and reports no upper
/// bound; a zero denominator is <c>not-measured</c> rather than a pass.
/// </remarks>
public sealed record ReleaseCapabilityResult(
    string Metric,
    string RowId,
    string? NFrom,
    double? Rate,
    long? Events,
    long? N,
    double? UpperBound,
    double Threshold,
    string Status,
    string Reason,
    IReadOnlyList<ReleaseCapabilityPass> Passes);

/// <summary>The whole release decision document, before any file is written.</summary>
/// <param name="ReferenceRow">The row every primary comparison is drawn against.</param>
/// <param name="Opponents">The declared opponents, in the thresholds file's order.</param>
/// <param name="MeasuredPasses">The pass ids that contributed, in natural-key order.</param>
/// <param name="N">How many measured passes the release inputs selected.</param>
/// <param name="Required">How many measured passes the release rule demands.</param>
/// <param name="Insufficient">Whether <paramref name="N"/> is below <paramref name="Required"/>.</param>
/// <param name="FamilywiseAlpha">The family-wise error rate the adjusted intervals control, always <c>0.05</c>.</param>
/// <param name="Metrics">The primary and exploratory metric results, in the thresholds file's order.</param>
/// <param name="Capabilities">Every capability gate's result, one per row and declared metric.</param>
public sealed record ReleaseComparisonReport(
    string ReferenceRow,
    IReadOnlyList<string> Opponents,
    IReadOnlyList<string> MeasuredPasses,
    int N,
    int Required,
    bool Insufficient,
    double FamilywiseAlpha,
    IReadOnlyList<ReleaseMetricResult> Metrics,
    IReadOnlyList<ReleaseCapabilityResult> Capabilities);
