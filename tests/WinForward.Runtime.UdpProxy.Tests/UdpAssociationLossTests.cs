using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FlowBuilders;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// Association death is the flow's death. A control connection that ends mid-flow faults the flow's
/// own association; the transport refuses the next datagram with
/// <see cref="UdpAssociationLostException"/>, the coordinator removes the slot as
/// <see cref="UdpTeardownReason.AssociationLost"/> without arming the setup cooldown, and the flow
/// re-establishes on its next datagram. Nothing is passed as a fallback, and a lost association is
/// never counted or reported as a setup failure.
/// </summary>
[Collection(UdpAssociationLostCounterCollection.Name)]
public sealed class UdpAssociationLossTests
{
    private const int PayloadLength = 16;

    [Fact]
    public async Task ControlConnectionDeathFailsTheFlowClosedAndTheNextDatagramReestablishes()
    {
        using var relay = NewRelaySocket();
        var relayEndpoint = (IPEndPoint)relay.LocalEndPoint!;
        var server = new ScriptedSocks5UdpServer(relayEndpoint);
        var registry = new SelfTrafficRegistry();
        var controlPort = server.ControlEndpoint.Port;
        var logger = new RecordingLogger();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            new Socks5UdpTransportFactory(registry, UdpFrameBuilder.DefaultMaximumEthernetFrame, logger: logger),
            new NoopResponseSink(),
            new UdpProxyOptions { Capacity = 16 });
        var flow = CreateFlow("192.0.2.53");
        var payload = new byte[PayloadLength];
        var serverKey = server.Server;
        try
        {
            Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(serverKey), payload, default, CancellationToken.None));
            Assert.Equal(1, await ReceiveCountAsync(relay, CancellationToken.None));
            Assert.Equal(1, coordinator.SessionCount);

            var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);
            await server.StopAsync();

            // Fail closed: the datagram makes the send path throw the association's death (it is
            // never delivered to the relay, and never downgraded to a pass), so the coordinator
            // removes the slot with the association-lost reason.
            var lost = await WaitUntilSendFailsAsync(coordinator, flow, serverKey, payload);
            AssertCarriesTheControlStreamDeath(lost);
            Assert.True(RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) >= lostBefore + 1);
            Assert.Contains(logger.Events, static recorded => string.Equals(recorded.Name, "udp.association.lost", StringComparison.Ordinal));
            await WaitForAsync(() => server.LiveConnectionCount == 0);

            // I5: no setup cooldown was armed for the association loss.
            Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);
            Assert.Equal(0, coordinator.SessionCount);

            // The flow re-establishes on its next datagram, immediately, and is served.
            await using var restarted = await ScriptedSocks5UdpServer.StartOnPortAsync(controlPort, relayEndpoint);
            Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(restarted.Server), payload, default, CancellationToken.None));
            Assert.Equal(1, await ReceiveCountAsync(relay, CancellationToken.None));
            Assert.Equal(1, coordinator.SessionCount);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ControlDeathFailsTheTransportClosedAndClosesTheControlConnection()
    {
        using var relay = NewRelaySocket();
        await using var server = new ScriptedSocks5UdpServer((IPEndPoint)relay.LocalEndPoint!);
        await using var transport = await UdpTransportTestFactory.CreateAsync(server.Server, new SelfTrafficRegistry());
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];

        await transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(1, await ReceiveCountAsync(relay, CancellationToken.None));

        server.DropControlConnections();

        var lost = await WaitUntilTransportFailsAsync(transport, destination, payload);
        // The raised exception carries the death the watchdog diagnosed, and the dead control
        // connection is closed rather than held until the flow's transport is disposed.
        AssertCarriesTheControlStreamDeath(lost);
        await WaitForAsync(() => server.LiveConnectionCount == 0);
    }

    [Fact]
    public async Task ControlDeathLeavesNoUnobservedTaskFault()
    {
        using var relay = NewRelaySocket();
        await using var server = new ScriptedSocks5UdpServer((IPEndPoint)relay.LocalEndPoint!);
        var probe = new UnobservedExceptionProbe();
        var handler = new UnobservedTaskExceptionEventHandler(probe);
        // The unobserved-exception event is process-global, so the probe counts a foreign fault while
        // its target is unset. The placeholder closes that window before the test's first await; the
        // real target (the fault the watchdog records) replaces it once it exists.
        probe.Track(new IOException("placeholder"));
        TaskScheduler.UnobservedTaskException += handler.OnUnobserved;
        try
        {
            var transport = await UdpTransportTestFactory.CreateAsync(server.Server, new SelfTrafficRegistry());
            var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
            var payload = new byte[PayloadLength];
            await transport.SendSpanAsync(destination, payload, CancellationToken.None);
            Assert.Equal(1, await ReceiveCountAsync(relay, CancellationToken.None));

            server.DropControlConnections();
            var lost = await WaitUntilTransportFailsAsync(transport, destination, payload);
            probe.Track(lost);

            await transport.DisposeAsync();
            await WaitForAsync(() => server.LiveConnectionCount == 0);

            UnobservedExceptionProbe.ForceFinalization();
            Assert.Equal(0, probe.Count);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler.OnUnobserved;
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
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(server), payload, default, CancellationToken.None));
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
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(server), payload, default, CancellationToken.None));
        // Why not an immediate assert: the setup ring runs the dial on a pooled worker, so the second
        // create is asynchronous and a synchronous assert would race the scheduler under load.
        await WaitForAsync(() => factory.CreateCalls == 2);
    }

    /// <summary>
    /// The teardown reason on its own, with no coordinator and nothing to arm: the mapping is what
    /// makes an association death a counted <see cref="UdpTeardownReason.AssociationLost"/> teardown,
    /// and it is what keeps the flow out of the <see cref="UdpTeardownReason.SetupFailure"/> branch
    /// that arms the setup cooldown, so a lost association never waits to re-establish.
    /// </summary>
    [Fact]
    public void AnAssociationLostExceptionIsTornDownAsAssociationLost()
    {
        var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);

        Assert.Equal(
            UdpTeardownReason.AssociationLost,
            UdpProxyCoordinator.TeardownReasonFor(new UdpAssociationLostException("The flow's SOCKS5 UDP association was lost; the flow must be re-established.")));
        // The counted direction the process-global counter allows, as in the end-to-end tests above.
        Assert.True(RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) >= lostBefore + 1);
        // The mapping discriminates: every other send fault is the generic Fault, and no send fault is
        // ever the SetupFailure a cooldown is armed for.
        Assert.Equal(UdpTeardownReason.Fault, UdpProxyCoordinator.TeardownReasonFor(new IOException("the relay socket faulted")));
        Assert.NotEqual(UdpTeardownReason.SetupFailure, UdpProxyCoordinator.TeardownReasonFor(new SocketException((int)SocketError.ConnectionReset)));
    }

    /// <summary>
    /// The death the watchdog diagnosed rides the raised exception as its inner exception. A
    /// server-side close arrives as a clean EOF or as a reset, depending on how the peer's socket
    /// went away, so both are the control stream ending.
    /// </summary>
    private static void AssertCarriesTheControlStreamDeath(UdpAssociationLostException lost) =>
        Assert.True(
            lost.InnerException is IOException or SocketException,
            $"expected the control-stream death as the inner exception, observed {lost.InnerException?.GetType().Name ?? "null"}");

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

    /// <summary>
    /// Drives the coordinator's send path until the flow's association loss surfaces, and returns
    /// the exception the transport raised. The loss is detected by a background watchdog, so the
    /// first send after the control connection ends may still find a healthy association.
    /// </summary>
    private static async Task<UdpAssociationLostException> WaitUntilSendFailsAsync(UdpProxyCoordinator coordinator, FlowKey flow, Socks5Server server, byte[] payload)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try
            {
                _ = await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(server), payload, default, CancellationToken.None);
            }
            catch (UdpAssociationLostException lost)
            {
                return lost;
            }

            Assert.True(DateTime.UtcNow < deadline, "the association loss never surfaced on the send path");
            await Task.Delay(20);
        }
    }

    /// <summary>Drives one transport's send path until its association loss surfaces.</summary>
    private static async Task<UdpAssociationLostException> WaitUntilTransportFailsAsync(Socks5UdpTransport transport, Endpoint destination, byte[] payload)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try
            {
                await transport.SendSpanAsync(destination, payload, CancellationToken.None);
            }
            catch (UdpAssociationLostException lost)
            {
                return lost;
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

        public async ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
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
