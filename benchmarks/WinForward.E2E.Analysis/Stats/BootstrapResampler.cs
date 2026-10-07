namespace WinForward.E2E.Analysis.Stats;

/// <summary>
/// The bootstrap the reference draws every interval and p-value from: paired ratio or difference
/// statistics over **passes** (never over samples), resampled <c>--resamples</c> times with a
/// <see cref="int"/> seed derived from the metric and the two rows, then Holm-adjusted across the
/// family of comparable pairs.
/// </summary>
/// <remarks>
/// <b>Completed in batch 1b.</b> The numbers it draws with are <c>--resamples 10000</c> and
/// <c>--seed 20261006</c>, both of which are already parsed; what is missing is the sampler itself,
/// and it cannot be approximated: every interval in <c>verdict.json</c> is compared as text, so the
/// draw order, the median and the percentile interpolation all have to be the reference's (D20.5).
/// </remarks>
internal static class BootstrapResampler
{
    /// <summary>What this file still owes, for the run summary a caller reads.</summary>
    internal const string Pending = "1b bootstrap intervals and Holm adjustment";
}
