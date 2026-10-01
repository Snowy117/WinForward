using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FlowBuilders;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// Association death, in-place recovery, and the fail-closed path (I4/I5/I7): the watchdog notices
/// the control connection ending, a same-family re-association re-points every attached flow
/// without tearing a session down, an unrecoverable or address-family-changing death faults the
/// lease so the next send throws <see cref="UdpAssociationLostException"/>, and the coordinator
/// removes the flow with the association-lost reason without arming the setup cooldown.
/// </summary>
public sealed class UdpAssociationRecoveryTests
{
    private const int PayloadLength = 16;

    [Fact]
    public async Task WatchdogRecoversInPlaceAndTheSameLeaseKeepsSendingToTheNewRelay()
    {
        using var firstRelay = NewRelaySocket();
        using var secondRelay = NewRelaySocket();
        var firstEndpoint = (IPEndPoint)firstRelay.LocalEndPoint!;
        var secondEndpoint = (IPEndPoint)secondRelay.LocalEndPoint!;
        await using var server = new ScriptedSocks5UdpServer(firstEndpoint);
        var registry = new SelfTrafficRegistry();
        await using var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Always);
        var lease = await pool.RentAsync(server.Server, CancellationToken.None);
        var transport = Socks5UdpTransport.Create(lease, registry);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];
        var recoveredBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationRecovered);

        await transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(1, await ReceiveCountAsync(firstRelay, CancellationToken.None));
        // One control connection, exactly one UDP ASSOCIATE on it.
        Assert.Equal(1, server.AssociateReplyCount);
        var local = Endpoint.From(transport.LocalEndpoint.Address, checked((ushort)transport.LocalEndpoint.Port));
        var firstContext = RelayContext(local, firstEndpoint);
        var secondContext = RelayContext(local, secondEndpoint);
        Assert.True(registry.IsOwned(firstContext));

        // The server drops the association and advertises a different relay for the re-dial.
        server.AdvertiseNext(secondEndpoint);
        server.DropControlConnections();

        await WaitForAsync(() => server.ConnectionCount == 2 && lease.RelayEndpoint.Port == secondEndpoint.Port);
        Assert.Equal(recoveredBefore + 1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationRecovered));
        // The recovery dial performed its own single ASSOCIATE, never a second one on either connection.
        Assert.Equal(2, server.AssociateReplyCount);

        Assert.False(lease.IsFaulted);
        Assert.Equal(secondEndpoint, transport.RelayEndpoint);
        // The same transport keeps its socket and session: the next datagram reaches the new relay.
        await transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(1, await ReceiveCountAsync(secondRelay, CancellationToken.None));
        // The send rebound the loop-prevention tuple: the old relay registration is gone and the new
        // one is owned, so a catch-all proxy rule cannot recursively intercept the relay traffic.
        Assert.False(registry.IsOwned(firstContext));
        Assert.True(registry.IsOwned(secondContext));

        await transport.DisposeAsync();
        Assert.Equal(1, server.LiveConnectionCount);
        // Disposal released the one registration it owned: the swap did not leak either tuple.
        Assert.False(registry.IsOwned(secondContext));
    }

    [Fact]
    public async Task AddressFamilyChangeFaultsTheLeaseAndTheNextSendThrows()
    {
        using var relay = NewRelaySocket();
        using var ipv6Relay = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        ipv6Relay.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        await using var server = new ScriptedSocks5UdpServer((IPEndPoint)relay.LocalEndPoint!);
        var registry = new SelfTrafficRegistry();
        await using var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Always);
        var lease = await pool.RentAsync(server.Server, CancellationToken.None);
        var transport = Socks5UdpTransport.Create(lease, registry);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];
        await transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(1, await ReceiveCountAsync(relay, CancellationToken.None));

        server.AdvertiseNext((IPEndPoint)ipv6Relay.LocalEndPoint!);
        server.DropControlConnections();

        await WaitForAsync(() => lease.IsFaulted);

        Assert.NotNull(lease.Fault);
        // Attached flows cannot follow a family change: their relay socket is bound to the old family.
        Assert.Equal(AddressFamily.InterNetwork, lease.RelayAddressFamily);
        var lost = await Assert.ThrowsAsync<UdpAssociationLostException>(async () => await transport.SendSpanAsync(destination, payload, CancellationToken.None));
        Assert.Same(lease.Fault, lost.InnerException);
        await WaitForAsync(() => server.LiveConnectionCount == 0);

        await transport.DisposeAsync();
        Assert.Equal(0, pool.AssociationCount);
    }

    [Fact]
    public async Task FailedRecoveryFaultsTheLeaseClosesTheControlConnectionAndLeavesNoUnobservedFault()
    {
        using var relay = NewRelaySocket();
        await using var server = new ScriptedSocks5UdpServer((IPEndPoint)relay.LocalEndPoint!);
        var registry = new SelfTrafficRegistry();
        var probe = new UnobservedExceptionProbe();
        var handler = new UnobservedTaskExceptionEventHandler(probe);
        // The unobserved-exception event is process-global, so the probe counts a foreign fault while
        // its target is unset. The placeholder closes that window before the test's first await; the
        // real target (the fault the failed recovery stored) replaces it once it exists.
        probe.Track(new IOException("placeholder"));
        TaskScheduler.UnobservedTaskException += handler.OnUnobserved;
        try
        {
            var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Always);
            var lease = await pool.RentAsync(server.Server, CancellationToken.None);
            var transport = Socks5UdpTransport.Create(lease, registry);
            var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
            var payload = new byte[PayloadLength];
            await transport.SendSpanAsync(destination, payload, CancellationToken.None);
            Assert.Equal(1, await ReceiveCountAsync(relay, CancellationToken.None));

            // A dead SOCKS5 server: the listener is gone, so the re-dial is refused.
            await server.StopAsync();
            await WaitForAsync(() => lease.IsFaulted);
            probe.Track(lease.Fault!);

            await Assert.ThrowsAsync<UdpAssociationLostException>(async () => await transport.SendSpanAsync(destination, payload, CancellationToken.None));
            await WaitForAsync(() => server.LiveConnectionCount == 0);

            await transport.DisposeAsync();
            await pool.DisposeAsync();
            Assert.Equal(0, pool.AssociationCount);
            Assert.Equal(0, pool.LeasedFlowCount);

            UnobservedExceptionProbe.ForceFinalization();
            Assert.Equal(0, probe.Count);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler.OnUnobserved;
        }
    }

    [Fact]
    public async Task CoordinatorRemovesTheFlowOnAssociationLossWithoutArmingASetupCooldown()
    {
        using var relay = NewRelaySocket();
        var relayEndpoint = (IPEndPoint)relay.LocalEndPoint!;
        var server = new ScriptedSocks5UdpServer(relayEndpoint);
        var registry = new SelfTrafficRegistry();
        var controlPort = server.ControlEndpoint.Port;
        await using var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Always);
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            new Socks5UdpTransportFactory(pool, registry, UdpFrameBuilder.DefaultMaximumEthernetFrame),
            new NoopResponseSink(),
            new UdpProxyOptions { Capacity = 16 });
        var flow = CreateFlow("192.0.2.53");
        var payload = new byte[PayloadLength];
        var serverKey = server.Server;
        try
        {
            Assert.True(await coordinator.TrySendSpanAsync(flow, serverKey, payload, default, CancellationToken.None));
            Assert.Equal(1, await ReceiveCountAsync(relay, CancellationToken.None));
            Assert.Equal(1, coordinator.SessionCount);

            // The association dies unrecoverably: the server is gone. The loss counter is
            // process-global and other collections lose associations in parallel, so only its
            // counted direction is asserted here; the local witnesses are the removed session and
            // the un-armed cooldown below.
            var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);
            await server.StopAsync();
            await WaitUntilSendFailsAsync(coordinator, flow, serverKey, payload);
            Assert.True(RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) >= lostBefore + 1);

            // I5: no setup cooldown was armed for the association loss.
            Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);
            Assert.Equal(0, coordinator.SessionCount);

            // The flow re-establishes on its next datagram, immediately.
            await using var restarted = await ScriptedSocks5UdpServer.StartOnPortAsync(controlPort, relayEndpoint);
            Assert.True(await coordinator.TrySendSpanAsync(flow, restarted.Server, payload, default, CancellationToken.None));
            Assert.Equal(1, await ReceiveCountAsync(relay, CancellationToken.None));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task CoordinatorKeepsTheSessionAliveAcrossASameFamilyInPlaceRecovery()
    {
        using var firstRelay = NewRelaySocket();
        using var secondRelay = NewRelaySocket();
        var firstEndpoint = (IPEndPoint)firstRelay.LocalEndPoint!;
        var secondEndpoint = (IPEndPoint)secondRelay.LocalEndPoint!;
        var server = new ScriptedSocks5UdpServer(firstEndpoint);
        var registry = new SelfTrafficRegistry();
        var serverKey = server.Server;
        await using var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Always);
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            new Socks5UdpTransportFactory(pool, registry, UdpFrameBuilder.DefaultMaximumEthernetFrame),
            new NoopResponseSink(),
            new UdpProxyOptions { Capacity = 16 });
        var flow = CreateFlow("192.0.2.53");
        var payload = new byte[PayloadLength];
        var recoveredBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationRecovered);
        try
        {
            Assert.True(await coordinator.TrySendSpanAsync(flow, serverKey, payload, default, CancellationToken.None));
            Assert.Equal(1, await ReceiveCountAsync(firstRelay, CancellationToken.None));
            Assert.Equal(1, coordinator.SessionCount);

            server.AdvertiseNext(secondEndpoint);
            server.DropControlConnections();

            // The flow's session survives the whole in-place re-association: the relay moves under
            // it, but no datagram, lease, or socket is lost and no teardown is started.
            Assert.Equal(1, coordinator.SessionCount);
            await WaitForAsync(() => RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationRecovered) >= recoveredBefore + 1);
            Assert.Equal(1, coordinator.SessionCount);

            Assert.True(await coordinator.TrySendSpanAsync(flow, serverKey, payload, default, CancellationToken.None));
            Assert.Equal(1, await ReceiveCountAsync(secondRelay, CancellationToken.None));
            // A recovery is not an association loss: the coordinator removes a session only on
            // AssociationLost, so the surviving session (asserted here and above) is the local
            // witness. The process-global loss counter is shared with parallel collections and
            // cannot attribute an increment to this test.
            Assert.Equal(1, coordinator.SessionCount);
            Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AssociationLostWhileTheSetupQueueFlushesIsALossNotASetupFailure()
    {
        var factory = new SetupFlushLostTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new NoopResponseSink(), new UdpProxyOptions { Capacity = 16 });
        var flow = CreateFlow("192.0.2.53");
        var server = new Socks5Server("scripted", "127.0.0.1", 1080, Username: null, Password: null);
        var payload = new byte[PayloadLength];
        var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);

        // The first datagram is admitted while the association is still setting up, so it waits in
        // the setup queue; the association then dies before that queue is flushed through it.
        Assert.True(await coordinator.TrySendSpanAsync(flow, server, payload, default, CancellationToken.None));
        await factory.FirstCreateStarted.Task.WaitAsync(CancellationToken.None);
        Assert.Equal(1, coordinator.SessionCount);
        factory.LoseAssociationOnFlush();

        await WaitForAsync(() => coordinator.SessionCount == 0);
        // I5/R3: the flush-window loss is the same association loss the ready path reports — its own
        // counter (asserted in its counted direction only: the global counter is shared with parallel
        // collections) and no 1 s cooldown, which is the local discriminator from a setup failure.
        Assert.True(RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) >= lostBefore + 1);
        Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);

        // The very next datagram starts a fresh setup instead of being refused by a cooldown.
        Assert.True(await coordinator.TrySendSpanAsync(flow, server, payload, default, CancellationToken.None));
        // Why not an immediate assert: the setup ring runs the dial on a pooled worker, so the second
        // create is asynchronous and a synchronous assert would race the scheduler under load.
        await WaitForAsync(() => factory.CreateCalls == 2);
    }

    [Fact]
    public async Task ARecoveryThatNeverCompletesFaultsTheLeaseInsideTheInjectedBudget()
    {
        using var relay = NewRelaySocket();
        await using var server = new ScriptedSocks5UdpServer((IPEndPoint)relay.LocalEndPoint!);
        var registry = new SelfTrafficRegistry();
        var attempts = 0;
        await using var pool = new UdpAssociationPool(
            registry,
            UdpAssociationReuseMode.Always,
            createControl: async (target, token) =>
            {
                if (Interlocked.Increment(ref attempts) == 1) return await Socks5ControlConnection.ConnectAsync(target, token).ConfigureAwait(false);
                // A dial that never completes: only the recovery budget can end it.
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                throw new InvalidOperationException("The recovery dial was expected to be cancelled by its budget.");
            },
            recoveryTimeout: TimeSpan.FromMilliseconds(200));
        var lease = await pool.RentAsync(server.Server, CancellationToken.None);
        var transport = Socks5UdpTransport.Create(lease, registry);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];
        await transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(1, await ReceiveCountAsync(relay, CancellationToken.None));

        server.DropControlConnections();

        // The wait is shorter than the 5 s default budget: a hard-coded budget, or a watchdog that
        // never gives up, fails here instead of passing.
        await WaitForAsync(() => lease.IsFaulted, timeoutMs: 4_000);
        Assert.NotNull(lease.Fault);
        await Assert.ThrowsAsync<UdpAssociationLostException>(async () => await transport.SendSpanAsync(destination, payload, CancellationToken.None));

        // Exactly one re-dial, and no retry storm after the budget expired.
        Assert.Equal(2, attempts);
        await Task.Delay(250);
        Assert.Equal(2, attempts);

        await transport.DisposeAsync();
    }

    /// <summary>The loop-prevention context of one relay tuple for a flow's local relay endpoint.</summary>
    private static FlowContext RelayContext(Endpoint local, IPEndPoint relay)
    {
        var relayEndpoint = Endpoint.From(relay.Address, checked((ushort)relay.Port));
        return Context(FlowKey.Create(local, relayEndpoint, TransportProtocol.Udp, FlowOriginKind.Host));
    }

    private static Socket NewRelaySocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    /// <summary>Receives one datagram; the receive itself is the proof that the send reached this relay.</summary>
    private static async Task<int> ReceiveCountAsync(Socket relay, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        var result = await relay.ReceiveFromAsync(buffer, SocketFlags.None, sender, budget.Token);
        return result.ReceivedBytes > 0 ? 1 : 0;
    }

    private static async Task WaitUntilSendFailsAsync(UdpProxyCoordinator coordinator, FlowKey flow, Socks5Server server, byte[] payload)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try
            {
                _ = await coordinator.TrySendSpanAsync(flow, server, payload, default, CancellationToken.None);
            }
            catch (UdpAssociationLostException)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "the association loss never surfaced on the send path");
            await Task.Delay(20);
        }
    }

    /// <summary>Adapter so the process-global unobserved-exception event can be unsubscribed by the test.</summary>
    private sealed class UnobservedTaskExceptionEventHandler(UnobservedExceptionProbe probe)
    {
        public void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args) => probe.OnUnobserved(sender, args);
    }

    /// <summary>
    /// A transport factory whose first transport appears only once the test releases it, with sends
    /// that report the association lost: the shape of an association dying after ASSOCIATE but
    /// before the setup queue is flushed through its transport. Later transports are healthy, so
    /// the test can prove the flow is free to set up again.
    /// </summary>
    private sealed class SetupFlushLostTransportFactory : IUdpProxyTransportFactory
    {
        private readonly TaskCompletionSource<FakeTransport> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        /// <summary>Completes once the first dial is in flight, with the flow's datagram already queued.</summary>
        public TaskCompletionSource<bool> FirstCreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateCalls => Volatile.Read(ref _calls);

        public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstCreateStarted.TrySetResult(true);
                return await _first.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new FakeTransport(AddressFamily.InterNetwork, 40_100 + Volatile.Read(ref _calls));
        }

        /// <summary>Completes the queued first dial with a transport whose sends report the loss.</summary>
        public void LoseAssociationOnFlush() =>
            _first.TrySetResult(new FakeTransport(AddressFamily.InterNetwork, 40_000)
            {
                SendFault = new UdpAssociationLostException("The flow's SOCKS5 UDP association was lost; the flow must be re-established."),
            });
    }
}
