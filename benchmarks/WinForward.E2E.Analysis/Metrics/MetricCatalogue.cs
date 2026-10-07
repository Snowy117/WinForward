namespace WinForward.E2E.Analysis.Metrics;

/// <summary>
/// The twenty-one metrics the headline matrix and <c>verdict.json</c>'s <c>metrics</c> object are
/// built from: each one's identifier, label, unit, rounding, practical-significance family and the
/// per-pass extraction that reads it out of an arm's records.
/// </summary>
/// <remarks>
/// <para><b>Completed in batches 3 and 4, and the split is the oracle's.</b> The metric extractors are
/// the one verdict slice that belongs to two batches — the latency, UDP, DNS, MIX-loss and goodput
/// metrics land with §5/§8/§9 (batch 3) and the CPU, memory, PERSIST and reliability metrics with
/// §4/§6/§7/§10/§11 (batch 4) — which is why <c>oracle-diff.py</c> slices <c>metrics</c> by member
/// name instead of by key.</para>
/// <para><b>§4 prints every metric column.</b> The headline matrix is one table over all twenty-one
/// metrics, so it is a batch-4 section: by the time it is compared, the batch that renders the other
/// fifteen columns has already passed, and no section has to wait on a later batch.</para>
/// </remarks>
internal static class MetricCatalogue
{
    /// <summary>What this file still owes, for the run summary a caller reads.</summary>
    internal const string Pending = "3/4 the twenty-one metric extractors behind the headline matrix";
}
