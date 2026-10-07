namespace WinForward.E2E.Analysis.Findings;

/// <summary>
/// The findings the reference collects — a correctness failure, a path-interference, a harness error,
/// a measurement caveat or an informational note, each with its scope and its detail — and their
/// grading into the five severities §0 counts and <c>verdict.json</c> publishes.
/// </summary>
/// <remarks>
/// <b>Completed in batch 2.</b> A finding is not a decoration: it is how a reader learns that a cell
/// exists but should not be compared, and the deduplication (severity, kind, scope, detail) is part of
/// the rendered counts. The findings that come from the invariants live in <c>Checks/</c>; this file
/// grades and orders them, together with the ones only the tables can see (a lane that witnessed
/// nothing, a truncated ledger record, a control block that drifted).
/// </remarks>
internal static class FindingsCollector
{
    /// <summary>What this file still owes, for the run summary a caller reads.</summary>
    internal const string Pending = "2 findings grading and §0 severity counts";
}
