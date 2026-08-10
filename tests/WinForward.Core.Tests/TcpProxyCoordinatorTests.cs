using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class TcpProxyCoordinatorTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);
    private static readonly IPAddress s_clientIpv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destIpv4 = IPAddress.Parse("192.0.2.53");
    private static readonly IPAddress s_clientIpv6 = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress s_destIpv6 = IPAddress.Parse("2001:db8::53");

    [Fact]
    public async Task SynClaimIsExactlyOnce()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);
        var packet = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);

        var first = await coordinator.HandleSynAsync(packet, s_server, CancellationToken.None);
        var second = await coordinator.HandleSynAsync(packet, s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, first);
        Assert.Equal(TcpRedirectOutcome.Injected, second);
        Assert.Single(listenerFactory.Listeners);
        // A retransmitted SYN re-injects toward the listener; the flow is still one association.
        Assert.Equal(2, injector.InjectedFrames.Count);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public async Task ReversePacketWithClassifierOrientationResolvesToOriginalFlow()
    {
        // The flow classifier sets Local=SourceAddress, Remote=DestinationAddress. A reverse packet
        // (listener -> client) therefore has Local=listener, Remote=client — the opposite orientation
        // from MakeReversePacket. The reverse resolver must check both Local and Remote against the
        // translated-tuple index or this packet is misrouted.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacketClassifierOrientation(listenerTuple.Address, listenerTuple.Port, s_clientIpv4, 53000);
        var outcome = await coordinator.HandleReverseAsync(reverse, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, outcome);
        Assert.Equal(2, injector.InjectedFrames.Count);
    }

    [Fact]
    public async Task TranslatedTupleAliasCollisionIsRejectedFailClosed()
    {
        var sharedTuple = Endpoint.From(IPAddress.Loopback, 9999);
        var listenerFactory = new FakeListenerFactory(sharedTuple);
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        var first = await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        var second = await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80), s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, first);
        Assert.Equal(TcpRedirectOutcome.Blocked, second);
        Assert.Single(listenerFactory.Listeners, listener => !listener.IsDisposed);
        Assert.Single(injector.InjectedFrames);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public async Task ReversePacketResolvesToOriginalFlow()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacket(listenerTuple.Address, listenerTuple.Port, s_clientIpv4, 53000);
        var outcome = await coordinator.HandleReverseAsync(reverse, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, outcome);
        Assert.Equal(2, injector.InjectedFrames.Count);
    }

    [Fact]
    public async Task CapacityBoundBlocksFailClosed()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable(capacity: 1);
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, capacity: 1);

        var first = await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        var second = await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80), s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, first);
        Assert.Equal(TcpRedirectOutcome.Blocked, second);
        Assert.Single(listenerFactory.Listeners);
        Assert.Single(injector.InjectedFrames);
    }

    [Fact]
    public async Task ListenerAllocationFailureBlocks()
    {
        var listenerFactory = new FakeListenerFactory(throwOnCreate: true);
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

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
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic);

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        var listener = Assert.Single(listenerFactory.Listeners);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(IPAddress.Loopback, 1111)), CancellationToken.None);

        await WaitForAsync(() => table.Count == 0);

        Assert.Equal(0, table.Count);
        Assert.True(listener.IsDisposed);
    }

    [Fact]
    public async Task SelfTrafficRegistryContainsListenerTupleBeforeInjection()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);

        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var key = FlowKey.Create(listenerTuple, listenerTuple, TransportProtocol.Tcp, FlowOriginKind.Host);
        var context = new FlowContext(key, null, null, null, null, listenerTuple.Port);

        Assert.True(selfTraffic.IsOwned(context));
    }

    [Fact]
    public async Task ConcurrentSynBurstOnOneFlowUsesOneListener()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);
        var packet = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        const int count = 32;

        var tasks = Enumerable.Range(0, count)
            .Select(_ => coordinator.HandleSynAsync(packet, s_server, CancellationToken.None).AsTask())
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);

        Assert.All(outcomes, outcome => Assert.Equal(TcpRedirectOutcome.Injected, outcome));
        Assert.Single(listenerFactory.Listeners);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public async Task ConcurrentSynBurstWithAsyncListenerStaysExactlyOnce()
    {
        // Deterministically force the redirect-table exactly-once race: every caller's
        // CreateAsync blocks at a barrier until all N have arrived, guaranteeing every caller
        // passed the TryResolveByOriginal fast path (empty table) before any TryClaim runs. The
        // coordinator's concurrent-loser counter then proves N-1 callers hit the existing-
        // association branch, released their redundant listener, and fell back to re-inject.
        const int count = 8;
        var listenerFactory = new BarrierListenerFactory(count);
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);
        var packet = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);

        var tasks = Enumerable.Range(0, count)
            .Select(_ => coordinator.HandleSynAsync(packet, s_server, CancellationToken.None).AsTask())
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);

        Assert.All(outcomes, outcome => Assert.Equal(TcpRedirectOutcome.Injected, outcome));
        Assert.Equal(1, table.Count);
        // The N-1 losing callers each detected an existing association, released their redundant
        // listener, and re-injected. This is the load-bearing assertion the synchronous fake masked.
        Assert.Equal(count - 1, coordinator.ConcurrentLoserCount);
        Assert.Single(listenerFactory.Listeners, listener => !listener.IsDisposed);
    }

    [Fact]
    public async Task IPv4AndIPv6SynsAllocateMatchingFamilyListener()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv6, s_destIpv6, 53001, 443), s_server, CancellationToken.None);

        Assert.Equal(2, listenerFactory.RequestedFamilies.Count);
        Assert.Equal(AddressFamilyKind.IPv4, listenerFactory.RequestedFamilies[0]);
        Assert.Equal(AddressFamilyKind.IPv6, listenerFactory.RequestedFamilies[1]);
    }

    [Fact]
    public async Task ReverseRewriteRestoresOriginalRemoteEndpoint()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        injector.InjectedFrames.Clear();
        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacket(listenerTuple.Address, listenerTuple.Port, s_clientIpv4, 53000);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleReverseAsync(reverse, CancellationToken.None));

        var injectedFrame = Assert.Single(injector.InjectedFrames).Frame;
        var rewrittenSrcAddress = new IPAddress(injectedFrame.AsSpan(26, 4).ToArray());
        var rewrittenSrcPort = BinaryPrimitives.ReadUInt16BigEndian(injectedFrame.AsSpan(34, 2));
        Assert.Equal(s_destIpv4, rewrittenSrcAddress);
        Assert.Equal(443u, rewrittenSrcPort);
    }

    [Fact]
    public async Task ForwardedFlowReverseInjectsTowardOriginAdapter()
    {
        // A forwarded flow (client behind a VM/remote adapter) has no host process owner and its
        // reverse packets must be sent back to the origin adapter, not up to MSTCP.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        var syn = MakeForwardedSynPacket(IPAddress.Parse("192.0.2.10"), s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        injector.InjectedFrames.Clear();
        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacketClassifierOrientation(listenerTuple.Address, listenerTuple.Port, IPAddress.Parse("192.0.2.10"), 53000);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleReverseAsync(reverse, CancellationToken.None));

        var injected = Assert.Single(injector.InjectedFrames);
        Assert.False(injected.TowardMstcp);
    }

    [Fact]
    public async Task HostFlowReverseInjectsTowardMstcp()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        injector.InjectedFrames.Clear();
        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacketClassifierOrientation(listenerTuple.Address, listenerTuple.Port, s_clientIpv4, 53000);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleReverseAsync(reverse, CancellationToken.None));

        var injected = Assert.Single(injector.InjectedFrames);
        Assert.True(injected.TowardMstcp);
    }

    [Fact]
    public async Task ShutdownDisposesAllSessionsAndListeners()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.53"), 53000, 443), s_server, CancellationToken.None);
        await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.54"), 53001, 443), s_server, CancellationToken.None);
        await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.12"), IPAddress.Parse("192.0.2.55"), 53002, 443), s_server, CancellationToken.None);

        await coordinator.DisposeAsync();

        Assert.All(listenerFactory.Listeners, listener => Assert.True(listener.IsDisposed));
    }

    [Fact]
    public async Task HandleReverseIfApplicableAsyncReturnsNotRelevantForUdp()
    {
        // H1: the numeric-port reverse gateway must not misclassify a UDP datagram whose local and
        // remote ports collide with an active TCP listener port. On UDP it returns NotRelevant so
        // the dispatcher leaves the datagram to normal flow/policy instead of dropping it.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);
        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        var listenerPort = Assert.Single(listenerFactory.Listeners).TranslatedTuple.Port;

        var local = Endpoint.From(s_clientIpv4, listenerPort);
        var remote = Endpoint.From(s_destIpv4, listenerPort);
        var udpKey = FlowKey.Create(local, remote, TransportProtocol.Udp, FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), new FlowContext(udpKey, "dns.exe", null, null, "eth0", listenerPort));

        var outcome = await coordinator.HandleReverseIfApplicableAsync(packet, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.NotRelevant, outcome);
        // Only the setup SYN was injected; the UDP datagram was not routed into reverse injection.
        Assert.Single(injector.InjectedFrames);
    }

    [Fact]
    public async Task Ipv6ReverseFrameWithCollidingPortDoesNotMatchIpv4Association()
    {
        // M5: an IPv6 frame whose port collides with an IPv4 flow's listener port shares the same
        // numeric port value; the reverse gateway must not route it into the IPv4 association, or a
        // rewritten frame would cross address families.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);
        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        var listenerPort = Assert.Single(listenerFactory.Listeners).TranslatedTuple.Port;

        // Fully-IPv6 reverse packet whose destination (Local) port equals the IPv4 listener port.
        var reverse = MakeReversePacket(s_clientIpv6, 53001, s_destIpv6, listenerPort);
        var outcome = await coordinator.HandleReverseIfApplicableAsync(reverse, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.NotRelevant, outcome);
        // Only the setup SYN was injected; the IPv6 reverse was not routed into the IPv4
        // association's reverse injection.
        Assert.Single(injector.InjectedFrames);
    }

    [Fact]
    public async Task ExpiryRemovesIdleAssociations()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        Assert.Equal(1, table.Count);

        var removed = table.RemoveExpired(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1));

        Assert.Equal(1, removed);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public async Task RemoveExpiredAsyncExpiresOnlyRedirectingNotRelayingSessions()
    {
        // M4: a session stuck in Redirecting (never relayed) is expired by the wall-clock sweep,
        // while a session whose relay is live (Relaying) is NOT torn down by remove-expiry — a live
        // connection silent at the packet level must not be force-terminated.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        // Session 1 remains Redirecting (no accepted connection ever relayed).
        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        // Session 2 is promoted to Relaying once its accepted connection establishes a relay.
        await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.54"), 53001, 443), s_server, CancellationToken.None);
        var relayingListener = listenerFactory.Listeners[1];
        await relayingListener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(IPAddress.Loopback, 1111)), CancellationToken.None);
        await WaitForAsync(() => table.Snapshot().Any(a => a.Phase == RelayPhase.Relaying));
        Assert.Equal(2, table.Count);

        var removed = await coordinator.RemoveExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1));

        Assert.Equal(1, removed);
        Assert.Equal(1, table.Count);
        Assert.Equal(RelayPhase.Relaying, Assert.Single(table.Snapshot()).Phase);
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
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, new TcpRedirectTable(), selfTraffic);

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

    [Fact]
    public async Task SelfTrafficRegistryMatchesWildcardBoundSocketByPort()
    {
        // The UDP relay transport binds 0.0.0.0 but emits packets whose source IP is chosen by
        // routing (e.g. 192.168.77.2). The registry must treat a 0.0.0.0:port registration as
        // matching any observed source IP on the same port + remote, or relay traffic recurses.
        var registry = new SelfTrafficRegistry();
        var anyLocal = Endpoint.From(IPAddress.Any, 40000);
        var remote = Endpoint.From(IPAddress.Parse("192.168.77.2"), 59391);
        var token = registry.Register(new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Udp, anyLocal, remote));

        var observedLocal = Endpoint.From(IPAddress.Parse("192.168.77.2"), 40000);
        var key = FlowKey.Create(observedLocal, remote, TransportProtocol.Udp, FlowOriginKind.Host);
        var context = new FlowContext(key, null, null, null, null, remote.Port);
        Assert.True(registry.IsOwned(context));

        token.Dispose();
        Assert.False(registry.IsOwned(context));
    }

    [Fact]
    public void SelfTrafficUpstreamTcpTupleIsOwnedWhenObservedAsHostEphemeralToProxy()
    {
        // Regression for the CRITICAL loop-prevention defect: the TCP relay registered
        // (proxy:proxy) as both endpoints, which can never match the observed upstream SYN
        // (host:ephemeral -> proxy:socks). Under a catch-all proxy rule the SOCKS5 control
        // connection would be recursively re-proxied. The fix registers the bound local endpoint
        // (Any:port) plus the proxy endpoint; the wildcard matcher must own the observed tuple.
        var registry = new SelfTrafficRegistry();
        var boundLocal = Endpoint.From(IPAddress.Any, 40000);
        var proxy = Endpoint.From(IPAddress.Parse("192.168.77.2"), 1080);
        using var token = registry.Register(new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Tcp, boundLocal, proxy));

        var observedLocal = Endpoint.From(IPAddress.Parse("192.168.77.1"), 40000);
        var observedKey = FlowKey.Create(observedLocal, proxy, TransportProtocol.Tcp, FlowOriginKind.Host);
        var observed = new FlowContext(observedKey, null, null, null, null, proxy.Port);
        Assert.True(registry.IsOwned(observed));

        // The proxy's response (reverse direction) is owned too.
        var reverse = new FlowContext(observedKey.Reverse(), null, null, null, null, proxy.Port);
        Assert.True(registry.IsOwned(reverse));

        // An unrelated application sharing the proxy endpoint but using its own source port is not
        // exempted: loop prevention is exact to WinForward-owned sockets, never a broad exemption.
        var otherLocal = Endpoint.From(IPAddress.Parse("192.168.77.1"), 41001);
        var other = new FlowContext(FlowKey.Create(otherLocal, proxy, TransportProtocol.Tcp, FlowOriginKind.Host), null, null, null, null, proxy.Port);
        Assert.False(registry.IsOwned(other));
    }

    private static CapturedFlowPacket MakeSynPacket(IPAddress client, IPAddress destination, ushort clientPort, ushort destinationPort)
    {
        var frame = client.AddressFamily == AddressFamily.InterNetwork
            ? BuildIpv4TcpSyn(client, destination, clientPort, destinationPort)
            : BuildIpv6TcpSyn(client, destination, clientPort, destinationPort);
        var local = Endpoint.From(client, clientPort);
        var remote = Endpoint.From(destination, destinationPort);
        var key = FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Host);
        var context = new FlowContext(key, "app.exe", null, null, "eth0", destinationPort);
        var lease = new PacketLease(frame);
        return new CapturedFlowPacket(lease, context, new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 0x1234));
    }

    private static CapturedFlowPacket MakeForwardedSynPacket(IPAddress client, IPAddress destination, ushort clientPort, ushort destinationPort)
    {
        var frame = client.AddressFamily == AddressFamily.InterNetwork
            ? BuildIpv4TcpSyn(client, destination, clientPort, destinationPort)
            : BuildIpv6TcpSyn(client, destination, clientPort, destinationPort);
        var local = Endpoint.From(client, clientPort);
        var remote = Endpoint.From(destination, destinationPort);
        var adapter = new AdapterContext("veth-1", "vEthernet 1", 7);
        var key = FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Forwarded, adapter);
        var context = new FlowContext(key, null, null, "veth-1", "vEthernet 1", destinationPort);
        var lease = new PacketLease(frame);
        return new CapturedFlowPacket(lease, context, new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnReceive, 0x1234));
    }

    private static CapturedFlowPacket MakeReversePacket(IPAddress source, ushort sourcePort, IPAddress destination, ushort destinationPort)
    {
        var frame = source.AddressFamily == AddressFamily.InterNetwork
            ? BuildIpv4TcpSyn(source, destination, sourcePort, destinationPort)
            : BuildIpv6TcpSyn(source, destination, sourcePort, destinationPort);
        var remote = Endpoint.From(source, sourcePort);
        var local = Endpoint.From(destination, destinationPort);
        var key = FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Host);
        var context = new FlowContext(key, "app.exe", null, null, "eth0", destinationPort);
        var lease = new PacketLease(frame);
        return new CapturedFlowPacket(lease, context, new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnReceive, 0x1234));
    }

    private static CapturedFlowPacket MakeReversePacketClassifierOrientation(IPAddress source, ushort sourcePort, IPAddress destination, ushort destinationPort)
    {
        var frame = source.AddressFamily == AddressFamily.InterNetwork
            ? BuildIpv4TcpSyn(source, destination, sourcePort, destinationPort)
            : BuildIpv6TcpSyn(source, destination, sourcePort, destinationPort);
        var local = Endpoint.From(source, sourcePort);
        var remote = Endpoint.From(destination, destinationPort);
        var key = FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Host);
        var context = new FlowContext(key, "app.exe", null, null, "eth0", destinationPort);
        var lease = new PacketLease(frame);
        return new CapturedFlowPacket(lease, context, new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnReceive, 0x1234));
    }

    private static byte[] BuildIpv4TcpSyn(IPAddress source, IPAddress destination, ushort sourcePort, ushort destinationPort)
    {
        const int tcpHeaderLength = 20;
        const int totalLength = 20 + tcpHeaderLength;
        var frame = new byte[14 + totalLength];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), (ushort)totalLength);
        frame[23] = 6;
        source.TryWriteBytes(frame.AsSpan(26, 4), out _);
        destination.TryWriteBytes(frame.AsSpan(30, 4), out _);

        const int tcp = 34;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 2, 2), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 4, 4), 0x00000001);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 8, 4), 0x00000000);
        frame[tcp + 12] = 0x50;
        frame[tcp + 13] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 14, 2), 0xffff);

        SetIpv4HeaderChecksum(frame);
        SetIpv4TcpChecksum(frame, tcp, tcpHeaderLength);
        return frame;
    }

    private static byte[] BuildIpv6TcpSyn(IPAddress source, IPAddress destination, ushort sourcePort, ushort destinationPort)
    {
        const int tcpLength = 20;
        var frame = new byte[14 + 40 + tcpLength];
        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), (ushort)tcpLength);
        frame[20] = 6;
        source.TryWriteBytes(frame.AsSpan(22, 16), out _);
        destination.TryWriteBytes(frame.AsSpan(38, 16), out _);

        const int tcp = 54;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 2, 2), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 4, 4), 0x00000002);
        frame[tcp + 12] = 0x50;
        frame[tcp + 13] = 0x02;

        SetIpv6TcpChecksum(frame, tcp, tcpLength);
        return frame;
    }

    private static void SetIpv4HeaderChecksum(byte[] frame)
    {
        const int headerLength = 20;
        frame[24] = 0;
        frame[25] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(24, 2), PacketChecksums.InternetChecksum(frame.AsSpan(14, headerLength)));
    }

    private static void SetIpv4TcpChecksum(byte[] frame, int tcp, int tcpLength)
    {
        frame[tcp + 16] = 0;
        frame[tcp + 17] = 0;
        var sum = Sum(frame.AsSpan(26, 4)) + Sum(frame.AsSpan(30, 4)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcp, tcpLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 16, 2), Finish(sum));
    }

    private static void SetIpv6TcpChecksum(byte[] frame, int tcp, int tcpLength)
    {
        frame[tcp + 16] = 0;
        frame[tcp + 17] = 0;
        var sum = Sum(frame.AsSpan(22, 16)) + Sum(frame.AsSpan(38, 16)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcp, tcpLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 16, 2), Finish(sum));
    }

    private static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var index = 0;
        for (; index + 1 < data.Length; index += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
        if (index < data.Length) sum += (uint)data[index] << 8;
        return sum;
    }

    private static ushort Finish(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
        }
    }

    private sealed class FakeListenerFactory : ITcpRedirectListenerFactory
    {
        private readonly Endpoint? _fixedTuple;
        private readonly bool _throwOnCreate;
        private int _nextPort = 40000;

        public FakeListenerFactory(Endpoint? fixedTuple = null, bool throwOnCreate = false)
        {
            _fixedTuple = fixedTuple;
            _throwOnCreate = throwOnCreate;
        }

        public List<FakeListener> Listeners { get; } = [];
        public List<AddressFamilyKind> RequestedFamilies { get; } = [];

        public ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken cancellationToken)
        {
            if (_throwOnCreate) throw new IOException("listener allocation failed");
            RequestedFamilies.Add(addressFamily);
            var loopback = addressFamily == AddressFamilyKind.IPv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            var tuple = _fixedTuple ?? Endpoint.From(loopback, checked((ushort)Interlocked.Increment(ref _nextPort)));
            var listener = new FakeListener(tuple);
            lock (Listeners) Listeners.Add(listener);
            return ValueTask.FromResult<ITcpRedirectListener>(listener);
        }
    }

    private sealed class BarrierListenerFactory(int participantCount) : ITcpRedirectListenerFactory
    {
        /* Gates CreateAsync so the first participantCount-1 callers block until the last one
         * arrives; all are then released together. This guarantees every caller passed the
         * coordinator's pre-claim TryResolveByOriginal fast path (empty table) before any
         * TryClaim runs, deterministically forcing the redirect-table exactly-once race a
         * synchronous fake masks. */
        private readonly TaskCompletionSource _gate = new();
        private int _arrived;
        private int _nextPort = 40000;

        public List<FakeListener> Listeners { get; } = [];

        public async ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken cancellationToken)
        {
            // The last caller to arrive opens the gate; the rest were already awaiting it.
            if (Interlocked.Increment(ref _arrived) == participantCount) _gate.TrySetResult();
            await using var registration = cancellationToken.Register(() => _gate.TrySetCanceled(cancellationToken));
            await _gate.Task.ConfigureAwait(false);
            await Task.Yield();
            var loopback = addressFamily == AddressFamilyKind.IPv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            var listener = new FakeListener(Endpoint.From(loopback, checked((ushort)Interlocked.Increment(ref _nextPort))));
            lock (Listeners) Listeners.Add(listener);
            return listener;
        }
    }

    private sealed class SingleListenerFactory(ITcpRedirectListener listener) : ITcpRedirectListenerFactory
    {
        public ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken _) => ValueTask.FromResult(listener);
    }

    private sealed class ThrowingListener : ITcpRedirectListener
    {
        private int _acceptCount;
        public Endpoint TranslatedTuple => Endpoint.From(IPAddress.Loopback, 40000);
        public int AcceptCount => Volatile.Read(ref _acceptCount);
        public bool IsDisposed { get; private set; }

        public ValueTask<ITcpAcceptedConnection> AcceptAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _acceptCount);
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("transient accept error");
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeListener(Endpoint translatedTuple) : ITcpRedirectListener
    {
        public Endpoint TranslatedTuple { get; } = translatedTuple;
        public Channel<FakeAcceptedConnection> AcceptChannel { get; } = Channel.CreateUnbounded<FakeAcceptedConnection>();
        public bool IsDisposed { get; private set; }

        public async ValueTask<ITcpAcceptedConnection> AcceptAsync(CancellationToken cancellationToken)
        {
            return await AcceptChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAcceptedConnection(Endpoint remoteEndPoint) : ITcpAcceptedConnection
    {
        public Endpoint RemoteEndPoint { get; } = remoteEndPoint;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRelayFactory(bool throwOnEstablish = false) : ITcpProxyRelayFactory
    {
        public List<Endpoint> EstablishedDestinations { get; } = [];

        public ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, ITcpAcceptedConnection acceptedConnection, Socks5Server server, CancellationToken cancellationToken)
        {
            if (throwOnEstablish) throw new IOException("relay setup failed");
            lock (EstablishedDestinations) EstablishedDestinations.Add(originalDestination);
            return ValueTask.FromResult<ITcpRelay>(new FakeRelay());
        }
    }

    private sealed class FakeRelay : ITcpRelay
    {
        public Task Completion { get; } = new TaskCompletionSource<bool>().Task;
        public bool IsDisposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeInjector : ITcpRedirectInjector
    {
        public List<(byte[] Frame, bool TowardMstcp, nint AdapterHandle)> InjectedFrames { get; } = [];

        public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken)
        {
            lock (InjectedFrames) InjectedFrames.Add((rewrittenFrame.ToArray(), towardMstcp, adapterHandle));
            return ValueTask.CompletedTask;
        }
    }
}
