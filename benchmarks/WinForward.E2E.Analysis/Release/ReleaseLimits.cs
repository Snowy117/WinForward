namespace WinForward.E2E.Analysis.Release;

/// <summary>The claim boundary the release verdict and its report publish.</summary>
/// <remarks>
/// These are sentences, not keys: a reader of either document has to be able to see what the campaign
/// cannot support without opening a task record. The list is deliberately short and repeated in both
/// files, because a limits section buried in one artifact is not a claim boundary.
/// </remarks>
internal static class ReleaseLimits
{
    /// <summary>Every limit line, in the order both documents print them.</summary>
    internal static IReadOnlyList<string> All { get; } =
    [
        "This is a solution comparison under one declared workload, not a product ranking; the seven rows are seven factor bundles.",
        "No absolute latency is claimed: there is no independent external reference, and the loopback path plus one vSwitch hop is not a WAN.",
        "The interval describes short-term session noise over the measured passes, not machine-to-machine reproducibility.",
        "The release rules and this verdict are C#-only; the Python oracle covers the unchanged legacy tables.md and verdict.json, not these files.",
        "The clock correction uses one offset per pass (the pre/post line at the pass midpoint); a campaign whose drift exceeds the declared limit is disclosed, not rescaled.",
        "Latency and CPU conclusions are phrased as \"not worse than X\" or \"difference at least Y\", never as a speed-up percentage.",
        "Internal WinForward row pairs and every metric outside the pre-registered primary family are exploratory: values and raw intervals only, no difference or equivalence claim.",
        "QUIC/HTTP3, IPv6, kernel DPC/ISR, true blinding and connection-id-level ledger attribution are outside this campaign.",
    ];
}
