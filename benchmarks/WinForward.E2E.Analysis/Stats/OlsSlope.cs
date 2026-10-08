namespace WinForward.E2E.Analysis.Stats;

/// <summary>
/// The least-squares fit §7's leak verdict is read from: private bytes regressed on elapsed time, with
/// a 95 % confidence interval and the one sentence the verdict is.
/// </summary>
/// <param name="Slope">The fitted slope, or null when there is too little data to fit one.</param>
/// <param name="Low">The interval's lower end, or null with the slope.</param>
/// <param name="High">The interval's upper end, or null with the slope.</param>
/// <param name="Count">How many samples were fitted.</param>
/// <param name="Verdict">Why the slope is what it is, in the reference's own words.</param>
/// <remarks>
/// <para><b>The interval is Student's t, not a normal quantile.</b> The residual standard error is
/// divided by <c>sqrt(n - 2)</c>, so the fit really is a two-parameter regression and the interval
/// widens on a short series; the critical value is tabulated for degrees of freedom up to 30 and 1.96
/// beyond, which is the point where the two agree to the printed precision.</para>
/// <para><b>Leaking is a one-sided reading.</b> A CI that excludes zero from above says the process
/// grew; one that excludes zero from below says it shrank, which is not a leak; one that contains zero
/// says the pass cannot tell, and the interval is still printed so the reader can see how close it
/// came.</para>
/// </remarks>
internal sealed record SlopeFit(double? Slope, double? Low, double? High, int Count, string Verdict)
{
    /// <summary>The verdict of a fit the data could not support.</summary>
    internal static string TooFewSamples => "n/a (fewer than three samples)";

    /// <summary>The verdict of a series whose every sample shares one timestamp.</summary>
    internal static string OneTimestamp => "n/a (all samples share one timestamp)";
}

/// <summary>The ordinary least-squares slope of one pass's sample series.</summary>
internal static class OlsSlope
{
    /// <summary>The two-sided 95 % critical values of Student's t for degrees of freedom 1–30.</summary>
    private static readonly double[] s_t95 =
    [
        0.0, 12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228, 2.201, 2.179, 2.160, 2.145,
        2.131, 2.120, 2.110, 2.101, 2.093, 2.086, 2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045,
        2.042,
    ];

    /// <summary>Beyond the table the t and the normal quantile agree to the printed precision.</summary>
    private const double LargeSampleT95 = 1.96;

    /// <summary>Fits one series: the slope, its interval, and the verdict the interval decides.</summary>
    /// <param name="xs">The elapsed times, in seconds.</param>
    /// <param name="ys">The regressed quantity, in MiB.</param>
    /// <returns>The fit, whose slope is null when the data cannot support one.</returns>
    internal static SlopeFit Fit(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        ArgumentNullException.ThrowIfNull(xs);
        ArgumentNullException.ThrowIfNull(ys);

        var count = xs.Count;
        if (count < 3)
        {
            return new SlopeFit(Slope: null, Low: null, High: null, Count: count, Verdict: SlopeFit.TooFewSamples);
        }

        var meanX = DescriptiveStats.Sum(xs) / count;
        var meanY = DescriptiveStats.Sum(ys) / count;

        var squaredX = new double[count];
        var cross = new double[count];
        for (var index = 0; index < count; index++)
        {
            squaredX[index] = Square(xs[index] - meanX);
            cross[index] = (xs[index] - meanX) * (ys[index] - meanY);
        }

        var sxx = DescriptiveStats.Sum(squaredX);
        var sxy = DescriptiveStats.Sum(cross);
#pragma warning disable S1244 // A sum of squares is exactly zero when every sample shares one timestamp.
        if (sxx == 0.0)
#pragma warning restore S1244
        {
            return new SlopeFit(Slope: null, Low: null, High: null, Count: count, Verdict: SlopeFit.OneTimestamp);
        }

        var slope = sxy / sxx;
        var intercept = meanY - (slope * meanX);
        var residuals = new double[count];
        for (var index = 0; index < count; index++)
        {
            residuals[index] = Square(ys[index] - (intercept + (slope * xs[index])));
        }

        var sse = DescriptiveStats.Sum(residuals);
        var error = sse > 0.0 ? Math.Sqrt(sse / (count - 2) / sxx) : 0.0;
        var critical = Critical95(count - 2);
        var low = slope - (critical * error);
        var high = slope + (critical * error);
        return new SlopeFit(slope, Low: low, High: high, Count: count, Verdict: Verdict(low, high));
    }

    /// <summary>The two-sided 95 % critical value for a number of degrees of freedom.</summary>
    private static double Critical95(int degreesOfFreedom)
    {
        if (degreesOfFreedom <= 0)
        {
            return 0.0;
        }

        return degreesOfFreedom < s_t95.Length ? s_t95[degreesOfFreedom] : LargeSampleT95;
    }

    /// <summary>Which side of zero the interval falls on, which is what the verdict says.</summary>
    private static string Verdict(double low, double high)
    {
        if (low > 0.0)
        {
            return "leaks (CI above zero)";
        }

        return high < 0.0 ? "does not leak (CI below zero)" : "does not leak (CI includes zero)";
    }

    private static double Square(double value) => value * value;
}
