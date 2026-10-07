using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// #9: a request that is still in flight when the offer loop ends is sampled, not cancelled with the
/// socket. The fact stages the lane's only reply for an instant past the deadline, so the histogram
/// sample can only come from the drain that keeps receiving and settling after the last offer.
/// </summary>
/// <remarks>
/// The offer loop offers only while its window is open, so the window is sized to absorb a host stall
/// of hundreds of milliseconds (`test-stability.md` §2.9) rather than to be consumed by one, and both
/// warm-ups keep first-call compilation out of it. The reply is staged past the deadline by far less
/// than the drain bound, so it is booked inside the drain and can never outlive it.
/// </remarks>
public sealed class LatencyArmDrainSamplingTests
{
    private const uint ConnectionId = 0x7100_0002u;
    private const int PayloadBytes = 24;
    private const double OfferSeconds = 0.5;
    private const double ReplyAfterDeadlineSeconds = 0.02;

    [Fact]
    public async Task AReplyThatLandsAfterTheOfferLoopStillReachesTheTcpHistogram()
    {
        await LaneTestOptions.WarmAsync();
        await WarmTheArmPolicyAsync();

        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var deadlineTicks = Clock.Now + Clock.FromSeconds(OfferSeconds);
        var state = new LatencyTcpState();
        var histogram = new LogHistogram();
        var policy = new LatencyTcpPolicy(state, ConnectionId, PayloadBytes, window: 4, histogram);
        var transport = new LaneTransportFake(log)
        {
            PayloadAtReceive = 1,
            PayloadNotBeforeTicks = deadlineTicks + Clock.FromSeconds(ReplyAfterDeadlineSeconds),
        };

        var engine = new LaneEngine<LaneTransportFake>(transport, policy, Options(deadlineTicks));
        var counts = await engine.RunAsync(cancellation.Token).ConfigureAwait(false);

        Assert.True(counts.SentOk > 0, $"the offer loop sent nothing ({counts.Supplied} slots supplied, scheduleTruncated {counts.ScheduleTruncated}), so no request was in flight to sample");
        Assert.Equal(1, state.Received);
        Assert.Equal(1, histogram.Count);
    }

    /// <summary>
    /// Runs one throwaway lane through the arm's own policy, transport and reply path, which is what
    /// <see cref="LaneTestOptions.WarmAsync"/> does not cover: it warms the engine against the policy
    /// fake, while the measured run above is the first to compile <see cref="LatencyTcpPolicy"/>.
    /// </summary>
    private static async Task WarmTheArmPolicyAsync()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new LatencyTcpPolicy(new LatencyTcpState(), ConnectionId, PayloadBytes, window: 4, new LogHistogram());
        var transport = new LaneTransportFake(log) { CancelAfter = cancellation, CancelAfterSends = 3, PayloadAtReceive = 1 };

        var engine = new LaneEngine<LaneTransportFake>(transport, policy, Options(Clock.Now + Clock.FromSeconds(30)));
        await engine.RunAsync(cancellation.Token).ConfigureAwait(false);
    }

    private static LaneEngineOptions Options(long deadlineTicks) => new()
    {
        RatePerSecond = 200,
        StartTicks = Clock.Now,
        DeadlineTicks = deadlineTicks,
        BacklogLimit = 4,
        SendBufferBytes = FrameCodec.HeaderSize + PayloadBytes + FrameCodec.TrailerSize,
        ReceiveBufferBytes = FrameCodec.HeaderSize + PayloadBytes + FrameCodec.TrailerSize,
        DrainLimitTicks = Clock.FromSeconds(0.2),
    };
}
