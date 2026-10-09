using WinForward.E2E.Client;
using Xunit;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// The order the engine drives its seam in: <c>BuildRequest → SendAsync → OnSent</c> strictly serial,
/// <c>OnSent</c> before the next <c>BuildRequest</c>, and <c>Settle</c> after the slot's pace and
/// before the slot's request.
/// </summary>
public sealed class LaneEngineOrderTests
{
    [Fact]
    public async Task OnSentPrecedesTheNextBuildRequest()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LanePolicyFake(log);
        var transport = new LaneTransportFake(log) { CancelAfter = cancellation, CancelAfterSends = 4 };

        await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Loop(), cancellation.Token);

        // Strip the events that are not part of a slot's send cycle: what is left must be exactly
        // Build → Send → OnSent per slot, with no build ever slipping in front of an OnSent.
        var cycle = Array.FindAll(log.Snapshot(), laneEvent => laneEvent.Kind is LaneEventKind.Build or LaneEventKind.Send or LaneEventKind.Sent);
        Assert.Equal(
            [
                LaneEventKind.Build, LaneEventKind.Send, LaneEventKind.Sent,
                LaneEventKind.Build, LaneEventKind.Send, LaneEventKind.Sent,
                LaneEventKind.Build, LaneEventKind.Send, LaneEventKind.Sent,
                LaneEventKind.Build, LaneEventKind.Send, LaneEventKind.Sent,
            ],
            Array.ConvertAll(cycle, laneEvent => laneEvent.Kind));
        Assert.Equal(
            [1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4],
            Array.ConvertAll(cycle, laneEvent => laneEvent.Sequence));
        Assert.Equal(0, policy.SerialityViolations);
        Assert.Equal(0, policy.ReentrancyViolations);
    }

    [Fact]
    public async Task AnIncompleteSendIsAwaitedBeforeTheNextRequestIsBuilt()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LanePolicyFake(log);
        var transport = new LaneTransportFake(log)
        {
            // Every send suspends: the engine has to await each one before it can build the next, so a
            // send that overlapped another would show up here.
            IncompleteSends = true,
            CancelAfter = cancellation,
            CancelAfterSends = 4,
        };

        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, LaneTestOptions.Loop(), cancellation.Token);

        Assert.Equal(4, counts.SentOk);
        Assert.Equal(0, transport.OverlapViolations);
        Assert.Equal(0, policy.SerialityViolations);
        Assert.Equal(4, counts.SendWouldBlock);
    }

    [Fact]
    public async Task SettleRunsAfterTheSlotsPaceAndBeforeItsRequest()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LanePolicyFake(log) { CancelAfter = cancellation, CancelAfterSettles = 3 };
        var transport = new LaneTransportFake(log);

        // 200 offers a second: the instants are 5 ms apart, wide enough that a settle recorded before
        // the pace would land clearly before the instant the slot was paced for.
        var options = LaneTestOptions.Loop(ratePerSecond: 200);
        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, options, cancellation.Token);

        Assert.Equal(3, counts.SentOk);
        var pacer = new Pacer(options.RatePerSecond, options.StartTicks);
        var events = log.Snapshot();
        for (var sequence = 1L; sequence <= 3; sequence++)
        {
            var build = IndexOf(events, LaneEventKind.Build, sequence);
            Assert.True(build > 0, $"slot {sequence} never reached BuildRequest");

            // The last settle before this build is the loop's own: the receive loop may log its own
            // ending between the two, which says nothing about the offer loop's order.
            var settleIndex = LastIndexOf(events, LaneEventKind.Settle, build - 1);
            Assert.True(settleIndex >= 0, $"slot {sequence} was built without a settle in front of it");
            var settle = events[settleIndex];
            Assert.True(
                settle.Ticks >= pacer.IntendedTicks(sequence - 1),
                $"slot {sequence} settled {pacer.IntendedTicks(sequence - 1) - settle.Ticks} ticks before the instant it was paced for");

            // Build → Send → OnSent inside the slot, in that order.
            var send = IndexOf(events, LaneEventKind.Send, sequence);
            var sent = IndexOf(events, LaneEventKind.Sent, sequence);
            Assert.True(send > build && sent > send, $"slot {sequence} left the Build/Send/OnSent order");

            // ...and the next slot's settle comes after this slot's send was booked.
            if (sequence < 3)
            {
                var nextBuild = IndexOf(events, LaneEventKind.Build, sequence + 1);
                Assert.True(LastIndexOf(events, LaneEventKind.Settle, nextBuild - 1) > sent, $"slot {sequence + 1} settled before slot {sequence} was reported");
            }
        }
    }

    [Fact]
    public async Task TheSettlementCarriesTheReceiveInstantAndNotTheSettleInstant()
    {
        await LaneTestOptions.WarmAsync();

        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LanePolicyFake(log) { CancelAfter = cancellation, CancelAfterSettles = 3 };
        var transport = new LaneTransportFake(log) { ReplyTo = policy, ReplyAfterSends = [1] };

        // 20 offers a second: the reply is handed over inside slot 1 and the next slot books it 50 ms
        // later, so a settlement carrying the settle instant would land at or after that slot's pace.
        var options = LaneTestOptions.Loop(ratePerSecond: 20);
        var (counts, _) = await LaneTestOptions.RunAsync(transport, policy, options, cancellation.Token);

        var pacer = new Pacer(options.RatePerSecond, options.StartTicks);
        Assert.Equal(3, counts.SentOk);
        Assert.Equal(1, policy.SettledItems);
        Assert.True(
            policy.LastSettledReceivedTicks < pacer.IntendedTicks(1),
            $"the settlement carried {policy.LastSettledReceivedTicks}, which is not before the instant slot 1 was paced for ({pacer.IntendedTicks(1)})");
    }

    private static int LastIndexOf(LaneEvent[] events, LaneEventKind kind, int from)
    {
        for (var index = from; index >= 0; index--)
        {
            if (events[index].Kind == kind)
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOf(LaneEvent[] events, LaneEventKind kind, long sequence)
    {
        for (var index = 0; index < events.Length; index++)
        {
            if (events[index].Kind == kind && events[index].Sequence == sequence)
            {
                return index;
            }
        }

        return -1;
    }
}
