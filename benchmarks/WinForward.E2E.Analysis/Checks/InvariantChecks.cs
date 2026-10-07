namespace WinForward.E2E.Analysis.Checks;

/// <summary>
/// The invariants the reference asserts over a campaign before it believes any number: every UDP
/// arm's arrivals plus late plus never plus abandoned plus corrupt datagrams equal what it sent, the
/// reliability arm's <c>connectAttempts</c> equals its <c>scheduledAttempts</c>, each multi-lane arm
/// has a non-zero witness per lane, no datagram crossed between flows, the two control blocks agree,
/// and the direct lane leaked nothing.
/// </summary>
/// <remarks>
/// <b>Completed in batch 2.</b> A violation is rendered as a finding in §0 and as a gate row in §3 —
/// the two places a reader learns that a number exists but should not be compared — so this file's
/// output is compared text twice. The checks themselves are also the analyzer's own answer to "is this
/// campaign usable", which is why they are a batch of their own rather than a helper of the tables.
/// </remarks>
internal static class InvariantChecks
{
    /// <summary>What this file still owes, for the run summary a caller reads.</summary>
    internal const string Pending = "2 invariant checks and the gate table";
}
