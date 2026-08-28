using System.Net;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class FlowDispatcherExecutorTests
{
    // ---- Flow dispatcher: process attribution + non-flow + proxy fail-closed ----

    [Fact]
    public async Task DispatcherAttributionsHostFlowsButNotForwarded()
    {
        var config = CreateConfig(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }), FlowAction.Pass);
        var attributor = new FakeAttributor("dns.exe");
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, attributor);

        var hostKey = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var hostPacket = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), FlowContext(hostKey));
        await dispatcher.DispatchAsync(hostPacket, CancellationToken.None);
        Assert.Equal(1, attributor.Calls);
        Assert.Equal(1, executor.PassCount);

        var forwardedKey = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53001), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Forwarded);
        var forwardedPacket = new CapturedFlowPacket(new PacketLease(new byte[] { 2 }), FlowContext(forwardedKey));
        await dispatcher.DispatchAsync(forwardedPacket, CancellationToken.None);
        Assert.Equal(1, attributor.Calls);
        Assert.Equal(2, executor.PassCount);
    }

    [Fact]
    public async Task DispatcherSkipsAttributionWhenPolicyHasNoProcessSelectors()
    {
        var config = CreateConfig(new RuleMatcher(Protocols: new HashSet<TransportProtocol> { TransportProtocol.Udp }), FlowAction.Pass);
        var attributor = new FakeAttributor("dns.exe");
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, attributor);
        var key = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);

        await dispatcher.DispatchAsync(new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), FlowContext(key)), CancellationToken.None);

        Assert.Equal(0, attributor.Calls);
        Assert.Equal(1, executor.PassCount);
    }

    [Fact]
    public async Task DispatcherSeparatesForwardedAdaptersFromHostCatchAllPolicy()
    {
        var server = new Socks5Server("primary", "127.0.0.1", 1080, null, null);
        var servers = new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase) { [server.Name] = server };
        var config = new ValidatedConfiguration(servers, new PolicySnapshot(
        [
            new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Proxy, 0, server.Name)),
            new(new RuleMatcher(), new FlowDecision(FlowAction.Proxy, 1, server.Name))
        ], FlowAction.Block));
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor);

        var forwardedB = FlowPacket(53000, FlowOriginKind.Forwarded, "id-b", "vEthernet B");
        var forwardedA = FlowPacket(53001, FlowOriginKind.Forwarded, "id-a", "vEthernet A");
        var host = FlowPacket(53002, FlowOriginKind.Host, "id-b", "Ethernet");

        await dispatcher.DispatchAsync(forwardedB, CancellationToken.None);
        await dispatcher.DispatchAsync(forwardedA, CancellationToken.None);
        await dispatcher.DispatchAsync(host, CancellationToken.None);

        Assert.Equal(PacketDisposition.Pass, forwardedB.Lease.Disposition);
        Assert.Equal(PacketDisposition.ProxyConsumed, forwardedA.Lease.Disposition);
        Assert.Equal(PacketDisposition.ProxyConsumed, host.Lease.Disposition);
        Assert.Equal(1, executor.PassCount);
        Assert.Equal(2, executor.ProxyCount);
        Assert.Equal(0, executor.BlockCount);
    }

    [Fact]
    public async Task DispatcherReusesForwardedDecisionAcrossAdapterObservations()
    {
        var config = CreateConfig(
            new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }),
            FlowAction.Block,
            ruleAction: FlowAction.Block);
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor);
        var first = FlowPacket(53000, FlowOriginKind.Forwarded, "id-a", "vEthernet A");
        var second = FlowPacket(53000, FlowOriginKind.Forwarded, "id-b", "vEthernet B");

        await dispatcher.DispatchAsync(first, CancellationToken.None);
        await dispatcher.DispatchAsync(second, CancellationToken.None);

        Assert.Equal(PacketDisposition.Block, first.Lease.Disposition);
        Assert.Equal(PacketDisposition.Block, second.Lease.Disposition);
        Assert.Equal(2, executor.BlockCount);
        Assert.Equal(0, executor.PassCount);
    }

    [Fact]
    public async Task DispatcherCachesForwardedImplicitPassAcrossOriginsAndFailsClosedAtCapacity()
    {
        var config = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Block));
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, flowCapacity: 1);
        var first = FlowPacket(53000, FlowOriginKind.Forwarded, "id-b", "vEthernet B");
        var observedAsHost = FlowPacket(53000, FlowOriginKind.Host, "id-a", "Ethernet A");
        var second = FlowPacket(53001, FlowOriginKind.Forwarded, "id-b", "vEthernet B");

        await dispatcher.DispatchAsync(first, CancellationToken.None);
        await dispatcher.DispatchAsync(observedAsHost, CancellationToken.None);
        await dispatcher.DispatchAsync(second, CancellationToken.None);

        Assert.Equal(PacketDisposition.Pass, first.Lease.Disposition);
        Assert.Equal(PacketDisposition.Pass, observedAsHost.Lease.Disposition);
        Assert.Equal(PacketDisposition.Block, second.Lease.Disposition);
        Assert.Equal(2, executor.PassCount);
        Assert.Equal(1, executor.BlockCount);
    }

    [Fact]
    public async Task DispatcherFallsBackWhenHostAttributionIsUnknown()
    {
        var config = CreateConfig(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }), FlowAction.Pass, ruleAction: FlowAction.Block);
        var attributor = new FakeAttributor(null);
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, attributor);
        var key = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), FlowContext(key));

        await dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.Equal(1, attributor.Calls);
        Assert.Equal(1, executor.PassCount);
        Assert.Equal(0, executor.BlockCount);
        Assert.Equal(PacketDisposition.Pass, packet.Lease.Disposition);
    }

    [Fact]
    public async Task DispatcherForwardedNonFlowAlwaysPassesRegardlessOfRules()
    {
        var config = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot(
            [
                new(new RuleMatcher(), new FlowDecision(FlowAction.Block, 0, null)),
                new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Proxy, 1, "proxy"))
            ], FlowAction.Block));
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor);
        var adapterA = new WindowsAdapter("id-a", "vEthernet A", "a", 1, 1);
        var adapterB = new WindowsAdapter("id-b", "vEthernet B", "b", 2, 1);
        var selected = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), PacketFlowClassifier.ClassifyNonFlow(adapterA, isOnSend: false));
        var unselected = new CapturedFlowPacket(new PacketLease(new byte[] { 2 }), PacketFlowClassifier.ClassifyNonFlow(adapterB, isOnSend: false));

        await dispatcher.DispatchNonFlowAsync(selected, CancellationToken.None);
        await dispatcher.DispatchNonFlowAsync(unselected, CancellationToken.None);

        Assert.Equal(0, executor.BlockCount);
        Assert.Equal(2, executor.PassCount);
        Assert.Equal(PacketDisposition.Pass, selected.Lease.Disposition);
        Assert.Equal(PacketDisposition.Pass, unselected.Lease.Disposition);
    }

    [Fact]
    public async Task DispatcherHostNonFlowAlwaysPassesRegardlessOfFallbackAndRules()
    {
        var config = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot(
            [
                new(new RuleMatcher(Protocols: new HashSet<TransportProtocol> { TransportProtocol.Tcp }), new FlowDecision(FlowAction.Block, 0, null)),
                new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Proxy, 1, "proxy")),
                new(new RuleMatcher(), new FlowDecision(FlowAction.Block, 2, null))
            ], FlowAction.Block));
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor);
        var adapter = new WindowsAdapter("id-a", "Ethernet", "a", 1, 1);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), PacketFlowClassifier.ClassifyNonFlow(adapter, isOnSend: true));

        await dispatcher.DispatchNonFlowAsync(packet, CancellationToken.None);

        Assert.Equal(1, executor.PassCount);
        Assert.Equal(0, executor.BlockCount);
        Assert.Equal(PacketDisposition.Pass, packet.Lease.Disposition);
    }

    [Fact]
    public async Task DispatcherProxyDecisionFailsClosedWithoutPassOrReinjection()
    {
        var config = CreateConfig(new RuleMatcher(), FlowAction.Block, ruleAction: FlowAction.Proxy, server: "proxy");
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor);
        var key = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), FlowContext(key));

        await dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.Equal(1, executor.ProxyCount);
        Assert.Equal(0, executor.PassCount);
        Assert.Equal(PacketDisposition.ProxyConsumed, packet.Lease.Disposition);
    }

    [Fact]
    public async Task DispatcherCompletesLeaseExactlyOnceWhenExecutorThrows()
    {
        var config = CreateConfig(new RuleMatcher(), FlowAction.Pass);
        var executor = new ThrowingPassExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor);
        var key = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), FlowContext(key));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await dispatcher.DispatchAsync(packet, CancellationToken.None));

        // The lease is completed exactly once even though the executor threw afterwards.
        Assert.Equal(PacketDisposition.Pass, packet.Lease.Disposition);
        Assert.False(packet.Lease.TryComplete(PacketDisposition.Block));
    }

    [Fact]
    public async Task DispatcherReversePacketReusesDecisionWithoutReattribution()
    {
        var config = CreateConfig(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }), FlowAction.Pass);
        var attributor = new FakeAttributor("dns.exe");
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, attributor);
        var hostKey = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var responseKey = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), TransportProtocol.Tcp, FlowOriginKind.Forwarded);

        var first = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), FlowContext(hostKey));
        await dispatcher.DispatchAsync(first, CancellationToken.None);
        var reverse = new CapturedFlowPacket(new PacketLease(new byte[] { 2 }), FlowContext(responseKey));
        await dispatcher.DispatchAsync(reverse, CancellationToken.None);

        Assert.Equal(1, attributor.Calls);
        Assert.Equal(PacketDisposition.Pass, first.Lease.Disposition);
        Assert.Equal(PacketDisposition.Pass, reverse.Lease.Disposition);
    }

    // ---- Packet action executor: pass direction mapping, block, proxy fail-closed ----

    [Fact]
    public void PooledLeaseReturnsFrameExactlyOnce()
    {
        var returns = 0;
        var lease = new PacketLease(new byte[] { 1, 2, 3 }, _ => returns++);

        Assert.True(lease.TryComplete(PacketDisposition.Pass));
        Assert.False(lease.TryComplete(PacketDisposition.Block));
        lease.Dispose();

        Assert.Equal(1, returns);
        Assert.Equal(PacketDisposition.Pass, lease.Disposition);
    }

    [Fact]
    public void PooledLeaseDisposeIsTheSoleCompletionPath()
    {
        var returns = 0;
        var lease = new PacketLease(new byte[] { 1 }, _ => returns++);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(1, returns);
        Assert.Equal(PacketDisposition.Block, lease.Disposition);
    }

    [Fact]
    public void PlainLeaseCompletionFiresNoReturnCallback()
    {
        var lease = new PacketLease(new byte[] { 1, 2, 3 });

        Assert.True(lease.TryComplete(PacketDisposition.Pass));

        Assert.Equal(PacketDisposition.Pass, lease.Disposition);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ProcessorCopiesFrameBytesThroughPooledLease()
    {
        var reinjector = new FakeReinjector();
        var dispatcher = new FlowDispatcher(CreateConfig(new RuleMatcher(), FlowAction.Pass), new FakeGuard(), new NdisPacketActionExecutor(reinjector));
        var adapter = new WindowsAdapter("id-a", "Ethernet", "internal-a", 7, 1);
        using var buffer = new NdisPacketBuffer();
        var frame = FrameBuilders.CreateIpv4TcpFrame();
        buffer.SetFrame(frame, NdisApiAbi.PacketFlagOnSend, (nint)7);

        await new CapturePacketProcessor(dispatcher).ProcessAsync(
            NdisCapturedPacket.FromCapture(buffer, (nint)7),
            adapter,
            CancellationToken.None);

        // The pooled lease exposes exactly the captured frame: byte-identical content and no
        // trailing pool slack beyond the actual frame length.
        Assert.NotNull(reinjector.LastFrame);
        Assert.Equal(frame.Length, reinjector.LastFrame!.Length);
        Assert.Equal(frame, reinjector.LastFrame);
    }

    [Fact]
    public async Task ExecutorReinjectsOnSendTowardAdapterAndOnReceiveTowardMstcp()
    {
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);

        var send = new CapturedFlowPacket(new PacketLease(new byte[] { 1, 2, 3 }), FlowContext(FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 1), Endpoint.From(IPAddress.Parse("192.0.2.53"), 2), TransportProtocol.Tcp, FlowOriginKind.Host)), new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 7));
        await executor.PassAsync(send, CancellationToken.None);
        Assert.Equal(1, reinjector.ToAdapterCount);
        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal((nint)7, reinjector.LastAdapterHandle);
        Assert.Equal(new byte[] { 1, 2, 3 }, reinjector.LastFrame!);

        var receive = new CapturedFlowPacket(new PacketLease(new byte[] { 4, 5 }), FlowContext(FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.53"), 2), Endpoint.From(IPAddress.Parse("192.0.2.10"), 1), TransportProtocol.Tcp, FlowOriginKind.Forwarded)), new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnReceive, 8));
        await executor.PassAsync(receive, CancellationToken.None);
        Assert.Equal(1, reinjector.ToMstcpCount);
        Assert.Equal((nint)8, reinjector.LastAdapterHandle);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ProcessorPreservesCapturedNdisFlagsThroughPassReinjection()
    {
        var reinjector = new FakeReinjector();
        var dispatcher = new FlowDispatcher(CreateConfig(new RuleMatcher(), FlowAction.Pass), new FakeGuard(), new NdisPacketActionExecutor(reinjector));
        var adapter = new WindowsAdapter("id-a", "Ethernet", "internal-a", 7, 1);
        using var buffer = new NdisPacketBuffer();
        buffer.SetFrame(FrameBuilders.CreateIpv4TcpFrame(), NdisApiAbi.PacketFlagOnSend, (nint)7, flags: 0x4000_0021);

        await new CapturePacketProcessor(dispatcher).ProcessAsync(
            NdisCapturedPacket.FromCapture(buffer, (nint)7),
            adapter,
            CancellationToken.None);

        Assert.Equal(1, reinjector.ToAdapterCount);
        Assert.Equal(0x4000_0021u, reinjector.LastFlags);
    }

    [Fact]
    public async Task ExecutorBlockAndProxyNeverReinject()
    {
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), FlowContext(FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 1), Endpoint.From(IPAddress.Parse("192.0.2.53"), 2), TransportProtocol.Tcp, FlowOriginKind.Host)), new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 7));

        await executor.BlockAsync(packet, CancellationToken.None);
        await executor.ProxyAsync(packet, new Socks5Server("p", "127.0.0.1", 1080, null, null), CancellationToken.None);

        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.Equal(0, reinjector.ToMstcpCount);
    }

    [Fact]
    public async Task ExecutorPassesTcpPacketWhenCoordinatorReportsNotRelevant()
    {
        var reinjector = new FakeReinjector();
        await using var coordinator = new TcpProxyCoordinator(
            new ThrowingRedirectListenerFactory(), new ThrowingRelayFactory(), new ThrowingRedirectInjector(),
            new TcpRedirectTable(), new SelfTrafficRegistry(), new FakeLocalAddressProvider());
        var executor = new NdisPacketActionExecutor(reinjector, tcpProxy: coordinator);
        var key = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(FrameBuilders.CreateIpv4TcpFrame()), FlowContext(key), new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 7));

        await executor.ProxyAsync(packet, new Socks5Server("p", "127.0.0.1", 1080, null, null), CancellationToken.None);

        Assert.Equal(1, reinjector.ToAdapterCount);
        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal((nint)7, reinjector.LastAdapterHandle);
    }

    // ---- Helpers ----

    private static ValidatedConfiguration CreateConfig(RuleMatcher matcher, FlowAction fallback, FlowAction ruleAction = FlowAction.Pass, string? server = null)
    {
        var servers = new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase);
        if (server is not null) servers[server] = new Socks5Server(server, "127.0.0.1", 1080, null, null);
        var rules = new[] { new PolicyRule(matcher, new FlowDecision(ruleAction, 0, server)) };
        return new ValidatedConfiguration(servers, new PolicySnapshot(rules, fallback));
    }

    private static FlowContext FlowContext(FlowKey key) => new(key, null, null, null, null, key.Remote.Port);

    private static CapturedFlowPacket FlowPacket(ushort localPort, FlowOriginKind origin, string adapterId, string adapterName)
    {
        var adapter = new AdapterContext(adapterId, adapterName, 1);
        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), 443),
            TransportProtocol.Tcp,
            origin,
            adapter);
        var context = new FlowContext(key, null, null, adapterId, adapterName, key.Remote.Port);
        return new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), context);
    }
}
