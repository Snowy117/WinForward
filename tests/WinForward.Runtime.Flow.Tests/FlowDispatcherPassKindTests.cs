using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.TestSupport;
using WinForward.Windows;
using Xunit;

namespace WinForward.Runtime.Flow.Tests;

/// <summary>
/// The dispatcher's pass tagging, which is what makes the kernel mirror safe: a claimed policy
/// <c>Pass</c> is tagged <see cref="PacketPassKind.Flow"/>; self traffic, non-flow frames and a
/// proxied UDP flow's reverse response carry their own kinds so the pass observer never mirrors them.
/// </summary>
public sealed class FlowDispatcherPassKindTests
{
    private static readonly IPAddress s_client = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_server = IPAddress.Parse("192.0.2.53");

    [Fact]
    public async Task AnInlinePolicyPassIsTaggedAsAFlowPass()
    {
        var executor = new RecordingExecutor();
        var dispatcher = new FlowDispatcher(CreateConfig(new RuleMatcher(), FlowAction.Pass), new FakeGuard(), executor);
        var packet = FlowPacket(53000, TransportProtocol.Tcp, FlowOriginKind.Host, "id-a", "Ethernet");

        await dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.Equal([PacketPassKind.Flow], executor.PassKinds);
    }

    [Fact]
    public async Task ATaggedFlowPassReachesTheExecutorTagged()
    {
        var executor = new RecordingExecutor();
        var dispatcher = new FlowDispatcher(CreateConfig(new RuleMatcher(), FlowAction.Pass), new FakeGuard(), executor, flowCapacity: 1);
        var first = FlowPacket(53001, TransportProtocol.Tcp, FlowOriginKind.Host, "id-a", "Ethernet");
        var warm = FlowPacket(53001, TransportProtocol.Tcp, FlowOriginKind.Host, "id-a", "Ethernet");

        await dispatcher.DispatchAsync(first, CancellationToken.None);
        await dispatcher.DispatchAsync(warm, CancellationToken.None);

        // The warm resolve returns the stored decision and keeps the same eligibility: a claimed pass
        // is a claimed pass whether it is decided inline or served from the flow table.
        Assert.Equal([PacketPassKind.Flow, PacketPassKind.Flow], executor.PassKinds);
    }

    [Fact]
    public async Task SelfTrafficIsTaggedAsSelfTraffic()
    {
        var executor = new RecordingExecutor();
        var dispatcher = new FlowDispatcher(CreateConfig(new RuleMatcher(), FlowAction.Block), new FakeGuard { Owned = true }, executor);
        var packet = FlowPacket(53002, TransportProtocol.Tcp, FlowOriginKind.Host, "id-a", "Ethernet");

        await dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.Equal([PacketPassKind.SelfTraffic], executor.PassKinds);
    }

    [Fact]
    public async Task ANonFlowFrameIsTaggedAsNonFlow()
    {
        var executor = new RecordingExecutor();
        var dispatcher = new FlowDispatcher(CreateConfig(new RuleMatcher(), FlowAction.Block), new FakeGuard(), executor);
        var adapter = new WindowsAdapter("id-a", "Ethernet", "a", 1, 1);
        var packet = new CapturedFlowPacket(
            new PacketLease(new byte[] { 1 }),
            PacketFlowClassifier.ClassifyNonFlow(adapter, isOnSend: false, FlowBuilders.SlotOf(adapter.StableId, adapter.Generation, adapter.FriendlyName), FlowBuilders.Slots));

        await dispatcher.DispatchNonFlowAsync(packet, CancellationToken.None);

        Assert.Equal([PacketPassKind.NonFlow], executor.PassKinds);
    }

    [Fact]
    public async Task AProxiedUdpFlowsReverseResponseIsTaggedAsReverse()
    {
        var server = new Socks5Server("primary", "127.0.0.1", 1080, Username: null, Password: null);
        var targets = new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase) { [server.Name] = ProxyTarget.FromServer(server) };
        var config = new ValidatedConfiguration(
            targets,
            new PolicySnapshot([new(new RuleMatcher(Protocols: new HashSet<TransportProtocol> { TransportProtocol.Udp }), new FlowDecision(FlowAction.Proxy, 0, server.Name))], FlowAction.Pass));
        var executor = new RecordingExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor);
        var request = FlowPacket(53003, TransportProtocol.Udp, FlowOriginKind.Host, "id-a", "Ethernet");
        var response = ReverseFlowPacket(53003, TransportProtocol.Udp);

        await dispatcher.DispatchAsync(request, CancellationToken.None);
        await dispatcher.DispatchAsync(response, CancellationToken.None);

        // The forward decision is a proxy decision (not a pass), and the response is passed only so it
        // reaches the client. A kernel rule for this tuple would bypass the proxy in the forward
        // direction, so the pass must be tagged Reverse rather than Flow.
        Assert.Equal([PacketPassKind.Reverse], executor.PassKinds);
    }

    private static ValidatedConfiguration CreateConfig(RuleMatcher matcher, FlowAction fallback, FlowAction ruleAction = FlowAction.Pass)
    {
        var rules = new[] { new PolicyRule(matcher, new FlowDecision(ruleAction, 0, TargetName: null)) };
        return new ValidatedConfiguration(new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase), new PolicySnapshot(rules, fallback));
    }

    private static CapturedFlowPacket FlowPacket(ushort localPort, TransportProtocol protocol, FlowOriginKind origin, string adapterId, string adapterName)
    {
        var adapterSlot = FlowBuilders.SlotOf(adapterId, 1);
        var key = FlowKey.Create(Endpoint.From(s_client, localPort), Endpoint.From(s_server, protocol == TransportProtocol.Udp ? (ushort)53 : (ushort)443), protocol, origin, adapterSlot, 1);
        var context = FlowBuilders.Context(key, adapterId: adapterId, adapterName: adapterName);
        return new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), context);
    }

    private static CapturedFlowPacket ReverseFlowPacket(ushort localPort, TransportProtocol protocol)
    {
        var key = FlowKey.Create(Endpoint.From(s_server, protocol == TransportProtocol.Udp ? (ushort)53 : (ushort)443), Endpoint.From(s_client, localPort), protocol, FlowOriginKind.Forwarded);
        return new CapturedFlowPacket(new PacketLease(new byte[] { 2 }), FlowBuilders.Context(key));
    }
}
