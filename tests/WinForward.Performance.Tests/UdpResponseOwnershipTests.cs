using System.Net;
using WinForward.Benchmarks;
using WinForward.Benchmarks.Stability;
using WinForward.Core;
using WinForward.Runtime.UdpProxy;
using Xunit;

namespace WinForward.Performance.Tests;

/// <summary>
/// The per-flow response ownership check in the two sinks the session-budget tests do not already
/// cover: a reply may only become a flow's first response when it arrived on the flow whose id its
/// payload carries. Each case fails the way the sharing comparison did before this check — a
/// sibling's reply recorded as the flow's own answer — and each sink's own flow-id space is
/// exercised, because the burst sink's burst population is not zero-based.
/// </summary>
public sealed class UdpResponseOwnershipTests
{
    [Fact]
    public async Task ChurnSinkCreditsOnlyTheFlowsOwnReplies()
    {
        var keys = CreateFlowKeys(3);
        var sink = new ChurnCountingSink(3, keys);
        sink.BeginWave();

        // Flow 1's relay socket receives flow 2's echo (the shared association's last-sender write
        // path), then flow 1's own echo, then flow 0's echo arrives at flow 1 as well.
        await InjectAsync(sink, keys[1], flowId: 2);
        Assert.Equal(0, sink.FirstResponses);
        Assert.Null(sink.TryGetFirstResponseTicks(1));
        Assert.Null(sink.TryGetFirstResponseTicks(2));

        await InjectAsync(sink, keys[1], flowId: 1);
        Assert.Equal(1, sink.FirstResponses);
        Assert.NotNull(sink.TryGetFirstResponseTicks(1));

        // Flow 0's echo arrives on flow 1's socket too. Flow 1 was answered by its own echo, so it
        // is an own response, not a misdelivery; flows 0 and 2 are the wave's two unanswered flows.
        await InjectAsync(sink, keys[1], flowId: 0);
        Assert.Equal(1, sink.FirstResponses);
        Assert.Equal(2, sink.Misdelivered);
        Assert.Null(sink.TryGetFirstResponseTicks(0));
        Assert.Null(sink.TryGetFirstResponseTicks(2));
    }

    [Fact]
    public async Task ChurnSinkResetsMisdeliveryBetweenWaves()
    {
        var keys = CreateFlowKeys(2);
        var sink = new ChurnCountingSink(2, keys);

        sink.BeginWave();
        await InjectAsync(sink, keys[1], flowId: 0);
        Assert.Equal(1, sink.Misdelivered);

        // The next wave starts clean: the same misdelivery has to be re-observed, so a wave's row
        // never inherits the previous wave's count. Every reply carries this wave's sequence, which
        // is what the wave guard matches.
        sink.BeginWave();
        await InjectAsync(sink, keys[0], flowId: 0, sequence: 2);
        await InjectAsync(sink, keys[1], flowId: 1, sequence: 2);
        Assert.Equal(0, sink.Misdelivered);
        Assert.Equal(2, sink.FirstResponses);
    }

    [Fact]
    public async Task BurstSinkRejectsAForeignBurstReplyAndKeepsTheTwoFlowIdRangesApart()
    {
        var (sink, backgroundKeys, burstKeys, tracker) = CreateBurstSink(backgroundFlows: 2, burstFlows: 2);

        // Burst flow 0 receives burst flow 1's echo, then its own.
        await InjectAsync(sink, burstKeys[0], flowId: 3);
        Assert.Equal(0, sink.BurstFirstResponses);
        Assert.Null(sink.TryGetBurstFirstResponseTicks(0));
        Assert.Null(sink.TryGetBurstFirstResponseTicks(1));

        await InjectAsync(sink, burstKeys[0], flowId: 2);
        Assert.Equal(1, sink.BurstFirstResponses);
        Assert.NotNull(sink.TryGetBurstFirstResponseTicks(0));
        Assert.Equal(1, sink.MisdeliveredBurstFlows);

        // A warm-up reply that arrives on a sibling's socket is not the sibling's answer, and it
        // never reaches the tracker: the warm-up wait must observe only replies to its own flows.
        await InjectAsync(sink, backgroundKeys[1], flowId: 0);
        Assert.False(tracker.AttributionActive);
        Assert.Equal(0, sink.WarmupResponses);
        Assert.Equal(1, sink.MisdeliveredBackgroundFlows);

        await InjectAsync(sink, backgroundKeys[0], flowId: 0);
        Assert.Equal(1, sink.WarmupResponses);
    }

    [Fact]
    public async Task BurstSinkDoesNotAttributeAForeignReplyToAnInFlightDatagram()
    {
        var (sink, backgroundKeys, _, tracker) = CreateBurstSink(backgroundFlows: 2, burstFlows: 1);
        tracker.BeginAttribution();
        tracker.Stamp(flowId: 1, sequence: 7, BackgroundWindow.Burst);

        // The reply for background flow 1's in-flight datagram arrives on background flow 0: the
        // ownership check runs before the tracker, so the stamp is neither taken nor credited.
        await InjectAsync(sink, backgroundKeys[0], flowId: 1, sequence: 7);
        Assert.Equal(0, sink.InjectedIn(BackgroundWindow.Burst));
        Assert.True(tracker.TryTake(flowId: 1, sequence: 7, out _));

        // The same reply on its own flow is credited to the window it was sent in.
        tracker.Stamp(flowId: 1, sequence: 8, BackgroundWindow.Post);
        await InjectAsync(sink, backgroundKeys[1], flowId: 1, sequence: 8);
        Assert.Equal(1, sink.InjectedIn(BackgroundWindow.Post));
        Assert.Equal(1, sink.MisdeliveredBackgroundFlows);
    }

    private static (BurstCountingSink Sink, FlowKey[] BackgroundKeys, FlowKey[] BurstKeys, InFlightTracker Tracker) CreateBurstSink(int backgroundFlows, int burstFlows)
    {
        var flowKeys = CreateFlowKeys(backgroundFlows + burstFlows);
        var tracker = new InFlightTracker(capacity: 64);
        return (
            new BurstCountingSink(backgroundFlows, burstFlows, tracker, flowKeys),
            flowKeys[..backgroundFlows],
            flowKeys[backgroundFlows..],
            tracker);
    }

    private static FlowKey[] CreateFlowKeys(int count)
    {
        var keys = new FlowKey[count];
        for (var index = 0; index < count; index++) keys[index] = BenchmarkShared.CreateFlowKey(index);
        return keys;
    }

    private static ValueTask InjectAsync(IUdpResponseSink sink, FlowKey arrivingFlow, int flowId, long sequence = 1)
    {
        var payload = new byte[16];
        DatagramHeader.Write(payload, sequence, flowId);
        var remote = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        return sink.InjectAsync(arrivingFlow, remote, payload, MacAddress.Invalid, CancellationToken.None);
    }
}
