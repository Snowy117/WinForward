using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class FlowDispatcherTests
{
    [Fact]
    public async Task FirstPacketClaimsPolicyAndUdpResponseDirectionIsPassed()
    {
        var config = CreateConfig();
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor);
        var key = CreateKey();
        var first = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), Context(key));
        var reverse = new CapturedFlowPacket(new PacketLease(new byte[] { 2 }), Context(key.Reverse()));

        await dispatcher.DispatchAsync(first, CancellationToken.None);
        await dispatcher.DispatchAsync(reverse, CancellationToken.None);

        // The client datagram is proxied once (policy evaluated once). The reverse datagram is a
        // relay response on the proxied flow and must be delivered to the local client, not
        // re-proxied back to the relay (otherwise the response loops forever).
        Assert.Equal(1, executor.ProxyCount);
        Assert.Equal(1, executor.PassCount);
        Assert.Equal(PacketDisposition.ProxyConsumed, first.Lease.Disposition);
        Assert.Equal(PacketDisposition.Pass, reverse.Lease.Disposition);
    }

    [Fact]
    public async Task UdpFlowWithTcpListenerPortCollisionIsNotDroppedByReverseHandler()
    {
        // H1: the dispatcher invokes the TCP reverse handler only for TCP packets. A UDP datagram
        // whose local AND remote ports equal an active TCP proxy-listener port value must be
        // evaluated by normal flow/policy (here proxied) and never routed into the reverse handler,
        // whose numeric-port matching could otherwise drop it.
        var server = new Socks5Server("primary", "127.0.0.1", 1080, null, null);
        var servers = new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase) { [server.Name] = server };
        const ushort collidingPort = 40001;
        var rules = new[] { new PolicyRule(new RuleMatcher(RemotePorts: [(collidingPort, collidingPort)]), new FlowDecision(FlowAction.Proxy, 0, server.Name)) };
        var config = new ValidatedConfiguration(servers, new PolicySnapshot(rules, FlowAction.Block));

        var executor = new FakeExecutor();
        var reverseCalls = 0;
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, reverseHandler: (packet, ct) =>
        {
            // If the dispatcher routed a UDP packet here, it would drop it (Blocked). The H1 gate
            // must prevent that.
            reverseCalls++;
            return ValueTask.FromResult(TcpRedirectOutcome.Blocked);
        });

        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), collidingPort),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), collidingPort),
            TransportProtocol.Udp, FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), Context(key));

        await dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.Equal(0, reverseCalls);
        Assert.Equal(1, executor.ProxyCount);
        Assert.Equal(PacketDisposition.ProxyConsumed, packet.Lease.Disposition);
    }

    [Fact]
    public async Task SelfTrafficIsPassedBeforeReverseHookAndCatchAllPolicy()
    {
        var config = CreateConfig();
        var executor = new FakeExecutor();
        var reverseCalls = 0;
        var dispatcher = new FlowDispatcher(config, new FakeGuard { Owned = true }, executor, reverseHandler: (packet, ct) =>
        {
            reverseCalls++;
            return ValueTask.FromResult(TcpRedirectOutcome.Blocked);
        });
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), Context(CreateKey(TransportProtocol.Tcp)));

        await dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.Equal(0, reverseCalls);
        Assert.Equal(1, executor.PassCount);
        Assert.Equal(0, executor.ProxyCount);
    }

    [Fact]
    public async Task ReverseHookRunsBeforeExistingFlowResolution()
    {
        var config = CreateConfig();
        var executor = new FakeExecutor();
        var reverseCalls = 0;
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, reverseHandler: (packet, ct) =>
        {
            reverseCalls++;
            return ValueTask.FromResult(reverseCalls == 1 ? TcpRedirectOutcome.NotRelevant : TcpRedirectOutcome.Injected);
        });
        var key = CreateKey(TransportProtocol.Tcp);
        var first = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), Context(key));
        var reverse = new CapturedFlowPacket(new PacketLease(new byte[] { 2 }), Context(key.Reverse()));

        await dispatcher.DispatchAsync(first, CancellationToken.None);
        await dispatcher.DispatchAsync(reverse, CancellationToken.None);

        Assert.Equal(2, reverseCalls);
        Assert.Equal(1, executor.ProxyCount);
        Assert.Equal(PacketDisposition.ProxyConsumed, first.Lease.Disposition);
        Assert.Equal(PacketDisposition.ProxyConsumed, reverse.Lease.Disposition);
    }

    [Fact]
    public void SelfTrafficTokenIsExactAndGenerationSafe()
    {
        var registry = new SelfTrafficRegistry();
        var key = CreateKey();
        var selfKey = new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Udp, key.Local, key.Remote);
        using var first = registry.Register(selfKey);
        var context = Context(key);
        Assert.True(registry.IsOwned(context));

        using var second = registry.Register(selfKey);
        first.Dispose();
        Assert.True(registry.IsOwned(context));
        second.Dispose();
        Assert.False(registry.IsOwned(context));
    }

    [Fact]
    public void SelfTrafficGuardMatchesReverseRelayTuple()
    {
        var registry = new SelfTrafficRegistry();
        var key = CreateKey();
        using var token = registry.Register(new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Udp, key.Local, key.Remote));

        Assert.True(registry.IsOwned(Context(key.Reverse())));
    }

    private static ValidatedConfiguration CreateConfig()
    {
        var server = new Socks5Server("primary", "127.0.0.1", 1080, null, null);
        var servers = new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase) { [server.Name] = server };
        var rules = new[] { new PolicyRule(new RuleMatcher(RemotePorts: [(53, 53)]), new FlowDecision(FlowAction.Proxy, 0, server.Name)) };
        return new ValidatedConfiguration(servers, new PolicySnapshot(rules, FlowAction.Block));
    }

    private static FlowKey CreateKey(TransportProtocol protocol = TransportProtocol.Udp) => FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), protocol, FlowOriginKind.Host);
    private static FlowContext Context(FlowKey key) => new(key, "dns.exe", null, null, null, key.Remote.Port);

    private sealed class FakeGuard : ISelfTrafficGuard { public bool Owned { get; init; } public bool IsOwned(FlowContext context) => Owned; }

    private sealed class FakeExecutor : IPacketActionExecutor
    {
        public int PassCount { get; private set; }
        public int ProxyCount { get; private set; }
        public ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) { PassCount++; return ValueTask.CompletedTask; }
        public ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken) { ProxyCount++; return ValueTask.CompletedTask; }
    }
}
