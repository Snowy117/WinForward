namespace WinForward.E2E.Analysis.Release;

/// <summary>The two ways a release band states its width.</summary>
/// <remarks>
/// <b>The kind is the unit, not a formatting choice.</b> A <see cref="Ratio"/> band is a fraction of
/// the reference's value (±0.18 is ±18 %), while a <see cref="PercentagePoints"/> band is an absolute
/// difference in percentage points; judging a value against the wrong kind changes the decision, so the
/// parser refuses a declaration the metric's own unit contradicts.
/// </remarks>
public static class ThresholdKind
{
    /// <summary>A band on a ratio: the compared values are divided and judged against <c>1 ± band</c>.</summary>
    public const string Ratio = "ratio";

    /// <summary>A band on a difference in percentage points.</summary>
    public const string PercentagePoints = "pp";

    /// <summary>Whether a spelling is one of the two declared kinds.</summary>
    internal static bool IsDeclared(string kind) =>
        string.Equals(kind, Ratio, StringComparison.Ordinal) || string.Equals(kind, PercentagePoints, StringComparison.Ordinal);
}

/// <summary>The release caliber a thresholds file is calibrated for: the plan, its hash and its declared arm durations.</summary>
/// <param name="Path">The plan file, relative to the working directory the release run resolves it from.</param>
/// <param name="Sha256">The plan's SHA-256 as the thresholds file declares it, 64 hexadecimal characters.</param>
/// <param name="Seconds">Each full-plan arm's declared duration in seconds.</param>
/// <remarks>
/// <b>The declaration is repeated on purpose.</b> The plan has to be read at run time to compute its
/// hash, but the release report prints the durations without parsing a plan, and a plan whose durations
/// moved must not be described by a thresholds file written for the previous ones: the hash is what
/// catches that, and the durations are what the report quotes when it does not.
/// </remarks>
public sealed record CaliberDeclaration(string Path, string Sha256, IReadOnlyDictionary<string, int> Seconds);

/// <summary>One inferential metric's band, with the report metadata declared beside it.</summary>
/// <param name="Metric">The metric's identifier, the same key the analysis publishes.</param>
/// <param name="Kind">One of <see cref="ThresholdKind"/>.</param>
/// <param name="Band">The decision band, in the unit <paramref name="Kind"/> names.</param>
/// <param name="Business">The smallest effect worth acting on, when the calibration declared one.</param>
/// <param name="Mdd">The minimum detectable difference the calibration measured, when it declared one.</param>
public sealed record PrimaryMetric(string Metric, string Kind, double Band, double? Business, double? Mdd);

/// <summary>One capability gate: a metric bounded rather than compared.</summary>
/// <param name="Metric">The metric's identifier.</param>
/// <param name="Kind">One of <see cref="ThresholdKind"/>.</param>
/// <param name="UpperBound">The upper bound the measured rate is judged against, in the kind's unit.</param>
/// <param name="NFrom">The published key the sample size is read from, when one was declared.</param>
public sealed record CapabilityRule(string Metric, string Kind, double UpperBound, string? NFrom);

/// <summary>One pre-registered TOST family: a primary metric and the equivalence margin declared for it.</summary>
/// <param name="Metric">A metric the thresholds file also declares in <c>primary</c>.</param>
/// <param name="Margin">The declared equivalence margin, in the metric's own unit.</param>
public sealed record EquivalenceFamily(string Metric, double Margin);

/// <summary>What the clock correction is asked to prove for this campaign.</summary>
/// <param name="Require">Whether a missing <c>clock.jsonl</c> refuses the run instead of being disclosed.</param>
/// <param name="MaxDriftMs">The largest pre-to-post offset move the campaign accepts before the clock is reported as drifted.</param>
public sealed record ClockRule(bool Require, double MaxDriftMs)
{
    /// <summary>The drift the release design accepts when the thresholds file does not declare one.</summary>
    public const double DefaultMaxDriftMs = 100;
}
