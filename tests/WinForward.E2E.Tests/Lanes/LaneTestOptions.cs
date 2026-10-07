using WinForward.E2E.Client;
using WinForward.E2E.Client.Lanes;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// The options the engine facts drive: no pace at all (every intended instant is the start), a deadline
/// far enough away that the fakes decide when a run ends, and a short grace bound so the drain's own
/// bound is never what a fact accidentally measures.
/// </summary>
internal static class LaneTestOptions
{
    internal static LaneEngineOptions Loop(double ratePerSecond = 0, int backlogLimit = 4, double drainSeconds = 0.05) => new()
    {
        RatePerSecond = ratePerSecond,
        StartTicks = Clock.Now,
        DeadlineTicks = Clock.Now + Clock.FromSeconds(30),
        BacklogLimit = backlogLimit,
        SendBufferBytes = 16,
        ReceiveBufferBytes = 32,
        DrainLimitTicks = Clock.FromSeconds(drainSeconds),
    };

    /// <summary>
    /// The grace facts' options: a paced offer loop that ends on its own deadline, so the token stays
    /// alive and the drain really has ticks to run, plus the injected drain bound under test.
    /// </summary>
    internal static LaneEngineOptions Deadline(double ratePerSecond, double offerSeconds, double drainSeconds) => new()
    {
        RatePerSecond = ratePerSecond,
        StartTicks = Clock.Now,
        DeadlineTicks = Clock.Now + Clock.FromSeconds(offerSeconds),
        BacklogLimit = 4,
        SendBufferBytes = 16,
        ReceiveBufferBytes = 32,
        DrainLimitTicks = Clock.FromSeconds(drainSeconds),
    };

    /// <summary>
    /// Runs one throwaway lane so the one-time JIT cost of the send path is already paid. The grace
    /// facts drive the offer loop against a 30 ms window, and on a loaded host that window is shorter
    /// than the first slot's compilation — which would leave the measured run with no slots at all.
    /// </summary>
    internal static async Task WarmAsync()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();

        // The reply path is part of what needs warming: a fact that measures inside slot 1 would
        // otherwise be measuring the first call of OnReceive and of the settlement queue.
        var policy = new LanePolicyFake(log);
        var transport = new LaneTransportFake(log) { CancelAfter = cancellation, CancelAfterSends = 3, ReplyTo = policy, ReplyAfterSends = [1] };
        await RunAsync(transport, policy, Loop(), cancellation.Token).ConfigureAwait(false);
    }

    /// <summary>Runs one lane and answers both the counts and the connect outcome.</summary>
    internal static async Task<(LaneCounts Counts, LaneOpenResult Open)> RunAsync(
        LaneTransportFake transport,
        LanePolicyFake policy,
        LaneEngineOptions options,
        CancellationToken cancellationToken)
    {
        var engine = new LaneEngine<LaneTransportFake>(transport, policy, options);
        var counts = await engine.RunAsync(cancellationToken).ConfigureAwait(false);
        return (counts, engine.OpenResult);
    }
}
