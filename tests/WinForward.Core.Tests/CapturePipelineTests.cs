using System.Buffers.Binary;
using System.Net;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class CapturePipelineTests
{
    // ---- Packet parser (IpTcpUdpPacket) ----

    [Fact]
    public void ParsesIpv4TcpAndUdpAndRejectsNonTcpUdp()
    {
        Assert.True(IpTcpUdpPacket.TryParse(CreateIpv4TcpFrame(), out var tcp));
        Assert.Equal(PacketTransport.Tcp, tcp.Transport);
        Assert.Equal((ushort)53000, tcp.SourcePort);
        Assert.Equal((ushort)443, tcp.DestinationPort);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), tcp.SourceAddress);
        Assert.Equal(IPAddress.Parse("192.0.2.53"), tcp.DestinationAddress);

        Assert.True(IpTcpUdpPacket.TryParse(CreateIpv4UdpFrame(), out var udp));
        Assert.Equal(PacketTransport.Udp, udp.Transport);
        Assert.Equal((ushort)53, udp.DestinationPort);

        var icmp = CreateIpv4UdpFrame();
        icmp[23] = 1; // ICMP
        Assert.False(IpTcpUdpPacket.TryParse(icmp, out _));
    }

    [Fact]
    public void ParsesIpv6TcpAndUdp()
    {
        Assert.True(IpTcpUdpPacket.TryParse(CreateIpv6TcpFrame(), out var tcp));
        Assert.Equal(PacketTransport.Tcp, tcp.Transport);
        Assert.Equal(IPAddress.Parse("2001:db8::10"), tcp.SourceAddress);
        Assert.Equal((ushort)443, tcp.DestinationPort);

        Assert.True(IpTcpUdpPacket.TryParse(CreateIpv6UdpFrame(), out var udp));
        Assert.Equal(PacketTransport.Udp, udp.Transport);
        Assert.Equal(IPAddress.Parse("2001:db8::53"), udp.DestinationAddress);
    }

    [Fact]
    public void RejectsIpv4FragmentsAndTruncatedFrames()
    {
        var frame = CreateIpv4TcpFrame();
        frame[20] = 0x20; // MF fragment flag
        Assert.False(IpTcpUdpPacket.TryParse(frame, out _));

        Assert.False(IpTcpUdpPacket.TryParse(frame.AsSpan(0, 14 + 12), out _));
    }

    [Fact]
    public void RejectsNonIpAndNonEthernetFrames()
    {
        var arp = new byte[14 + 28];
        arp[12] = 0x08;
        arp[13] = 0x06; // ARP
        Assert.False(IpTcpUdpPacket.TryParse(arp, out _));

        Assert.False(IpTcpUdpPacket.TryParse(new byte[10], out _));
    }

    // ---- Flow classifier ----

    [Fact]
    public void ClassifyFlowUsesDirectionForOriginAndAdapterForIdentity()
    {
        var adapter = new WindowsAdapter("id-a", "Ethernet", "internal-a", 1, 7);
        var view = new PacketView(PacketTransport.Tcp, IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.53"), 53000, 443, 20, 20);

        var host = PacketFlowClassifier.ClassifyFlow(view, adapter, isOnSend: true);
        Assert.Equal(FlowOriginKind.Host, host.Key.Origin);
        Assert.Equal("id-a", host.AdapterId);
        Assert.Equal("Ethernet", host.AdapterName);
        Assert.Equal((ushort)443, host.RemotePort);
        Assert.Equal(adapter.Generation, host.Key.OriginAdapterGeneration);

        var forwarded = PacketFlowClassifier.ClassifyFlow(view, adapter, isOnSend: false);
        Assert.Equal(FlowOriginKind.Forwarded, forwarded.Key.Origin);
    }

    [Fact]
    public void ClassifyFlowPreservesIpv6Endpoints()
    {
        var adapter = new WindowsAdapter("id-6", "vEthernet", "internal-6", 2, 1);
        var view = new PacketView(PacketTransport.Udp, IPAddress.Parse("2001:db8::10"), IPAddress.Parse("2001:db8::53"), 53000, 53, 40, 8);

        var context = PacketFlowClassifier.ClassifyFlow(view, adapter, isOnSend: false);

        Assert.Equal(AddressFamilyKind.IPv6, context.Key.AddressFamily);
        Assert.Equal(Endpoint.From(IPAddress.Parse("2001:db8::10"), 53000), context.Key.Local);
        Assert.Equal(Endpoint.From(IPAddress.Parse("2001:db8::53"), 53), context.Key.Remote);
    }

    // ---- Adapter scope resolution ----

    [Fact]
    public void AdapterScopeResolvesConstrainedAndUnconstrainedRules()
    {
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-b", "vEthernet 1", "b", 2, 1)
        };
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Pass, 0, null)),
            new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "browser.exe" }), new FlowDecision(FlowAction.Block, 1, null))
        ], FlowAction.Pass);

        Assert.True(CaptureAdapterScopeResolver.TryResolve(adapters, policy, out var scope, out _));
        // Unconstrained process rule widens scope to every adapter.
        Assert.Equal(2, scope.Count);
    }

    [Fact]
    public void AdapterScopeFailsOnMissingAndAmbiguousSelectors()
    {
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-a2", "Ethernet", "a2", 3, 1),
            new WindowsAdapter("id-b", "vEthernet (Shared)", "b", 2, 1)
        };
        var missing = new PolicySnapshot([new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nope" }), new FlowDecision(FlowAction.Pass, 0, null))], FlowAction.Pass);

        Assert.False(CaptureAdapterScopeResolver.TryResolve(adapters, missing, out _, out var missingErrors));
        Assert.Contains(missingErrors, error => error.Contains("nope", StringComparison.Ordinal));

        var ambiguous = new PolicySnapshot(
            [new(new RuleMatcher(AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ethernet" }), new FlowDecision(FlowAction.Pass, 0, null))],
            FlowAction.Pass);
        Assert.False(CaptureAdapterScopeResolver.TryResolve(adapters, ambiguous, out _, out var ambiguousErrors));
        Assert.Contains(ambiguousErrors, error => error.Contains("ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void AdapterScopeFailsWhenAdapterIdAndAdapterNameResolveToDifferentAdapters()
    {
        // Design §3: an ID/name selector must resolve to the same current adapter. A rule whose
        // adapterId and adapterName point at different adapters can never match (AND semantics) and
        // is a startup error, not a silent fallback.
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-b", "vEthernet 1", "b", 2, 1)
        };
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(
                AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" },
                AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vEthernet 1" }),
                new FlowDecision(FlowAction.Pass, 0, null))],
            FlowAction.Pass);

        Assert.False(CaptureAdapterScopeResolver.TryResolve(adapters, policy, out _, out var errors));
        Assert.Contains(errors, error => error.Contains("different adapters", StringComparison.Ordinal));
    }

    [Fact]
    public void AdapterScopeAcceptsAdapterIdAndAdapterNameResolvingToTheSameAdapter()
    {
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-b", "vEthernet 1", "b", 2, 1)
        };
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(
                AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-b" },
                AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vEthernet 1" }),
                new FlowDecision(FlowAction.Pass, 0, null))],
            FlowAction.Pass);

        Assert.True(CaptureAdapterScopeResolver.TryResolve(adapters, policy, out var scope, out _));
        var single = Assert.Single(scope);
        Assert.Equal("id-b", single.StableId);
    }

    [Fact]
    public void AdapterScopeIncludesEveryAdapterForFallbackOnlyPolicy()
    {
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-b", "vEthernet 1", "b", 2, 1)
        };
        var policy = new PolicySnapshot([], FlowAction.Pass);

        Assert.True(CaptureAdapterScopeResolver.TryResolve(adapters, policy, out var scope, out _));
        Assert.Equal(2, scope.Count);
    }

    [Fact]
    public void AdapterScopeConstrainedOnlyCapturesSelectedAdapters()
    {
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-b", "vEthernet 1", "b", 2, 1)
        };
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vEthernet 1" }), new FlowDecision(FlowAction.Pass, 0, null))],
            FlowAction.Pass);

        Assert.True(CaptureAdapterScopeResolver.TryResolve(adapters, policy, out var scope, out _));
        var single = Assert.Single(scope);
        Assert.Equal("id-b", single.StableId);
    }

    // ---- Flow table cross-origin / cross-adapter reuse ----

    [Fact]
    public void FlowTableResolvesReversePacketAcrossOriginKindAndReusesDecision()
    {
        var table = new FlowTable();
        var hostKey = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var decisions = 0;

        Assert.True(table.TryClaimResolved(hostKey, () => { decisions++; return new FlowDecision(FlowAction.Pass, 0, null); }, out var claimed));
        Assert.NotNull(claimed);
        Assert.Equal(1, decisions);

        // Response packet is ON_RECEIVE -> Forwarded origin, reverse endpoints.
        var responseKey = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), TransportProtocol.Tcp, FlowOriginKind.Forwarded);

        Assert.True(table.TryResolve(responseKey, out var resolved));
        Assert.Same(claimed, resolved);
        Assert.Equal(FlowAction.Pass, resolved!.Decision.Action);

        // Re-resolving the response must not re-evaluate policy.
        Assert.True(table.TryResolve(responseKey, out _));
        Assert.Equal(1, decisions);
    }

    [Fact]
    public void FlowTableReusesDecisionAcrossAdapterBoundaries()
    {
        var table = new FlowTable();
        var onAdapterA = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host, new AdapterContext("a", "Ethernet", 1));
        var onAdapterB = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), TransportProtocol.Tcp, FlowOriginKind.Forwarded, new AdapterContext("b", "vEthernet", 2));

        Assert.True(table.TryClaimResolved(onAdapterA, () => new FlowDecision(FlowAction.Block, 0, null), out var claimed));
        Assert.True(table.TryResolve(onAdapterB, out var resolved));
        Assert.Same(claimed, resolved);
    }

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

        var forwardedKey = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Forwarded);
        var forwardedPacket = new CapturedFlowPacket(new PacketLease(new byte[] { 2 }), FlowContext(forwardedKey));
        await dispatcher.DispatchAsync(forwardedPacket, CancellationToken.None);
        // Forwarded traffic has no host owner; the process rule cannot match, so it falls back to pass.
        Assert.Equal(1, attributor.Calls);
        Assert.Equal(2, executor.PassCount);
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
    public async Task DispatcherNonFlowEvaluatesAdapterRuleOrFallback()
    {
        var adapter = new WindowsAdapter("id-a", "vEthernet 1", "b", 2, 1);
        var config = CreateConfig(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), FlowAction.Pass, ruleAction: FlowAction.Block);
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor);

        var nonFlow = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), PacketFlowClassifier.ClassifyNonFlow(adapter, isOnSend: false));
        await dispatcher.DispatchNonFlowAsync(nonFlow, CancellationToken.None);

        Assert.Equal(1, executor.BlockCount);
        Assert.Equal(PacketDisposition.Block, nonFlow.Lease.Disposition);
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
    public async Task ExecutorReinjectsOnSendTowardAdapterAndOnReceiveTowardMstcp()
    {
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);

        var send = new CapturedFlowPacket(new PacketLease(new byte[] { 1, 2, 3 }), FlowContext(FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 1), Endpoint.From(IPAddress.Parse("192.0.2.53"), 2), TransportProtocol.Tcp, FlowOriginKind.Host)), new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 7));
        await executor.PassAsync(send, CancellationToken.None);
        Assert.Equal(1, reinjector.ToAdapterCount);
        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal((nint)7, reinjector.LastAdapterHandle);
        Assert.Equal(new byte[] { 1, 2, 3 }, reinjector.LastFrame);

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
        buffer.SetFrame(CreateIpv4TcpFrame(), NdisApiAbi.PacketFlagOnSend, (nint)7, flags: 0x4000_0021);

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

    // ---- Frame builders ----

    private static byte[] CreateIpv4TcpFrame()
    {
        var frame = new byte[14 + 20 + 20];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 40);
        frame[23] = 6;
        IPAddress.Parse("192.0.2.10").GetAddressBytes().CopyTo(frame, 26);
        IPAddress.Parse("192.0.2.53").GetAddressBytes().CopyTo(frame, 30);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 443);
        frame[46] = 0x50; // TCP data offset = 5
        return frame;
    }

    private static byte[] CreateIpv4UdpFrame()
    {
        var frame = new byte[14 + 20 + 8];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 28);
        frame[23] = 17;
        IPAddress.Parse("192.0.2.10").GetAddressBytes().CopyTo(frame, 26);
        IPAddress.Parse("192.0.2.53").GetAddressBytes().CopyTo(frame, 30);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38, 2), 8);
        return frame;
    }

    private static byte[] CreateIpv6TcpFrame()
    {
        var frame = new byte[14 + 40 + 20];
        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), 20);
        frame[20] = 6;
        IPAddress.Parse("2001:db8::10").GetAddressBytes().CopyTo(frame, 22);
        IPAddress.Parse("2001:db8::53").GetAddressBytes().CopyTo(frame, 38);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(54, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(56, 2), 443);
        frame[66] = 0x50; // TCP data offset = 5
        return frame;
    }

    private static byte[] CreateIpv6UdpFrame()
    {
        var frame = new byte[14 + 40 + 8];
        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), 8);
        frame[20] = 17;
        IPAddress.Parse("2001:db8::10").GetAddressBytes().CopyTo(frame, 22);
        IPAddress.Parse("2001:db8::53").GetAddressBytes().CopyTo(frame, 38);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(54, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(56, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(58, 2), 8);
        return frame;
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

    private sealed class FakeGuard : ISelfTrafficGuard
    {
        public bool IsOwned(FlowContext context) => false;
    }

    private sealed class FakeAttributor : IProcessAttributor
    {
        private readonly string? _name;
        public int Calls { get; private set; }
        public FakeAttributor(string? name) => _name = name;
        public ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<ProcessIdentity?>(_name is null ? null : new ProcessIdentity(123, DateTime.UtcNow, _name, null));
        }
    }

    private sealed class FakeExecutor : IPacketActionExecutor
    {
        public int PassCount { get; private set; }
        public int BlockCount { get; private set; }
        public int ProxyCount { get; private set; }
        public ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) { PassCount++; return ValueTask.CompletedTask; }
        public ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) { BlockCount++; return ValueTask.CompletedTask; }
        public ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken) { ProxyCount++; return ValueTask.CompletedTask; }
    }

    private sealed class ThrowingPassExecutor : IPacketActionExecutor
    {
        public ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) => throw new InvalidOperationException("injection failed");
        public ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FakeReinjector : IPacketReinjector
    {
        public int ToAdapterCount { get; private set; }
        public int ToMstcpCount { get; private set; }
        public nint LastAdapterHandle { get; private set; }
        public uint LastFlags { get; private set; }
        public byte[]? LastFrame { get; private set; }

        public void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer)
        {
            ToAdapterCount++;
            LastAdapterHandle = adapterHandle;
            LastFlags = buffer.Flags;
            LastFrame = buffer.GetFrame().ToArray();
        }

        public void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer)
        {
            ToMstcpCount++;
            LastAdapterHandle = adapterHandle;
            LastFlags = buffer.Flags;
            LastFrame = buffer.GetFrame().ToArray();
        }
    }
}
