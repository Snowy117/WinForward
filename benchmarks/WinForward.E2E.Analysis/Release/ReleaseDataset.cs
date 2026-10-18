namespace WinForward.E2E.Analysis.Release;

/// <summary>One percentile interval: the level it covers, its two edges and nothing else.</summary>
/// <param name="Level">The fraction of the bootstrap distribution the interval covers, for example <c>0.95</c>.</param>
/// <param name="Low">The lower edge, in the metric's own unit.</param>
/// <param name="High">The upper edge, in the metric's own unit.</param>
/// <remarks>
/// <b>The level is data, not a name.</b> The raw interval covers 95 %, while the release decision uses
/// <c>1 - 0.05 / m</c> for the <c>m</c> in-family pairs of one metric. Carrying the level on the
/// interval keeps a reader from reading the adjusted edge as if it were the raw 95 % one.
/// </remarks>
public sealed record ReleaseInterval(double Level, double Low, double High);

/// <summary>One pass's numerator and denominator for a metric read from counts.</summary>
/// <param name="Numerator">The event count the pass published.</param>
/// <param name="Denominator">The exposure the pass published.</param>
/// <remarks>
/// The capability gate is the release path's only count-based reading: a rate is not estimated from
/// per-pass rates alone when its upper bound is the claim, because the bound needs the exposure behind
/// the events. A zero denominator is kept as <c>0</c> so the gate can report <c>not-measured</c> rather
/// than silently dropping the pass.
/// </remarks>
public sealed record ReleaseCount(long Numerator, long Denominator);

/// <summary>The names a metric reading carries before any interval is drawn, matching the legacy cell statuses.</summary>
/// <remarks>
/// The values are the legacy names, repeated here because a public dataset has to spell the status a
/// caller sets. Keeping the spellings identical means the release path and <c>verdict.json</c> answer
/// "did this row run the arm" the same way instead of maintaining two vocabularies.
/// </remarks>
public static class ReleaseReadingStatus
{
    /// <summary>Nothing is wrong with this metric on this row.</summary>
    public const string Ok = "ok";

    /// <summary>The row's plan never runs the arm.</summary>
    public const string NotInPlan = "not-in-plan";

    /// <summary>The row's plan runs the arm but no arm file or result was found.</summary>
    public const string DeclaredAbsent = "declared-absent";

    /// <summary>The product cannot carry UDP, so every UDP-derived number for this row measures nothing.</summary>
    public const string NotCarried = "not-carried";

    /// <summary>The port-53 arm is a direct-path measurement on this row.</summary>
    public const string DnsCarriage = "dns-carriage";
}

/// <summary>One row's per-pass readings for one metric: the values, the reasons and the counts behind them.</summary>
/// <remarks>
/// <b>A pass appears in <see cref="Values"/> or <see cref="Reasons"/>, never in both.</b> The split is
/// the legacy cell's own contract: a value is a measurement, a reason is why the pass has none, and a
/// JSON <see langword="null"/> denominator is a reason rather than a zero. <see cref="Counts"/> is populated only
/// where the metric is read from a numerator and a denominator, which is the capability gate.
/// </remarks>
public sealed record ReleaseMetricReadings
{
    /// <summary>Per measured pass, the value that pass contributed.</summary>
    public required IReadOnlyDictionary<string, double> Values { get; init; }

    /// <summary>Per measured pass without a value, why it has none.</summary>
    public IReadOnlyDictionary<string, string> Reasons { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Per measured pass, the counts behind the value when the metric has a denominator.</summary>
    public IReadOnlyDictionary<string, ReleaseCount> Counts { get; init; } =
        new Dictionary<string, ReleaseCount>(StringComparer.Ordinal);

    /// <summary>One of the <see cref="ReleaseReadingStatus"/> names.</summary>
    public string Status { get; init; } = ReleaseReadingStatus.Ok;

    /// <summary>The status's own reason, or null when the status is <see cref="ReleaseReadingStatus.Ok"/>.</summary>
    public string? StatusReason { get; init; }

    /// <summary>How many measured passes have a reason rather than a value.</summary>
    public int MissingPasses { get; init; }

    /// <summary>How many measured passes a release gate removed from <see cref="Values"/>.</summary>
    /// <remarks>
    /// A gate failure is not the same state as a published null denominator: the pass was measured and
    /// then disqualified, so the comparison keeps the reason beside the cell and excludes the value from
    /// every interval. Counting the passes here is what lets the comparison tell a gate-empty row from a
    /// row that merely had no denominator.
    /// </remarks>
    public int InvalidatedPasses { get; init; }

    /// <summary>The measured pass ids whose value a release gate removed, in natural-key order.</summary>
    public IReadOnlyList<string> InvalidatedPassIDs { get; init; } = [];
}

/// <summary>One campaign row's readings, keyed by metric.</summary>
/// <param name="RowId">The row id the campaign directory and the thresholds file use.</param>
/// <param name="Metrics">Every metric the row was read for, keyed by the catalogue's metric identifier.</param>
public sealed record ReleaseRowReadings(string RowId, IReadOnlyDictionary<string, ReleaseMetricReadings> Metrics);

/// <summary>
/// The per-pass values the release comparison decides on, read from a campaign tree once and reused by
/// every metric and pair.
/// </summary>
/// <remarks>
/// <para><b>This is a snapshot of the legacy measurement, not a second measurement.</b>
/// <see cref="TryRead"/> loads the tree with the same loader the legacy documents use and reads every
/// metric with <c>MetricCatalogue.PerPassValues</c>, then keeps only the measured passes. A release run
/// therefore compares exactly the numbers <c>tables.md</c> and <c>verdict.json</c> describe, with the
/// discarded warm-up pass removed before any value is selected.</para>
/// <para><b>Reading writes nothing.</b> The campaign loader also locates the ledgers and the environment
/// record, but this type does not render or write any artifact; the release writer owns its outputs.</para>
/// </remarks>
public sealed record ReleaseDataset
{
    /// <summary>The pass ids that contributed, in natural-key order; the discarded pass is absent.</summary>
    public required IReadOnlyList<string> MeasuredPasses { get; init; }

    /// <summary>Every row the campaign loaded, in the analysis's own row order.</summary>
    public required IReadOnlyList<ReleaseRowReadings> Rows { get; init; }

    /// <summary>How many resamples each interval is drawn with.</summary>
    public required int Resamples { get; init; }

    /// <summary>The base seed each pair's generator is derived from.</summary>
    public required int Seed { get; init; }

    /// <summary>
    /// Reads the measured passes of a campaign tree into per-pass metric readings, using the legacy
    /// loader and metric catalogue.
    /// </summary>
    /// <param name="campaignRoot">The campaign root that holds the <c>passN</c> directories.</param>
    /// <param name="inputs">The release inputs whose measured pass list selects the values.</param>
    /// <param name="resamples">How many bootstrap resamples the comparison will draw.</param>
    /// <param name="seed">The base seed each pair's generator is derived from.</param>
    /// <param name="dataset">The readings, when the tree and the pass list agree.</param>
    /// <param name="error">Why they do not, when they do not.</param>
    /// <returns>True when every measured pass was read.</returns>
    /// <remarks>
    /// A release run reads a campaign, not a flat tree: the inputs already rejected a pass list that is
    /// not numbered, and the loader is asked for the pass directories the inputs named. The read is
    /// fail-closed on a measured pass the tree does not hold, because a silently shorter campaign would
    /// change the interval without changing the record.
    /// </remarks>
    public static bool TryRead(
        string campaignRoot,
        ReleaseInputs inputs,
        int resamples,
        int seed,
        out ReleaseDataset? dataset,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(campaignRoot);
        ArgumentNullException.ThrowIfNull(inputs);
        if (resamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(resamples), resamples, "at least one resample is needed.");
        }

        dataset = null;
        if (!double.IsFinite(inputs.WarmupSeconds))
        {
            error = "release: the warm-up window is not a finite number";
            return false;
        }

        if (inputs.Passes.Measured.Count == 0)
        {
            error = "release: the release inputs name no measured pass";
            return false;
        }

        return ReleaseDatasetReader.TryRead(campaignRoot, inputs, resamples, seed, out dataset, out error);
    }
}
