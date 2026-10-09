using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// The arm's side of <c>outstandingAtTeardown</c>: the sum of the two halves the policy and the
/// engine each own. The lane facts pin where each half comes from; this one pins that
/// the arm publishes their sum, so dropping either term fails a fact instead of passing unnoticed
/// because the counter is zero in every baseline.
/// </summary>
public sealed class LatencyArmTotalsTests
{
    private const uint ConnectionId = 0x7100_0001u;
    private const int PayloadBytes = 24;

    [Fact]
    public async Task OutstandingAtTeardownIsThePolicyBookPlusTheEnginesDeferredQueue()
    {
        // The offer loop's first slot pays the JIT cost of the whole send path, and this fact's window
        // is a wall-clock bound, so a loaded host can otherwise spend the window compiling slot one.
        await LaneTestOptions.WarmAsync();

        // A real lane rather than a hand-made snapshot: the arm's own book is what the policy writes,
        // the engine drives it against a transport that never answers, and the window never reopens.
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var lane = new Lane<UdpLatencyState>();
        var policy = new UdpLatencyPolicy(lane.Book, ConnectionId, PayloadBytes, window: 2, new LogHistogram());
        var engine = new LaneEngine<LaneTransportFake>(new LaneTransportFake(log), policy, Options(offerSeconds: 0.2));

        lane.Counts = await engine.RunAsync(cancellation.Token).ConfigureAwait(false);

        // Both halves have to be non-zero for the sum below to be a fact rather than an identity: the
        // window holds what it sent and the bounded queue holds what it could not let out. How many
        // slots the window got through is the host's to decide, so the halves are asserted non-empty
        // and the counters they are made of are pinned by the policy facts, not by this one.
        Assert.True(lane.Book.Pending > 0, $"the fact needs requests no reply consumed, but the lane sent {lane.Counts.SentOk}");
        Assert.True(lane.Counts.DeferredPending > 0, $"the fact needs intents the window never let out, but {lane.Counts.DeferredQueued} slots were deferred and {lane.Counts.DeferredDropped} dropped");

        // TotalUdp's two calls, in its order: the engine's counts and the policy's book, and then the
        // pending half. The arm keeps no third opinion about either number.
        var totals = new LatencyTotals();
        totals.AddUdp(lane);
        totals.AddPending(lane.Book.Pending);

        Assert.Equal(lane.Book.Pending + lane.Counts.DeferredPending, totals.Outstanding);
    }

    private static LaneEngineOptions Options(double offerSeconds) => new()
    {
        RatePerSecond = 2000,
        StartTicks = Clock.Now,
        DeadlineTicks = Clock.Now + Clock.FromSeconds(offerSeconds),
        BacklogLimit = 4,
        SendBufferBytes = FrameCodec.HeaderSize + PayloadBytes + FrameCodec.TrailerSize,
        ReceiveBufferBytes = FrameCodec.HeaderSize + PayloadBytes + FrameCodec.TrailerSize,
        DrainLimitTicks = Clock.FromSeconds(0.05),
    };
}
