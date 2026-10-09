using WinForward.E2E.Client;
using WinForward.E2E.Client.Lanes;
using Xunit;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// How a lane ends: a transport that never opened offers nothing, and the grace drain runs to the
/// policy's <c>BookEmpty</c> or to its bound — never forever, and never past a book that is already
/// empty.
/// </summary>
public sealed class LaneEngineTeardownTests
{
    [Fact]
    public async Task ATransportThatDoesNotOpenOffersNothingAndSaysTheScheduleWasShort()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LanePolicyFake(log);
        var transport = new LaneTransportFake(log) { Open = new LaneOpenResult(false, "connection refused") };

        var engine = new LaneEngine<LaneTransportFake>(transport, policy, LaneTestOptions.Loop());
        var counts = await engine.RunAsync(cancellation.Token);

        Assert.False(engine.OpenResult.Ok);
        Assert.Equal("connection refused", engine.OpenResult.Error);
        Assert.Equal(0, counts.Supplied);
        Assert.Equal(0, counts.SentOk);
        Assert.True(counts.ScheduleTruncated, "a lane that never connected offered none of its share of the schedule");
        Assert.Equal(0, transport.SendCalls);
        Assert.Equal(0, transport.ReceiveCalls);
        Assert.Equal(0, policy.SettleCalls);
    }

    [Fact]
    public async Task TheGraceDrainStopsAsSoonAsTheBookIsEmpty()
    {
        await LaneTestOptions.WarmAsync();
        var log = new LaneEventLog();
        var policy = new LanePolicyFake(log, LanePolicyBehavior.Skip) { GraceTicksBeforeEmpty = 2 };
        var transport = new LaneTransportFake(log);

        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Deadline(200, 0.03, 1), CancellationToken.None);

        Assert.True(counts.Supplied > 0, "the offer loop has to run for the drain to have a book to wait on");

        // Two drain ticks emptied the staged book, and the engine settled once more after the join.
        Assert.Equal(3, policy.SettlesAfterTheOfferLoop);
        Assert.True(policy.BookEmpty);
    }

    [Fact]
    public async Task AnEmptyBookEndsTheGraceDrainWithoutASettle()
    {
        await LaneTestOptions.WarmAsync();
        var log = new LaneEventLog();
        var policy = new LanePolicyFake(log, LanePolicyBehavior.Skip);
        var transport = new LaneTransportFake(log);

        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Deadline(200, 0.03, 1), CancellationToken.None);

        Assert.True(counts.Supplied > 0);

        // Nothing to wait for: the drain never ticked, so the only settle after the offer loop is the
        // one the engine runs after the join.
        Assert.Equal(1, policy.SettlesAfterTheOfferLoop);
        Assert.True(policy.BookEmpty);
    }

    [Fact]
    public async Task TheGraceDrainStopsAtItsBoundWhenTheBookNeverEmpties()
    {
        await LaneTestOptions.WarmAsync();
        var log = new LaneEventLog();
        var policy = new LanePolicyFake(log, LanePolicyBehavior.Skip) { GraceTicksBeforeEmpty = long.MaxValue };
        var transport = new LaneTransportFake(log);

        // A 50 ms bound: the drain must give up and return rather than hold the arm open, and the book
        // is still not empty when it does — the only other ways out of the drain are an empty book and a
        // receive loop that has already ended, and both are false here. How many ticks fit in the bound
        // is the host's business, so the fact only pins that the drain ticked at all.
        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Deadline(200, 0.03, 0.05), CancellationToken.None);

        Assert.True(counts.Supplied > 0);
        Assert.True(policy.SettlesAfterTheOfferLoop >= 2, $"the drain ticked {policy.SettlesAfterTheOfferLoop - 1} times before its bound");
        Assert.False(policy.BookEmpty);
        Assert.True(policy.IsDrained);
    }

    [Fact]
    public async Task AReceiveLoopThatEndedOnItsOwnEndsTheGraceDrain()
    {
        await LaneTestOptions.WarmAsync();
        var log = new LaneEventLog();
        var policy = new LanePolicyFake(log, LanePolicyBehavior.Skip) { GraceTicksBeforeEmpty = long.MaxValue };
        var transport = new LaneTransportFake(log) { EndOfStreamAtReceive = 1 };

        // The book never empties, so only the third way out of the drain can end it promptly: the loop the
        // peer closed. A drain that ignored that condition would tick out its whole one-second bound.
        var begin = Clock.Now;
        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Deadline(200, 0.03, 1), CancellationToken.None);
        var elapsed = Clock.ToSeconds(Clock.Now - begin);

        Assert.True(counts.Supplied > 0);
        Assert.Equal(1, transport.ReceiveCalls);
        Assert.False(policy.BookEmpty);
        Assert.Equal(1, policy.SettlesAfterTheOfferLoop);
        Assert.True(elapsed < 0.5, $"the drain ran {elapsed:F3} s against a receive loop that had already ended");
    }

    [Fact]
    public async Task TheReceiveLoopIsJoinedBeforeTheFinalSettle()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LanePolicyFake(log);
        var transport = new LaneTransportFake(log) { CancelAfter = cancellation, CancelAfterSends = 3 };

        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Loop(), cancellation.Token);

        Assert.Equal(3, counts.SentOk);

        // The fake's receive never returns on its own, so the loop only ends when the engine joins it:
        // the cancellation of that parked receive is recorded, and the arm-end settle has to come after
        // it. Every settle also has to come after the receive loop started.
        var events = log.Snapshot();
        var firstReceive = Array.FindIndex(events, laneEvent => laneEvent.Kind == LaneEventKind.Receive);
        var receiveEnded = Array.FindIndex(events, laneEvent => laneEvent.Kind == LaneEventKind.ReceiveEnded);
        var lastSettle = Array.FindLastIndex(events, laneEvent => laneEvent.Kind == LaneEventKind.Settle);
        Assert.True(firstReceive >= 0, "the receive loop never started");
        Assert.True(receiveEnded > firstReceive, "the parked receive was never cancelled, so the loop was never joined");
        Assert.True(lastSettle > receiveEnded, "the arm-end settle ran before the receive loop was joined");
        Assert.True(policy.SettleCalls > counts.Supplied, "the arm-end settle runs after the offer loop, not only inside it");
    }
}
