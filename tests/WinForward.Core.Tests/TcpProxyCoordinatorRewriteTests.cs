using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

public sealed class TcpProxyCoordinatorRewriteTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);
    private static readonly IPAddress s_clientIpv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destIpv4 = IPAddress.Parse("192.0.2.53");
    private static readonly IPAddress s_clientIpv6 = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress s_destIpv6 = IPAddress.Parse("2001:db8::53");

    [Fact]
    public void NonExactReverseProbeDoesNotRefreshActivity()
    {
        var table = new TcpRedirectTable();
        var now = DateTimeOffset.UtcNow;
        var key = FlowKey.Create(Endpoint.From(s_clientIpv4, 53000), Endpoint.From(s_destIpv4, 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var adapter = new AdapterContext("eth0", "Ethernet", 1);
        Assert.True(table.TryClaim(key, key.Remote, adapter, (nint)0x1234, Endpoint.From(IPAddress.Loopback, 42000), null, now, out var claimed));
        Assert.NotNull(claimed);

        Assert.False(table.TryResolveByReverse(Endpoint.From(s_clientIpv4, 42000), Endpoint.From(IPAddress.Parse("192.0.2.99"), 53000), now.AddMinutes(1), out _));
        Assert.Equal(now, claimed!.LastActivityUtc);
    }

    [Fact]
    public async Task ReversePacketWithClassifierOrientationResolvesToOriginalFlow()
    {
        // The flow classifier sets Local=SourceAddress and Remote=DestinationAddress. A wildcard
        // listener replies from the route-selected client address, so the pre-rewrite SYN-ACK is
        // client:proxy-port -> server:original-client-port.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerTuple.Port, s_destIpv4, 53000);
        var outcome = await coordinator.HandleReverseAsync(reverse, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, outcome);
        Assert.Equal(2, injector.InjectedFrames.Count);
    }

    [Fact]
    public async Task ReversePacketResolvesToOriginalFlow()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerTuple.Port, s_destIpv4, 53000);
        var outcome = await coordinator.HandleReverseAsync(reverse, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, outcome);
        Assert.Equal(2, injector.InjectedFrames.Count);
    }

    [Fact]
    public async Task IPv4AndIPv6SynsAllocateMatchingFamilyListener()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

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
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        injector.InjectedFrames.Clear();
        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerTuple.Port, s_destIpv4, 53000, mutateFrame: f => { f[0] = 0x11; f[6] = 0x22; });
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleReverseAsync(reverse, CancellationToken.None));

        var injectedFrame = Assert.Single(injector.InjectedFrames).Frame;
        var rewrittenSrcAddress = new IPAddress(injectedFrame.AsSpan(26, 4).ToArray());
        var rewrittenSrcPort = BinaryPrimitives.ReadUInt16BigEndian(injectedFrame.AsSpan(34, 2));
        Assert.Equal(s_destIpv4, rewrittenSrcAddress);
        Assert.Equal(443u, rewrittenSrcPort);
        // Host shape swaps MACs so the frame looks like it arrived from the network.
        Assert.Equal(0x22, injectedFrame[0]);
        Assert.Equal(0x11, injectedFrame[6]);
    }

    [Fact]
    public async Task ForwardedFlowSynRewritesTowardAdapterLocalListener()
    {
        // DNAT-to-local: a forwarded SYN keeps the client's source tuple and only its destination
        // moves to an adapter-local address so the local stack accepts it for the redirect listener.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var forwardLocal = IPAddress.Parse("192.0.2.1");
        var localAddresses = new FakeLocalAddressProvider(forwardLocal);
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, localAddresses);

        var client = IPAddress.Parse("192.0.2.10");
        var syn = MakeForwardedSynPacket(client, s_destIpv4, 53000, 443, f => { f[0] = 0xAA; f[6] = 0xBB; });
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        Assert.Equal(1, localAddresses.Calls);
        var injected = Assert.Single(injector.InjectedFrames);
        Assert.True(injected.TowardMstcp);
        Assert.Equal((nint)0x1234, injected.AdapterHandle);
        var frame = injected.Frame;
        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        Assert.Equal(client, new IPAddress(frame.AsSpan(26, 4).ToArray()));
        Assert.Equal(53000u, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(34, 2)));
        Assert.Equal(forwardLocal, new IPAddress(frame.AsSpan(30, 4).ToArray()));
        Assert.Equal((uint)listenerTuple.Port, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(36, 2)));
        // Arrival MACs already address this host and are kept (no swap).
        Assert.Equal(0xAA, frame[0]);
        Assert.Equal(0xBB, frame[6]);
    }

    [Fact]
    public async Task ForwardedFlowWithoutLocalAddressFailsClosed()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

        var syn = MakeForwardedSynPacket(IPAddress.Parse("192.0.2.10"), s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Blocked, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        Assert.Empty(injector.InjectedFrames);
        Assert.Equal(0, table.Count);
        Assert.True(Assert.Single(listenerFactory.Listeners).IsDisposed);
    }

    [Fact]
    public async Task ForwardedFlowAcceptsClientTuplePeer()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var relayFactory = new FakeRelayFactory();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider(IPAddress.Parse("192.0.2.1")));

        var client = IPAddress.Parse("192.0.2.10");
        var syn = MakeForwardedSynPacket(client, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        // The DNAT shape preserves the client's tuple, so the accepted peer is the client itself.
        var listener = Assert.Single(listenerFactory.Listeners);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(client, 53000)));
        await WaitForAsync(() => relayFactory.EstablishedDestinations.Count == 1);
        Assert.Equal(Endpoint.From(s_destIpv4, 443), relayFactory.EstablishedDestinations[0]);
    }

    [Fact]
    public async Task ForwardedFlowReverseInjectsTowardOriginAdapter()
    {
        // A forwarded flow's reverse leg (listener -> client) is rewritten back to the original
        // server tuple and sent to the origin adapter without a MAC swap.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var forwardLocal = IPAddress.Parse("192.0.2.1");
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider(forwardLocal));

        var client = IPAddress.Parse("192.0.2.10");
        var syn = MakeForwardedSynPacket(client, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        injector.InjectedFrames.Clear();
        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacketClassifierOrientation(forwardLocal, listenerTuple.Port, client, 53000, (nint)0x5678, f => { f[0] = 0xCC; f[6] = 0xDD; });
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleReverseAsync(reverse, CancellationToken.None));

        var injected = Assert.Single(injector.InjectedFrames);
        Assert.False(injected.TowardMstcp);
        Assert.Equal((nint)0x1234, injected.AdapterHandle);
        var frame = injected.Frame;
        Assert.Equal(s_destIpv4, new IPAddress(frame.AsSpan(26, 4).ToArray()));
        Assert.Equal(443u, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(34, 2)));
        Assert.Equal(client, new IPAddress(frame.AsSpan(30, 4).ToArray()));
        Assert.Equal(53000u, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(36, 2)));
        Assert.Equal(0xCC, frame[0]);
        Assert.Equal(0xDD, frame[6]);
    }

    [Fact]
    public async Task SynRewriteParseFailureLeavesFrameByteIdentical()
    {
        // Parse-before-write invariant of the in-place rewrite: a frame that fails the rewrite's
        // parse stage must be left byte-identical (never a half-rewritten form), and the flow
        // fails closed with the listener, association, and session all released.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());
        var packet = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.True(MemoryMarshal.TryGetArray(packet.Lease.Frame, out var segment));
        // An ARP ethertype cannot pass the rewrite's parse stage, so the rewrite fails before
        // any field write.
        BinaryPrimitives.WriteUInt16BigEndian(segment.Array.AsSpan(segment.Offset + 12, 2), 0x0806);
        var expected = packet.Lease.Frame.ToArray();

        var outcome = await coordinator.HandleSynAsync(packet, s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Blocked, outcome);
        Assert.Equal(0, table.Count);
        Assert.Empty(injector.InjectedFrames);
        Assert.True(Assert.Single(listenerFactory.Listeners).IsDisposed);
        Assert.Equal(expected, packet.Lease.Frame.ToArray());
    }

    [Fact]
    public async Task HostFlowReverseInjectsTowardMstcp()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        injector.InjectedFrames.Clear();
        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerTuple.Port, s_destIpv4, 53000);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleReverseAsync(reverse, CancellationToken.None));

        var injected = Assert.Single(injector.InjectedFrames);
        Assert.True(injected.TowardMstcp);
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
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());
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
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());
        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        var listenerPort = Assert.Single(listenerFactory.Listeners).TranslatedTuple.Port;

        // Fully-IPv6 reverse tuple that uses the same listener port but has no IPv4 association.
        var reverse = MakeReversePacketClassifierOrientation(s_clientIpv6, listenerPort, s_destIpv6, 53001);
        var outcome = await coordinator.HandleReverseIfApplicableAsync(reverse, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.NotRelevant, outcome);
        // Only the setup SYN was injected; the IPv6 reverse was not routed into the IPv4
        // association's reverse injection.
        Assert.Single(injector.InjectedFrames);
    }

    [Fact]
    public async Task SameFamilyUnrelatedReverseTupleDoesNotMatchAssociation()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());
        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        var listenerPort = Assert.Single(listenerFactory.Listeners).TranslatedTuple.Port;

        var unrelated = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerPort, IPAddress.Parse("192.0.2.99"), 53000);
        var outcome = await coordinator.HandleReverseIfApplicableAsync(unrelated, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.NotRelevant, outcome);
        Assert.Single(injector.InjectedFrames);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public async Task SynWithPayloadIsBlockedWithoutClaim()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

        var packet = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443, [0x01, 0x02, 0x03]);
        var outcome = await coordinator.HandlePacketAsync(packet, s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Blocked, outcome);
        Assert.Equal(0, table.Count);
        Assert.Empty(listenerFactory.Listeners);
        Assert.Empty(injector.InjectedFrames);
    }
}
