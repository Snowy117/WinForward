namespace WinForward.E2E.Analysis.Stats;

/// <summary>
/// The bootstrap the reference draws every interval and p-value from: paired ratio or difference
/// statistics over **passes** (never over samples), resampled <c>--resamples</c> times with a seed
/// derived from the metric and the two rows, then Holm-adjusted across the family of comparable pairs.
/// </summary>
/// <remarks>
/// <para><b>The sampler lands with the intervals it computes.</b> What it draws with is already in
/// place: <see cref="CpRandom"/> reproduces the generator, <see cref="CpRandom.DeriveSeed"/> the
/// per-comparison seed, and <c>verdict.json</c> publishes both under <c>bootstrap</c> — but the
/// sampler's own output only exists inside the comparisons of the batches that render them (the
/// latency and accuracy tables of batch 3, the headline and detail tables of batch 4), because every
/// interval it produces is compared as text.</para>
/// <para><b><c>--resamples 0</c> has no reference behaviour to copy.</b> With no resamples the
/// reference divides by an empty estimate list and dies with <c>ZeroDivisionError</c> before writing
/// either file, so the sampler's contract at zero is a decision for the batch that first calls it
/// rather than something to be reproduced (D20.1: a behaviour the reference cannot produce is
/// asserted against this side alone).</para>
/// </remarks>
internal static class BootstrapResampler
{
    /// <summary>What this file still owes, for the run summary a caller reads.</summary>
    internal const string Pending = "3/4 bootstrap intervals, Holm adjustment and verdict wording";
}
