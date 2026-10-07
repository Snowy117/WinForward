using System.Net.Sockets;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The five <c>gates/clientSendLoss</c> values each arm derives from its own counters, one fact per
/// arm, each driving that counter to a non-zero value: a gate that is only ever asserted at zero would
/// pass for a literal. The four terms of the LOSS and MIX formula are driven one at a time as well,
/// because "the client destroyed this datagram" has to be true of the out-of-range slot too (D2/D7).
/// </summary>
public sealed class ClientSendLossGateTests
{
    private static readonly long s_windowTicks = UdpLossMath.WindowTicks(UdpLossMath.DefaultWindowMilliseconds);

    // The idle arm is the one arm with no send-side counter at all, so its gate cannot be driven: the
    // fact pins the reason -- the arm publishes no traffic and therefore no loss -- instead of a zero
    // that would read as a measurement.
    [Fact]
    public async Task TheIdleArmPublishesAStructurallyZeroLossGate()
    {
        await using var fixture = ArmRunFixture.Create(
            ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp));

        var outcome = await IdleArm.RunAsync(fixture.ContextFor(new ArmSpec { Name = "IDLE", Kind = "idle", Seconds = 0.05 }));

        Assert.Equal(0, outcome.Gates[ArmKeys.Common.Gates.ClientSendLoss]);
        Assert.IsType<IdleMetrics>(outcome.Metrics);

        // The gate is zero because the arm has nothing to lose, not because it lost nothing: no idle
        // record carries a send, a window or an arrival counter at all.
        Assert.Equal(
            [ArmKeys.Common.Gates.ClientSendLoss, ArmKeys.Common.Gates.WindowMs],
            outcome.Gates.Keys.Order(StringComparer.Ordinal));
    }

    // #12/DNS: the arm's own drop is the pacing slot the in-flight window refused. The black hole
    // never answers, so the window fills and the refusal is guaranteed rather than hoped for.
    [Fact]
    public async Task TheDnsArmGatesTheQueriesItsWindowRefusedToSend()
    {
        var dnsPort = ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp);
        using var blackHole = ArmRunFixture.BindBlackHoleUdp(dnsPort);
        await using var fixture = ArmRunFixture.Create(
            ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp),
            dnsPort);

        var outcome = await DnsArm.RunAsync(fixture.ContextFor(new ArmSpec
        {
            Name = "DNS",
            Kind = "dns",
            Seconds = 0.5,
            RatePerSecond = 4000,
            TcpPercent = 0,
        }));

        var metrics = Assert.IsType<DnsMetrics>(outcome.Metrics);
        Assert.True(metrics.Sent > 0, "the arm sent nothing, so its refusal path was never reached");
        Assert.True(metrics.Unsent > 0, $"the window never filled: sent {metrics.Sent}, socketErrors {metrics.SocketErrors}");

        Assert.Equal(metrics.Unsent, outcome.Gates[ArmKeys.Common.Gates.ClientSendLoss]);
    }

    // #12/DNS negative population: a socket error belongs to no single query, so it is not the pacing
    // slot a full in-flight window refused (D19.2 ④), and a formula that folded it in would publish the
    // same number as `unsent` on every run whose socket never failed. Nothing is bound to the DNS port,
    // so the resolver's socket fails after its first send and the two populations differ: `unsent` is the
    // slot the arm skipped, `socketErrors` is the failure that ended the phase.
    [Fact]
    public async Task TheDnsArmDoesNotGateItsSocketErrorsAsClientSendLoss()
    {
        var dnsPort = ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp);
        await using var fixture = ArmRunFixture.Create(
            ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp),
            dnsPort);

        var outcome = await DnsArm.RunAsync(fixture.ContextFor(new ArmSpec
        {
            Name = "DNS",
            Kind = "dns",
            Seconds = 0.5,
            RatePerSecond = 200,
            TcpPercent = 0,
        }));

        var metrics = Assert.IsType<DnsMetrics>(outcome.Metrics);
        Assert.True(metrics.SocketErrors > 0, $"no socket failed, so this fact drove nothing: sent {metrics.Sent}, unsent {metrics.Unsent}");
        Assert.Equal(metrics.Unsent, outcome.Gates[ArmKeys.Common.Gates.ClientSendLoss]);
    }

    // #12/THRU: the frames the socket refused. The listener resets every connection it accepts, so the
    // sends that follow the reset throw and the arm counts them instead of publishing a clean gate.
    [Fact]
    public async Task TheThroughputArmGatesTheFramesItsSocketRefused()
    {
        var tcpPort = ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp);
        using var resetting = ArmRunFixture.BindResettingTcp(tcpPort);
        await using var fixture = ArmRunFixture.Create(
            tcpPort,
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp));

        var outcome = await ThroughputArm.RunAsync(fixture.ContextFor(new ArmSpec
        {
            Name = "THRU",
            Kind = "throughput",
            Seconds = 1.0,
            Streams = 1,
            TargetBytesPerSecond = 100_000_000,
        }));

        var metrics = Assert.IsType<ThroughputMetrics>(outcome.Metrics);
        Assert.Equal(1, metrics.StreamConnects);
        Assert.True(
            metrics.SendFailures > 0,
            $"no send failed, so the fact drove nothing: framesSent {metrics.FramesSent}, frames {metrics.Frames}, bytesSent {metrics.BytesSent}");
        Assert.Equal(metrics.SendFailures, outcome.Gates[ArmKeys.Common.Gates.ClientSendLoss]);
    }

    // #12/REL: the scheduled slots no attempt ran for. The arm back-pressures rather than discards, so
    // the difference is zero on a run that retired its attempts -- which is exactly what the arm run
    // below pins -- and the forged pair below drives the production difference to a non-zero value.
    [Fact]
    public async Task TheReliabilityArmGatesTheScheduledSlotsNoAttemptRanFor()
    {
        await using var fixture = ArmRunFixture.Create(
            ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp));

        var outcome = await ReliabilityArm.RunAsync(fixture.ContextFor(new ArmSpec
        {
            Name = "REL",
            Kind = "reliability",
            Seconds = 1.0,
            ConnectionsPerSecond = 50,
        }));

        var metrics = Assert.IsType<ReliabilityMetrics>(outcome.Metrics);
        Assert.True(metrics.ScheduledAttempts > 0, "the arm scheduled nothing, so the difference is unmeasured");
        Assert.Equal(metrics.ScheduledAttempts, metrics.ConnectAttempts);
        Assert.Equal(
            ReliabilityMetricsWriter.ClientSendLoss(metrics.ScheduledAttempts, metrics.ConnectAttempts),
            outcome.Gates[ArmKeys.Common.Gates.ClientSendLoss]);

        Assert.Equal(3, ReliabilityMetricsWriter.ClientSendLoss(scheduledAttempts: 5, connectAttempts: 2));
        Assert.Equal(0, ReliabilityMetricsWriter.ClientSendLoss(scheduledAttempts: 7, connectAttempts: 7));
    }

    // The second half of the fact above: on every run that retires its attempts the difference is zero,
    // so a call site that wrote a literal 0 would publish the same gate and no arm-level fact could tell
    // them apart. The scan is what holds the arm to the derived value; the negative control is the same
    // line rewritten as `= 0L`, which this fails on while the fact above still passes.
    [Fact]
    public void TheReliabilityArmPublishesTheDerivedDifferenceRatherThanALiteral()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "benchmarks", "WinForward.E2E", "Client", "Arms", "ReliabilityArm.cs"));
        var collapsed = string.Join(' ', source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains(
            $"[{nameof(ArmKeys)}.{nameof(ArmKeys.Common)}.{nameof(ArmKeys.Common.Gates)}.{nameof(ArmKeys.Common.Gates.ClientSendLoss)}] = "
            + $"{nameof(ReliabilityMetricsWriter)}.{nameof(ReliabilityMetricsWriter.ClientSendLoss)}(metrics.ScheduledAttempts, metrics.ConnectAttempts),",
            collapsed,
            StringComparison.Ordinal);
    }

    // D7: the four terms of the loss arm's own formula, driven one at a time. The out-of-range slot is
    // the new one, and it is driven by the same call an arm makes: it is refused, it is not sent, it is
    // in no bucket, and it is still client send loss.
    [Fact]
    public void TheLossArmGateIsEveryWayTheClientDestroysADatagram()
    {
        var tracker = DrivenTracker(out var counts);

        Assert.Equal(1, counts.Undetermined);
        Assert.Equal(0, counts.Arrived + counts.Late + counts.Never + counts.CorruptDatagrams);
        Assert.Equal(1, tracker.SentOk);
        Assert.Equal(1, tracker.SentOutOfRange);
        Assert.Equal(2, tracker.OutOfRange);

        Assert.Equal(4, LossArm.ClientSendLoss(tracker, counts));
    }

    // The same four terms for the MIX UDP class, through its production fold: the returned gate, the
    // class block and the top-level metrics are one value.
    [Fact]
    public void TheMixUdpClassGateIsEveryWayTheClientDestroysADatagram()
    {
        var tracker = DrivenTracker(out var counts);
        var trackers = new[] { tracker };
        var observationEnds = new[] { Clock.Now };

        var (metrics, clientSendLoss, _) = MixMetricsWriter.WriteMetrics(
            new MixCounters(1),
            trackers,
            observationEnds,
            UdpLossMath.DefaultWindowMilliseconds,
            s_windowTicks,
            Clock.Now);

        Assert.Equal(1, counts.Undetermined);
        Assert.Equal(1, metrics.Classes.Udp.Sent);
        Assert.Equal(1, metrics.Classes.Udp.SentOutOfRangeSequences);
        Assert.Equal(2, metrics.Classes.Udp.OutOfRangeSequences);
        Assert.Equal(4, clientSendLoss);
        Assert.Equal(4, metrics.ClientSendLoss);
        Assert.Equal(4, metrics.Classes.Udp.ClientSendLoss);
    }

    // One datagram per term, and a different count per side of the D7 refusal: a send that threw, a slot
    // the window refused, a datagram still inside its window when observation stopped, one slot the send
    // side refused as out of range, and two corrupt arrivals naming sequences the receive side refused.
    // The two refusal counts differ, so the sum cannot be reproduced by the wrong side's counter and
    // neither assertion can pass by being the other one's zero.
    private static UdpReliabilityTracker DrivenTracker(out LossCounts counts)
    {
        var tracker = new UdpReliabilityTracker();
        var now = Clock.Now;
        tracker.MarkSupplied();
        tracker.MarkSent(1, now);
        tracker.MarkSendFailure();
        tracker.MarkWindowOverflow();
        tracker.MarkSent(UdpReliabilityTracker.MaxSequence + 1, now);
        tracker.MarkCorruptWithKnownSequence(long.MaxValue);
        tracker.MarkCorruptWithKnownSequence(long.MaxValue - 1);

        counts = tracker.Classify(s_windowTicks, observationEndTicks: now);
        return tracker;
    }
}
