using System.Buffers.Binary;
using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

public sealed class TcpProxyCoordinatorLifecycleTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);
    private static readonly IPAddress s_clientIpv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destIpv4 = IPAddress.Parse("192.0.2.53");

    [Fact]
    public async Task ListenerAllocationFailureBlocks()
    {
        var listenerFactory = new FakeListenerFactory(throwOnCreate: true);
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

        var outcome = await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Blocked, outcome);
        Assert.Empty(injector.InjectedFrames);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public async Task RelaySetupFailureBlocksAndReleasesAlias()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var relayFactory = new FakeRelayFactory(throwOnEstablish: true);
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider());

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        var listener = Assert.Single(listenerFactory.Listeners);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(s_destIpv4, 53000)), CancellationToken.None);

        await WaitForAsync(() => table.Count == 0);

        Assert.Equal(0, table.Count);
        Assert.True(listener.IsDisposed);
        // No SYN-ACK was observed, so no client reset can be crafted: only the redirected SYN left.
        Assert.Single(injector.InjectedFrames);
    }

    [Fact]
    public async Task RelaySetupFailureInjectsClientResetWhenSequencesKnown()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var relayFactory = new FakeRelayFactory(throwOnEstablish: true);
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider());

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        // The listener's SYN-ACK passes the reverse hook, which records the server ISN.
        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var synAck = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerTuple.Port, s_destIpv4, 53000, mutateFrame: f => f[47] = 0x12);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleReverseAsync(synAck, CancellationToken.None));

        var listener = Assert.Single(listenerFactory.Listeners);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(s_destIpv4, 53000)), CancellationToken.None);
        await WaitForAsync(() => table.Count == 0);

        // Frame 3 is the crafted RST: original server -> client, in-window seq, RST|ACK.
        Assert.Equal(3, injector.InjectedFrames.Count);
        var reset = injector.InjectedFrames[2];
        Assert.True(reset.TowardMstcp);
        var frame = reset.Frame;
        Assert.Equal(s_destIpv4, new IPAddress(frame.AsSpan(26, 4).ToArray()));
        Assert.Equal(443u, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(34, 2)));
        Assert.Equal(s_clientIpv4, new IPAddress(frame.AsSpan(30, 4).ToArray()));
        Assert.Equal(53000u, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(36, 2)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(38, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(42, 4)));
        Assert.Equal(0x14, frame[47]);
    }

    [Fact]
    public async Task ForwardedRelayFailureInjectsClientResetTowardOriginAdapter()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var relayFactory = new FakeRelayFactory(throwOnEstablish: true);
        var forwardLocal = IPAddress.Parse("192.0.2.1");
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider(forwardLocal));

        var client = IPAddress.Parse("192.0.2.10");
        var syn = MakeForwardedSynPacket(client, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var synAck = MakeReversePacketClassifierOrientation(forwardLocal, listenerTuple.Port, client, 53000, mutateFrame: f => f[47] = 0x12);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleReverseAsync(synAck, CancellationToken.None));

        var listener = Assert.Single(listenerFactory.Listeners);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(client, 53000)), CancellationToken.None);
        await WaitForAsync(() => table.Count == 0);

        Assert.Equal(3, injector.InjectedFrames.Count);
        var reset = injector.InjectedFrames[2];
        Assert.False(reset.TowardMstcp);
        Assert.Equal((nint)0x1234, reset.AdapterHandle);
        var frame = reset.Frame;
        Assert.Equal(s_destIpv4, new IPAddress(frame.AsSpan(26, 4).ToArray()));
        Assert.Equal(client, new IPAddress(frame.AsSpan(30, 4).ToArray()));
        Assert.Equal(0x14, frame[47]);
    }

    [Fact]
    public async Task RelayFailureResetAcknowledgesObservedClientSequence()
    {
        // D1: the reset's ack must cover data the client already sent (plus FIN's sequence number),
        // not the handshake-era ISN+1 — an out-of-window ack would be dropped as a slow EOF instead
        // of aborting the connection.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var relayFactory = new FakeRelayFactory(throwOnEstablish: true);
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider());

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var synAck = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerTuple.Port, s_destIpv4, 53000, mutateFrame: f => f[47] = 0x12);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleReverseAsync(synAck, CancellationToken.None));

        // Client sends 5 bytes of request data (seq = ISN+1 = 2), then a FIN (seq = 7): the
        // tracker must end at 8 — payload plus the FIN's one sequence number.
        var data = MakeForwardTcpPacket(s_clientIpv4, s_destIpv4, 53000, 443, TcpFlagAck, [0x01, 0x02, 0x03, 0x04, 0x05],
            f => BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(38, 4), 2));
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandlePacketAsync(data, s_server, CancellationToken.None));
        var fin = MakeForwardTcpPacket(s_clientIpv4, s_destIpv4, 53000, 443, TcpFlagFinAck,
            mutateFrame: f => BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(38, 4), 7));
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandlePacketAsync(fin, s_server, CancellationToken.None));

        var listener = Assert.Single(listenerFactory.Listeners);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(s_destIpv4, 53000)), CancellationToken.None);
        await WaitForAsync(() => table.Count == 0);

        // Frame 5 is the crafted RST: server next seq (SYN-ACK only) = 2, client next seq = 8.
        Assert.Equal(5, injector.InjectedFrames.Count);
        var reset = injector.InjectedFrames[4];
        Assert.True(reset.TowardMstcp);
        var frame = reset.Frame;
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(38, 4)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(42, 4)));
        Assert.Equal(0x14, frame[47]);
    }

    [Fact]
    public async Task RelayFailureResetSequenceCoversObservedServerData()
    {
        // D1: the reset's seq must equally cover reverse-leg data (SYN counts one: SYN-ACK advanced
        // the tracker to ISN+1, then 8 payload bytes advance it to ISN+9), so the reset stays
        // in-window for the client's receive side too.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var relayFactory = new FakeRelayFactory(throwOnEstablish: true);
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider());

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var synAck = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerTuple.Port, s_destIpv4, 53000, mutateFrame: f => f[47] = 0x12);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleReverseAsync(synAck, CancellationToken.None));
        var serverData = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerTuple.Port, s_destIpv4, 53000,
            payload: [1, 2, 3, 4, 5, 6, 7, 8],
            mutateFrame: f =>
            {
                f[47] = TcpFlagAck;
                BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(38, 4), 2);
            });
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleReverseAsync(serverData, CancellationToken.None));

        var listener = Assert.Single(listenerFactory.Listeners);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(s_destIpv4, 53000)), CancellationToken.None);
        await WaitForAsync(() => table.Count == 0);

        // Frame 4 is the RST: server next seq = SYN-ACK(1) + 1 + 8 payload = 10; the client sent
        // no data, so its side degrades to the ISN+1 fallback.
        Assert.Equal(4, injector.InjectedFrames.Count);
        var frame = injector.InjectedFrames[3].Frame;
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(38, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(42, 4)));
        Assert.Equal(0x14, frame[47]);
    }

    [Fact]
    public async Task ExistingFlowInjectionFailureReleasesAssociation()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector(throwOnCall: 2);
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());
        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);

        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));
        var listener = Assert.Single(listenerFactory.Listeners);

        Assert.Equal(TcpRedirectOutcome.Blocked, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));
        Assert.Equal(0, table.Count);
        Assert.True(listener.IsDisposed);
        Assert.Single(injector.InjectedFrames);
    }

    [Fact]
    public async Task CancelledRetransmitDoesNotRetireSharedRedirect()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector(throwIfCanceled: true);
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());
        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);

        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.HandleSynAsync(syn, s_server, cancellation.Token).AsTask());

        Assert.Equal(1, table.Count);
        Assert.False(Assert.Single(listenerFactory.Listeners).IsDisposed);
    }

    [Fact]
    public async Task CancellationAfterSynDoesNotStopSharedAcceptLoop()
    {
        var listenerFactory = new FakeListenerFactory();
        var relayFactory = new FakeRelayFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider());
        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        using var cancellation = new CancellationTokenSource();

        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, cancellation.Token));
        cancellation.Cancel();

        var listener = Assert.Single(listenerFactory.Listeners);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(s_destIpv4, 53000)), CancellationToken.None);
        await WaitForAsync(() => relayFactory.EstablishedDestinations.Count == 1);

        Assert.Equal(1, table.Count);
        Assert.False(listener.IsDisposed);
    }

    [Fact]
    public async Task ShutdownDisposesAllSessionsAndListeners()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

        await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.53"), 53000, 443), s_server, CancellationToken.None);
        await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.54"), 53001, 443), s_server, CancellationToken.None);
        await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.12"), IPAddress.Parse("192.0.2.55"), 53002, 443), s_server, CancellationToken.None);

        await coordinator.DisposeAsync();

        Assert.All(listenerFactory.Listeners, listener => Assert.True(listener.IsDisposed));
    }

    [Fact]
    public async Task DisposeWaitsForInFlightSetupAndReleasesLateListener()
    {
        var listenerFactory = new GatedListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());
        var setup = StartSynAsync(coordinator, MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443));

        await listenerFactory.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposal = coordinator.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);

        listenerFactory.Release();
        Assert.Equal(TcpRedirectOutcome.Blocked, await setup);
        await disposal;
        Assert.Equal(0, table.Count);
        Assert.True(Assert.Single(listenerFactory.Listeners).IsDisposed);
    }

    [Fact]
    public async Task UnrelatedAcceptedConnectionIsClosedBeforeRelaySetup()
    {
        var listenerFactory = new FakeListenerFactory();
        var relayFactory = new FakeRelayFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider());

        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        var listener = Assert.Single(listenerFactory.Listeners);
        var unrelated = new FakeAcceptedConnection(Endpoint.From(IPAddress.Loopback, 1));
        await listener.AcceptChannel.Writer.WriteAsync(unrelated, CancellationToken.None);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(s_destIpv4, 53000)), CancellationToken.None);

        await WaitForAsync(() => relayFactory.EstablishedDestinations.Count == 1);
        Assert.True(unrelated.IsDisposed);
        Assert.Equal(s_destIpv4, Assert.Single(relayFactory.EstablishedDestinations).Address);
    }

    [Fact]
    public async Task AcceptLoopDoesNotRunAwayOnTransientAcceptError()
    {
        // L3: a transient (non-cancel, non-disposed) accept error must not busy-loop the accept
        // path — the previous blanket `continue` could spin flat-out. The loop backs off for a
        // bounded delay between attempts, so the accept call count over a short window stays
        // bounded, and DisposeAsync (which cancels the loop and disposes the listener) completes
        // promptly rather than hanging behind a persistent throw.
        var throwingListener = new ThrowingListener();
        var listenerFactory = new SingleListenerFactory(throwingListener);
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, new TcpRedirectTable(), selfTraffic, new FakeLocalAddressProvider());

        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None));

        // Allow the accept loop to make a handful of backed-off attempts. With a 100 ms back-off
        // between transient failures, this window yields a few calls, and the elapsed time proves
        // real back-pressure was applied rather than a tight busy-loop.
        var elapsed = await MeasureWindowAsync(() => throwingListener.AcceptCount >= 3, TimeSpan.FromMilliseconds(300));
        Assert.True(throwingListener.AcceptCount < 25, $"accept calls over the window should be bounded, saw {throwingListener.AcceptCount}");
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(50), $"the retry should have observed back-off back-pressure, saw {elapsed.TotalMilliseconds:F0}ms");

        // Dispose must terminate the throwing accept loop promptly (cancel + listener dispose).
        await coordinator.DisposeAsync();
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or the window elapses; returns the time spent.</summary>
    private static async Task<TimeSpan> MeasureWindowAsync(Func<bool> condition, TimeSpan window)
    {
        var started = DateTime.UtcNow;
        var deadline = started.Add(window);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
        }
        return DateTime.UtcNow - started;
    }

    private static async Task<TcpRedirectOutcome> StartSynAsync(TcpProxyCoordinator coordinator, CapturedFlowPacket packet) =>
        await coordinator.HandleSynAsync(packet, s_server, CancellationToken.None).ConfigureAwait(false);
}
