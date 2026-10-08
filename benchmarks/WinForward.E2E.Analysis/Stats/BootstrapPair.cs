using System.Globalization;

namespace WinForward.E2E.Analysis.Stats;

/// <summary>
/// One bootstrap comparison's outcome: the point estimate, its 95 % interval, the two p-values the
/// verdicts are decided from, and the resampling mode that was actually used.
/// </summary>
/// <param name="Estimate">The point estimate: a ratio, or a difference in the metric's own units.</param>
/// <param name="CiLow">The interval's lower edge.</param>
/// <param name="CiHigh">The interval's upper edge.</param>
/// <param name="PValue">The no-difference p-value, which a <c>different</c> claim is judged by.</param>
/// <param name="PEquivalence">The TOST equivalence p-value, which a <c>same</c> claim is judged by.</param>
/// <param name="Mode">How the passes were resampled, printed beside the interval.</param>
/// <param name="Degenerate">Whether every resample produced the same estimate.</param>
internal sealed record Comparison(
    double Estimate,
    double CiLow,
    double CiHigh,
    double PValue,
    double? PEquivalence,
    string Mode,
    bool Degenerate);

/// <summary>
/// The bootstrap the reference draws every interval and p-value from: paired ratio or difference
/// statistics over **passes**, never over samples, resampled <c>--resamples</c> times with a seed
/// derived from the metric and the two rows.
/// </summary>
/// <remarks>
/// <para><b>Passes are the resampling unit.</b> A campaign has as many passes as it was run for, so a
/// comparison is backed by a handful of values; drawing from the samples instead would report a
/// precision the campaign never had.</para>
/// <para><b>Paired when both sides ran the same passes.</b> A ratio is computed in log space so the
/// interval is multiplicative; a pair with a non-positive side has no ratio at all and is reported as
/// an error rather than as a number.</para>
/// </remarks>
internal static class BootstrapPair
{
    /// <summary>Bootstraps one pair of per-pass value maps, or reports why it cannot.</summary>
    internal static (Comparison? Comparison, string? Error) Draw(
        IReadOnlyDictionary<string, double> valuesA,
        IReadOnlyDictionary<string, double> valuesB,
        string kind,
        double? threshold,
        int resamples,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(valuesA);
        ArgumentNullException.ThrowIfNull(valuesB);
        ArgumentNullException.ThrowIfNull(kind);

        if (valuesA.Count == 0 || valuesB.Count == 0)
        {
            return (null, "one side has no per-pass value for this metric");
        }

        var shared = valuesA.Keys.Where(valuesB.ContainsKey).Order(StringComparer.Ordinal).ToList();
        var paired = shared.Count >= 2 && shared.Count == valuesA.Count && shared.Count == valuesB.Count;
        var random = new CpRandom(seed);
        var (estimates, point, mode, error) = string.Equals(kind, "ratio", StringComparison.Ordinal)
            ? RatioEstimates(valuesA, valuesB, shared, paired, random, resamples)
            : DifferenceEstimates(valuesA, valuesB, shared, paired, random, resamples);
        if (error is not null)
        {
            return (null, error);
        }

        estimates.Sort();
        double below;
        double above;
        double lowEdge;
        double highEdge;
        if (string.Equals(kind, "ratio", StringComparison.Ordinal))
        {
            below = estimates.Count(value => value <= 1.0) / (double)estimates.Count;
            above = estimates.Count(value => value >= 1.0) / (double)estimates.Count;
            lowEdge = 1.0 - (threshold ?? 0.0);
            highEdge = 1.0 + (threshold ?? 0.0);
        }
        else
        {
            below = estimates.Count(value => value <= 0.0) / (double)estimates.Count;
            above = estimates.Count(value => value >= 0.0) / (double)estimates.Count;
            lowEdge = -(threshold ?? 0.0);
            highEdge = threshold ?? 0.0;
        }

        double? equivalence = null;
        if (threshold is not null)
        {
            var belowEdge = estimates.Count(value => value <= lowEdge) / (double)estimates.Count;
            var aboveEdge = estimates.Count(value => value >= highEdge) / (double)estimates.Count;
            equivalence = Math.Max(belowEdge, aboveEdge);
        }

        return (new Comparison(
            point,
            DescriptiveStats.Quantile(estimates, 0.025)!.Value,
            DescriptiveStats.Quantile(estimates, 0.975)!.Value,
            Math.Min(1.0, 2.0 * Math.Min(below, above)),
            equivalence,
            mode,
            estimates[0].Equals(estimates[^1])), null);
    }

    /// <summary>The resampled ratios: in log space when the two sides ran the same passes, else separately.</summary>
    private static (List<double> Estimates, double Point, string Mode, string? Error) RatioEstimates(
        IReadOnlyDictionary<string, double> valuesA,
        IReadOnlyDictionary<string, double> valuesB,
        List<string> shared,
        bool paired,
        CpRandom random,
        int resamples)
    {
        var estimates = new List<double>(resamples);
        if (paired)
        {
            var pairs = shared.ConvertAll(key => (A: valuesA[key], B: valuesB[key]));
            if (pairs.Exists(pair => pair.A <= 0.0 || pair.B <= 0.0))
            {
                return (estimates, 0.0, string.Empty, "non-positive values, ratio undefined");
            }

            var logs = pairs.ConvertAll(pair => Math.Log(pair.A) - Math.Log(pair.B));
            for (var draw = 0; draw < resamples; draw++)
            {
                estimates.Add(Math.Exp(DescriptiveStats.Median(Resample(logs, random))!.Value));
            }

            return (estimates, Math.Exp(DescriptiveStats.Median(logs)!.Value), $"paired ({pairs.Count} passes)", null);
        }

        var a = valuesA.Values.ToList();
        var b = valuesB.Values.ToList();
        if (a.Exists(value => value <= 0.0) || b.Exists(value => value <= 0.0))
        {
            return (estimates, 0.0, string.Empty, "non-positive values, ratio undefined");
        }

        for (var draw = 0; draw < resamples; draw++)
        {
            estimates.Add(DescriptiveStats.Median(Resample(a, random))!.Value
                / DescriptiveStats.Median(Resample(b, random))!.Value);
        }

        return (
            estimates,
            DescriptiveStats.Median(a)!.Value / DescriptiveStats.Median(b)!.Value,
            $"unpaired ({a.Count} vs {b.Count} passes)",
            null);
    }

    /// <summary>The resampled differences: paired when the two sides ran the same passes, else separately.</summary>
    private static (List<double> Estimates, double Point, string Mode, string? Error) DifferenceEstimates(
        IReadOnlyDictionary<string, double> valuesA,
        IReadOnlyDictionary<string, double> valuesB,
        List<string> shared,
        bool paired,
        CpRandom random,
        int resamples)
    {
        var estimates = new List<double>(resamples);
        if (paired)
        {
            var pairs = shared.ConvertAll(key => valuesA[key] - valuesB[key]);
            for (var draw = 0; draw < resamples; draw++)
            {
                estimates.Add(DescriptiveStats.Median(Resample(pairs, random))!.Value);
            }

            return (estimates, DescriptiveStats.Median(pairs)!.Value, $"paired ({pairs.Count} passes)", null);
        }

        var a = valuesA.Values.ToList();
        var b = valuesB.Values.ToList();
        for (var draw = 0; draw < resamples; draw++)
        {
            estimates.Add(DescriptiveStats.Median(Resample(a, random))!.Value
                - DescriptiveStats.Median(Resample(b, random))!.Value);
        }

        return (
            estimates,
            DescriptiveStats.Median(a)!.Value - DescriptiveStats.Median(b)!.Value,
            $"unpaired ({a.Count} vs {b.Count} passes)",
            null);
    }

    /// <summary>
    /// The practical-significance verdict for one pairwise comparison: <c>different</c> needs an
    /// interval that excludes both zero and the threshold band, <c>same</c> needs one wholly inside it,
    /// and anything else — including too few passes — is <c>inconclusive</c>.
    /// </summary>
    internal static (string Verdict, string Reason) Decide(
        string kind,
        double? threshold,
        Comparison comparison,
        int passesUsed,
        int minPasses)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(comparison);

        if (passesUsed < minPasses)
        {
            return (
                "inconclusive",
                $"only {passesUsed.ToString(CultureInfo.InvariantCulture)} pass(es) contribute; "
                + $"at least {minPasses.ToString(CultureInfo.InvariantCulture)} are needed");
        }

        if (threshold is null)
        {
            return ("no-threshold-declared", "no pre-declared practical threshold covers this metric");
        }

        var low = comparison.CiLow;
        var high = comparison.CiHigh;
        double bandLow;
        double bandHigh;
        bool excludesZero;
        if (string.Equals(kind, "ratio", StringComparison.Ordinal))
        {
            bandLow = 1.0 - threshold.Value;
            bandHigh = 1.0 + threshold.Value;
            excludesZero = low > 1.0 || high < 1.0;
        }
        else
        {
            bandLow = -threshold.Value;
            bandHigh = threshold.Value;
            excludesZero = low > 0.0 || high < 0.0;
        }

        if (excludesZero && (low > bandHigh || high < bandLow))
        {
            return ("different", string.Format(CultureInfo.InvariantCulture, "CI excludes both zero and the ±{0:0.00} threshold band", threshold.Value));
        }

        if (low >= bandLow && high <= bandHigh)
        {
            return ("same", string.Format(CultureInfo.InvariantCulture, "CI lies inside the ±{0:0.00} threshold band", threshold.Value));
        }

        return ("inconclusive", string.Format(CultureInfo.InvariantCulture, "CI straddles the ±{0:0.00} threshold band", threshold.Value));
    }

    private static List<double> Resample(List<double> values, CpRandom random)
    {
        var draw = new List<double>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            draw.Add(values[(int)random.RandBelow(values.Count)]);
        }

        return draw;
    }
}
