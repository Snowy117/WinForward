using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;

namespace WinForward.Runtime.Flow.Tests;

/// <summary>
/// The UDP leg releases idle relay sockets on its own fast cadence
/// (half the configured idle timeout, floored at 5 s) while the TCP-session and flow-table legs
/// keep the main sweep interval, so shortening UDP retention does not multiply the expensive
/// table sweeps. The fake clock gating the main legs starts ahead of the real clock, so the TCP
/// session and the flow created by the test are already idle against it — only the main-leg gate
/// can keep them alive.
/// </summary>
public sealed class IdleExpirySweeperCadenceTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);
    private static readonly TimeSpan s_mainInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_mainIdleTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_udpSweepInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task UdpLegSweepsOnTheFastCadenceWhileTheExpensiveLegsKeepTheMainInterval()
    {
        var udpLegRuns = 0;
        var transportFactory = new FakeTransportFactory();
        var udpCoordinator = UdpCoordinatorFakes.CreateCoordinator(
            transportFactory,
            new NoopResponseSink(),
            new UdpProxyOptions
            {
                Capacity = 16,
                BeforeExpiryRecheck = () =>
                {
                    // Counting the recheck counts UDP-leg runs: one idle session is offered to every
                    // run, and the failure leaves it in place for the next one.
                    // ReSharper disable once AccessToModifiedClosure // udpLegRuns is only ever touched through Interlocked/Volatile from the sweeper and the test thread, so the modification the inspection guards against is the checked access itself.
                    Interlocked.Increment(ref udpLegRuns);
                    return ValueTask.FromException(new IOException("hold the UDP session idle across ticks"));
                },
            });
        var harness = TcpCoordinatorFakes.CreateDispatcherHarness();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow.AddHours(1));

        var udpFlow = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        Assert.True(await udpCoordinator.TrySendSpanAsync(udpFlow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        await WaitForAsync(() => udpCoordinator.SessionState(udpFlow) == UdpSessionState.Active);

        // A half-open TCP redirect session (its accept is parked on the fake listener) and its flow
        // decision: both are idle against the fake clock and only the gated legs may expire them.
        // Dispatched through the dispatcher so the flow-table decision is created alongside the
        // redirect session the coordinator claims.
        await harness.Dispatcher.DispatchAsync(
            TcpCoordinatorFakes.MakeSynPacket(IPAddress.Parse("192.0.2.20"), IPAddress.Parse("192.0.2.80"), 53001, 443),
            CancellationToken.None);
        await harness.Coordinator.DrainPendingSetupsAsync();
        Assert.Equal(1, harness.Table.Count);
        Assert.Equal(1, harness.Dispatcher.FlowCount);

        var sweeper = new IdleExpirySweeper(
            harness.Dispatcher,
            harness.Coordinator,
            udpCoordinator,
            interval: s_mainInterval,
            flowIdleTimeout: s_mainIdleTimeout,
            redirectIdleTimeout: s_mainIdleTimeout,
            relayIdleTimeout: s_mainIdleTimeout,
            logger: harness.Logger,
            timeProvider: time,
            udpSweepInterval: s_udpSweepInterval);
        sweeper.Start();

        // Many fast ticks, no clock advance: the UDP leg ran each time, the main legs did not.
        await WaitForAsync(() => Volatile.Read(ref udpLegRuns) >= 5);
        Assert.Equal(1, harness.Table.Count);
        Assert.Equal(1, harness.Dispatcher.FlowCount);

        // One main interval on the injected clock: the next tick runs the TCP and flow legs too.
        time.Advance(s_mainInterval + TimeSpan.FromSeconds(1));
        await WaitForAsync(() => harness.Table.Count == 0 && harness.Dispatcher.FlowCount == 0);

        // The gate closes again right after a main sweep: a fresh idle flow decision survives many
        // fast UDP ticks and only expires on the next main-interval advance (a SYN-less straggler
        // packet is passed but still claims its flow decision).
        await harness.Dispatcher.DispatchAsync(
            TcpCoordinatorFakes.MakeForwardTcpPacket(IPAddress.Parse("192.0.2.30"), IPAddress.Parse("192.0.2.90"), 53002, 443, TcpCoordinatorFakes.TcpFlagAck),
            CancellationToken.None);
        Assert.Equal(1, harness.Dispatcher.FlowCount);
        var runsBeforeSecondPhase = Volatile.Read(ref udpLegRuns);
        await WaitForAsync(() => Volatile.Read(ref udpLegRuns) >= runsBeforeSecondPhase + 5);
        Assert.Equal(1, harness.Dispatcher.FlowCount);

        time.Advance(s_mainInterval + TimeSpan.FromSeconds(1));
        await WaitForAsync(() => harness.Dispatcher.FlowCount == 0);

        await sweeper.DisposeAsync();
        await udpCoordinator.DisposeAsync();
        await harness.Coordinator.DisposeAsync();
    }

    [Theory]
    [InlineData(600, 300)]
    [InlineData(30, 15)]
    [InlineData(8, 5)]
    [InlineData(5, 5)]
    public void DerivedUdpSweepIntervalIsHalfTheIdleTimeoutFlooredAtFiveSeconds(int idleSeconds, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), IdleExpirySweeper.DeriveUdpSweepInterval(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(idleSeconds), udpSweepInterval: null));

    [Fact]
    public void DerivedUdpSweepIntervalNeverExceedsTheMainInterval()
        => Assert.Equal(s_mainInterval, IdleExpirySweeper.DeriveUdpSweepInterval(s_mainInterval, TimeSpan.FromSeconds(600), udpSweepInterval: null));

    [Fact]
    public void NonPositiveUdpSweepIntervalIsRejectedAtConstruction()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new IdleExpirySweeper(
            CreateDispatcher(),
            tcp: null,
            udp: null,
            interval: s_mainInterval,
            udpSweepInterval: TimeSpan.Zero));

    private static FlowDispatcher CreateDispatcher()
    {
        var config = new ValidatedConfiguration(
            new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        return new FlowDispatcher(config, new FakeGuard(), new FakeExecutor());
    }
}
