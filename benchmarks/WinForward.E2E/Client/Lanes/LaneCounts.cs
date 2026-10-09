using System.Runtime.InteropServices;

namespace WinForward.E2E.Client.Lanes;

/// <summary>
/// The send-side counters of one lane run, as an immutable snapshot. Window admission and
/// in-flight are deliberately absent: they are the policy's book, and the two name sets must stay
/// disjoint so there is exactly one truth per number.
/// </summary>
/// <param name="Supplied">Slots the schedule offered, counted once per slot however the slot ended.</param>
/// <param name="SentOk">Requests the transport accepted.</param>
/// <param name="SendWouldBlock">Sends whose transport did not complete synchronously.</param>
/// <param name="SendFailures">Requests the transport refused or failed on; each also reached the policy as <c>OnSent(Accepted: false)</c>.</param>
/// <param name="DeferredQueued">
/// Slots that found the window closed, whether or not the queue had room for them. This publishes as the
/// contract's <c>metrics.*.windowOverflow</c> and counts a slot once: the retry of an already
/// queued intent is not a new deferral.
/// </param>
/// <param name="DeferredDropped">
/// The deferred slots the bounded queue could not hold, so the intent yielded no sample. This publishes
/// as the contract's <c>metrics.*.backlogDrops</c> and is a subset of <see cref="DeferredQueued"/>: a
/// dropped slot counts in both, exactly as the two counters it replaces did.
/// </param>
/// <param name="DeferredPending">
/// The intents still waiting in the defer queue when the run returned: requests the schedule offered and
/// the window never let out. This publishes as one half of the contract's
/// <c>metrics.*.outstandingAtTeardown</c> — the other half is the policy's pending replies —
/// and it is the queue's occupancy at return, not a count of deferrals: <see cref="DeferredQueued"/>
/// minus <see cref="DeferredDropped"/> minus the intents that later went out.
/// </param>
/// <param name="ScheduleTruncated">The offer loop stopped before its deadline — cancelled, failed, or never connected.</param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct LaneCounts(
    long Supplied,
    long SentOk,
    long SendWouldBlock,
    long SendFailures,
    long DeferredQueued,
    long DeferredDropped,
    long DeferredPending,
    bool ScheduleTruncated);
