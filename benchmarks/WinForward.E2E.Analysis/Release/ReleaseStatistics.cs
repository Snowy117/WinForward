using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Release;

/// <summary>Draws the raw and Bonferroni-adjusted percentile intervals the release decision reads.</summary>
internal static class ReleaseStatistics
{
    /// <summary>One interval draw: the interval, whether every resample agreed, or why it could not be drawn.</summary>
    internal readonly record struct DrawResult(double? Median, ReleaseInterval? Interval, bool Degenerate, string? Error);

    /// <summary>
    /// Draws the percentile interval at <paramref name="alpha"/> from the same paired estimate
    /// <see cref="BootstrapPair.DrawInterval"/> computes, and keeps the level on the result.
    /// </summary>
    internal static DrawResult Draw(
        string kind,
        IReadOnlyDictionary<string, double> valuesA,
        IReadOnlyDictionary<string, double> valuesB,
        int resamples,
        int seed,
        double alpha)
    {
        var measured = BootstrapPair.DrawInterval(valuesA, valuesB, kind, resamples, seed, alpha);
        return measured.Value is { } interval
            ? new(Median: interval.Estimate, Interval: new ReleaseInterval(1.0 - alpha, interval.CiLow, interval.CiHigh), Degenerate: interval.Degenerate, Error: null)
            : new(Median: null, Interval: null, Degenerate: false, Error: measured.Reason);
    }
}
