using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FlowBuilders;

namespace WinForward.Performance.Tests;

public sealed class UdpWarmPathGateTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    [Fact]
    public async Task UdpReadyDatagramTakesZeroCoordinatorGateEntriesAndZeroClockReads()
    {
        var time = new CountingTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { TimeProvider = time });
        var flow = CreateFlow("192.0.2.53");
        await EstablishReadySessionAsync(coordinator, flow);

        var gateEntries = 0;
        coordinator.GateHoldProbe = () => Interlocked.Increment(ref gateEntries);
        var entriesAtStart = coordinator.GateEntryCountForDiagnostics;
        time.ThrowOnRead = true;
        var readsAtStart = time.Reads;

        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [2], default, CancellationToken.None));

        Assert.Equal(entriesAtStart, coordinator.GateEntryCountForDiagnostics);
        Assert.Equal(readsAtStart, time.Reads);
        Assert.Equal(0, gateEntries);
        time.ThrowOnRead = false;
    }

    [Fact]
    public async Task UdpReadySendTakesZeroActivityGateEntries()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink());
        var flow = CreateFlow("192.0.2.53");
        await EstablishReadySessionAsync(coordinator, flow);
        var session = coordinator.SessionForDiagnostics(flow);
        Assert.NotNull(session);

        session.ActivityGateHoldProbe = static () => { };
        var entriesAtStart = session.ActivityGateEntryCountForDiagnostics;
        for (var index = 0; index < 32; index++)
        {
            Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [(byte)index], default, CancellationToken.None));
        }

        Assert.Equal(entriesAtStart, session.ActivityGateEntryCountForDiagnostics);
    }

    [Fact]
    public async Task UdpReadySendCompletesWhileCoordinatorGateIsHeld()
    {
        var factory = new FakeTransportFactory();
        var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? holder = null;
        Thread? sender = null;
        try
        {
            var flow = CreateFlow("192.0.2.53");
            await EstablishReadySessionAsync(coordinator, flow);

            var parked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.GateHoldProbe = () =>
            {
                parked.TrySetResult(true);
                release.Task.Wait(TimeSpan.FromSeconds(10));
            };

            var held = -1;
            holder = new Thread(() => held = coordinator.SessionCount)
            {
                IsBackground = true,
                Name = "udp-coordinator-gate-holder",
            };

            holder.Start();
            try
            {
                Assert.True(await parked.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System), "no thread parked inside the coordinator gate");

                var sent = false;
                sender = new Thread(() =>
                {
                    var pending = coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [9], default, CancellationToken.None);
                    if (pending.IsCompletedSuccessfully) sent = pending.Result;
                })
                {
                    IsBackground = true,
                    Name = "udp-ready-send-during-gate-hold",
                };
                sender.Start();
                Assert.True(sender.Join(TimeSpan.FromSeconds(10)), "the ready send queued behind the parked coordinator gate");
                Assert.True(sent);
            }
            finally
            {
                release.TrySetResult();
            }

            Assert.True(holder.Join(TimeSpan.FromSeconds(10)));
            Assert.True(held >= 0);
        }
        finally
        {
            // Disposal runs only after every thread that captured the coordinator has been joined.
            release.TrySetResult();
            holder?.Join(TimeSpan.FromSeconds(10));
            sender?.Join(TimeSpan.FromSeconds(10));
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendIsAdmittedWhileTheSweeperHoldsTheActivityGate()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeTransportFactory();
        var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { TimeProvider = time });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? sweeper = null;
        Thread? sender = null;
        try
        {
            var flow = CreateFlow("192.0.2.53");
            await EstablishReadySessionAsync(coordinator, flow);
            var session = coordinator.SessionForDiagnostics(flow);
            Assert.NotNull(session);

            var parked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.ActivityGateHoldProbe = () =>
            {
                parked.TrySetResult(true);
                release.Task.Wait(TimeSpan.FromSeconds(10));
            };

            time.Advance(TimeSpan.FromMinutes(2));
            coordinator.ActivityClock.Tick();
            Task<int>? sweep = null;
            sweeper = new Thread(() => sweep = coordinator.RemoveExpiredAsync(time.GetUtcNow(), TimeSpan.FromMinutes(1)).AsTask())
            {
                IsBackground = true,
                Name = "udp-expiry-sweeper",
            };

            sweeper.Start();
            Assert.True(await parked.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System), "the sweeper never reached the session's activity gate");

            var sent = false;
            sender = new Thread(() =>
            {
                var pending = coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [7], default, CancellationToken.None);
                if (pending.IsCompletedSuccessfully) sent = pending.Result;
            })
            {
                IsBackground = true,
                Name = "udp-ready-send-during-activity-gate-hold",
            };
            sender.Start();
            Assert.True(sender.Join(TimeSpan.FromSeconds(10)), "the ready send waited for the sweeper's activity gate");
            Assert.True(sent);

            release.TrySetResult();
            Assert.True(sweeper.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, await sweep!);
        }
        finally
        {
            // Disposal runs only after every thread that captured the coordinator has been joined; the
            // release first so a parked sweeper can never hold a session gate across the teardown.
            release.TrySetResult();
            sweeper?.Join(TimeSpan.FromSeconds(10));
            sender?.Join(TimeSpan.FromSeconds(10));
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task UdpSessionCacheMissesOnATornDownSlot()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink());
        var flow = CreateFlow("192.0.2.53");
        await EstablishReadySessionAsync(coordinator, flow);

        Assert.Equal(1, await coordinator.RemoveExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1)));
        Assert.Equal(0, coordinator.SessionCount);

        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [2], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 2);
        Assert.Equal(2, factory.Transports.Count);
    }

    [Fact]
    public async Task UdpSessionCacheNeverServesACollidingFlowsSession()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 32 });
        var (first, second) = FindCollidingFlows(coordinator.SessionCacheSlotCountForDiagnostics - 1);

        await EstablishReadySessionAsync(coordinator, first);
        var firstTransport = Assert.Single(factory.Transports);
        await EstablishReadySessionAsync(coordinator, second);
        var secondTransport = factory.Transports[1];
        Assert.Equal(2, coordinator.SessionCount);

        Assert.True(await coordinator.TrySendSpanAsync(first, ProxyTarget.FromServer(s_server), [3], default, CancellationToken.None));

        Assert.Equal(2, SentOn(firstTransport));
        Assert.Equal(1, SentOn(secondTransport));
        Assert.Equal(2, coordinator.SessionCount);
    }

    [Fact]
    public async Task UdpSessionCountMatchesTheLiveSessionsAcrossChurn()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { TimeProvider = time });
        var first = CreateFlow("192.0.2.53");
        var second = CreateFlow("192.0.2.54");
        await EstablishReadySessionAsync(coordinator, first);
        await EstablishReadySessionAsync(coordinator, second);
        Assert.Equal(2, coordinator.SessionCount);
        Assert.Equal(2, LiveTransports(factory));

        time.Advance(TimeSpan.FromMinutes(2));
        coordinator.ActivityClock.Tick();
        Assert.True(await coordinator.TrySendSpanAsync(second, ProxyTarget.FromServer(s_server), [3], default, CancellationToken.None));
        Assert.Equal(1, await coordinator.RemoveExpiredAsync(time.GetUtcNow(), TimeSpan.FromMinutes(1)));

        Assert.Equal(1, coordinator.SessionCount);
        Assert.Equal(1, LiveTransports(factory));
    }

    private static int SentOn(FakeTransport transport)
    {
        lock (transport.Sent) return transport.Sent.Count;
    }

    private static int LiveTransports(FakeTransportFactory factory)
    {
        lock (factory.Transports) return factory.Transports.Count(static transport => !transport.IsDisposed);
    }

    private static (FlowKey First, FlowKey Second) FindCollidingFlows(int mask)
    {
        var seen = new Dictionary<int, FlowKey>();
        for (var index = 0; index < 100_000; index++)
        {
            var candidate = FlowKey.Create(
                Endpoint.From(IPAddress.Parse("192.0.2.10"), (ushort)(20_000 + index)),
                Endpoint.From(IPAddress.Parse("192.0.2.99"), 53),
                TransportProtocol.Udp,
                FlowOriginKind.Host);
            var slot = candidate.GetHashCode() & mask;
            if (seen.TryGetValue(slot, out var first) && !first.Equals(candidate)) return (first, candidate);
            seen[slot] = candidate;
        }

        throw new InvalidOperationException("No ready-cache slot collision found for the session-cache fact.");
    }

    private static async Task EstablishReadySessionAsync(UdpProxyCoordinator coordinator, FlowKey flow)
    {
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        await WaitForAsync(() => coordinator.SessionReadyForDiagnostics(flow));
    }
}
