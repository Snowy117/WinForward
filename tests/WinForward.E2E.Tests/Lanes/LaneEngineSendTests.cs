using Xunit;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// What each of the three slot decisions does to the lane's counts: a send that is accepted, one
/// that is refused, one that throws, the <c>WouldBlock</c> reading, a skipped slot, and the bounded
/// defer queue with its oldest-first retry and its drops. Every fact also asserts the two contracts
/// that ride along — <c>OnSent</c> ran before the next <c>BuildRequest</c>, and the engine never
/// disposes a transport it did not create.
/// </summary>
public sealed class LaneEngineSendTests
{
    [Fact]
    public async Task EverySlotSendsAndThePolicyBooksEachSendAsAccepted()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LanePolicyFake(log);
        var transport = new LaneTransportFake(log) { CancelAfter = cancellation, CancelAfterSends = 5 };

        var (counts, open) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Loop(), cancellation.Token);

        Assert.True(open.Ok);
        Assert.Equal(5, counts.Supplied);
        Assert.Equal(5, counts.SentOk);
        Assert.Equal(0, counts.SendFailures);
        Assert.Equal(0, counts.SendWouldBlock);
        Assert.Equal(0, counts.DeferredQueued);
        Assert.Equal(0, counts.DeferredDropped);
        Assert.True(counts.ScheduleTruncated, "the run was stopped before its deadline, so the tail of the schedule was never offered");

        Assert.Equal(5, policy.AcceptedSends);
        Assert.Equal(0, policy.RefusedSends);
        Assert.All(policy.Sends, send => Assert.True(send.Accepted));
        Assert.Equal([1, 2, 3, 4, 5], transport.SentSequences);
        Assert.Equal([1, 2, 3, 4, 5], policy.BuiltSequences);

        Assert.Equal(0, policy.SerialityViolations);
        Assert.Equal(0, policy.ReentrancyViolations);
        Assert.Equal(0, transport.OverlapViolations);
        Assert.Equal(0, transport.DisposeCalls);
    }

    [Fact]
    public async Task ARefusedSendCountsAsFailureAndReachesThePolicyWithAcceptedFalse()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LanePolicyFake(log);
        var transport = new LaneTransportFake(log)
        {
            Behavior = LaneSendBehavior.Refuse,
            CancelAfter = cancellation,
            CancelAfterSends = 3,
        };

        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Loop(), cancellation.Token);

        Assert.Equal(3, counts.Supplied);
        Assert.Equal(0, counts.SentOk);
        Assert.Equal(3, counts.SendFailures);
        Assert.Equal(0, counts.SendWouldBlock);
        Assert.Equal(3, policy.RefusedSends);
        Assert.Equal(0, policy.AcceptedSends);
        Assert.All(policy.Sends, send => Assert.False(send.Accepted));
        Assert.All(policy.Sends, send => Assert.Equal("the fake refused this send", send.Error));
    }

    [Fact]
    public async Task AThrownSocketErrorFailsOneRequestAndTheLoopContinues()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LanePolicyFake(log);
        var transport = new LaneTransportFake(log)
        {
            Behavior = LaneSendBehavior.Throw,
            CancelAfter = cancellation,
            CancelAfterSends = 3,
        };

        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Loop(), cancellation.Token);

        Assert.Equal(3, transport.SendCalls);
        Assert.Equal(3, counts.SendFailures);
        Assert.Equal(0, counts.SentOk);
        Assert.Equal(3, policy.RefusedSends);
        Assert.All(policy.Sends, send => Assert.False(send.Accepted));
        Assert.Equal(0, policy.SerialityViolations);
    }

    [Fact]
    public async Task AnIncompleteSendIsCountedAsWouldBlockEvenWhenTheTransportDoesNotSaySo()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LanePolicyFake(log);
        var transport = new LaneTransportFake(log)
        {
            IncompleteSends = true,
            CancelAfter = cancellation,
            CancelAfterSends = 3,
        };

        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Loop(), cancellation.Token);
        var diagnostic = $"supplied {counts.Supplied}, sentOk {counts.SentOk}, wouldBlock {counts.SendWouldBlock}, failures {counts.SendFailures}, sends {transport.SendCalls}";

        Assert.True(counts.SentOk == 3, diagnostic);
        Assert.True(counts.SendWouldBlock == 3, diagnostic);
        Assert.All(policy.Sends, send => Assert.False(send.WouldBlock, diagnostic));
    }

    [Fact]
    public async Task AReportedWouldBlockIsCountedOncePerSendAndNotTwice()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LanePolicyFake(log);
        var transport = new LaneTransportFake(log)
        {
            IncompleteSends = true,
            ReportWouldBlock = true,
            CancelAfter = cancellation,
            CancelAfterSends = 3,
        };

        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Loop(), cancellation.Token);
        var diagnostic = $"supplied {counts.Supplied}, sentOk {counts.SentOk}, wouldBlock {counts.SendWouldBlock}, failures {counts.SendFailures}, sends {transport.SendCalls}";

        Assert.True(counts.SentOk == 3, diagnostic);
        Assert.True(counts.SendWouldBlock == 3, diagnostic);
        Assert.All(policy.Sends, send => Assert.True(send.WouldBlock, diagnostic));
    }

    [Fact]
    public async Task ASkippedSlotCountsNothingAndIsStillASuppliedSlot()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LanePolicyFake(log, LanePolicyBehavior.Skip) { CancelAfter = cancellation, CancelAfterSettles = 4 };
        var transport = new LaneTransportFake(log);

        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Loop(), cancellation.Token);

        Assert.Equal(4, counts.Supplied);
        Assert.Equal(0, counts.SentOk);
        Assert.Equal(0, counts.SendFailures);
        Assert.Equal(0, counts.SendWouldBlock);
        Assert.Equal(0, counts.DeferredQueued);
        Assert.Equal(0, counts.DeferredDropped);
        Assert.Equal(0, transport.SendCalls);
        Assert.Empty(policy.Sends);
        Assert.Equal(4, policy.OfferedSlots);
    }

    [Fact]
    public async Task ADeferredIntentGoesOutLaterInFifoOrderAndNeverCountsASecondSlot()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();

        // Window of one, and the first reply is booked late (from the third settle on): slots 2 and 3
        // find the window closed, and from slot 4 the deferred intents are retried oldest first.
        var policy = new LanePolicyFake(log, LanePolicyBehavior.Window, window: 1)
        {
            AnswerFromSettle = 3,
            CancelAfter = cancellation,
            CancelAfterSettles = 6,
        };
        var transport = new LaneTransportFake(log);

        // Paced on purpose (5 ms a slot): with no pace every intended instant is the start instant, so
        // the retry-instant equality below could not fail even for a retry that dropped its own instant.
        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Loop(ratePerSecond: 200), cancellation.Token);

        Assert.Equal(6, counts.Supplied);
        Assert.Equal(5, counts.SentOk);
        Assert.Equal(5, counts.DeferredQueued);
        Assert.Equal(0, counts.DeferredDropped);
        Assert.Equal([1, 2, 3, 4, 5], transport.SentSequences);

        // A retry is the same intent asked again: the sequence repeats and so does the instant it was
        // first wanted for. It is not a new slot, which is why the retries never move Supplied.
        var retries = Array.FindAll(policy.Requests, request => request.IsRetry);
        Assert.NotEmpty(retries);
        Assert.True(retries[0].IntendedTicks != retries[^1].IntendedTicks, "the paced loop has to give the retries distinct intended instants, or the equality below is vacuous");
        Assert.All(retries, retry =>
        {
            var first = Array.Find(policy.Requests, request => !request.IsRetry && request.Sequence == retry.Sequence);
            Assert.Equal(first.IntendedTicks, retry.IntendedTicks);
        });
        Assert.True(policy.BuildCalls > counts.Supplied, "the retries went through BuildRequest without adding a slot");
        Assert.Equal(counts.Supplied, policy.OfferedSlots);
        Assert.Equal(0, policy.SerialityViolations);
    }

    [Fact]
    public async Task AFullDeferQueueDropsTheNewestIntentAndCountsItInBothCounters()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();

        // Window of one and no reply ever arrives, so every slot but the first is deferred; the queue
        // holds two, so the rest are dropped. The backlogs stay empty, which is the drop-newest shape.
        var policy = new LanePolicyFake(log, LanePolicyBehavior.Window, window: 1) { CancelAfter = cancellation, CancelAfterSettles = 6 };
        var transport = new LaneTransportFake(log);

        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Loop(backlogLimit: 2), cancellation.Token);

        Assert.Equal(6, counts.Supplied);
        Assert.Equal(1, counts.SentOk);
        Assert.Equal(5, counts.DeferredQueued);
        Assert.Equal(3, counts.DeferredDropped);
        Assert.True(counts.DeferredDropped < counts.DeferredQueued, "a dropped slot still found the window closed, so it counts as deferred too");
        Assert.Equal([1], transport.SentSequences);
    }
}
