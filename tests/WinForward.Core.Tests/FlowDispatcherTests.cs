using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
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
        var reverse = new RecordingReverseHandler(() => TcpRedirectOutcome.Blocked);
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, reverseHandler: reverse);

        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), collidingPort),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), collidingPort),
            TransportProtocol.Udp, FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), Context(key));

        await dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.Equal(0, reverse.HandleCount);
        Assert.Equal(1, executor.ProxyCount);
        Assert.Equal(PacketDisposition.ProxyConsumed, packet.Lease.Disposition);
    }

    [Fact]
    public async Task SelfTrafficIsPassedBeforeReverseHookAndCatchAllPolicy()
    {
        var config = CreateConfig();
        var executor = new FakeExecutor();
        var reverse = new RecordingReverseHandler(() => TcpRedirectOutcome.Blocked);
        var dispatcher = new FlowDispatcher(config, new FakeGuard { Owned = true }, executor, reverseHandler: reverse);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), Context(CreateKey(TransportProtocol.Tcp)));

        await dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.Equal(0, reverse.HandleCount);
        Assert.Equal(1, executor.PassCount);
        Assert.Equal(0, executor.ProxyCount);
    }

    [Fact]
    public async Task ReverseHookRunsBeforeExistingFlowResolution()
    {
        var config = CreateConfig();
        var executor = new FakeExecutor();
        var reverse = new RecordingReverseHandler();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, reverseHandler: reverse);
        var key = CreateKey(TransportProtocol.Tcp);
        var first = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), Context(key));
        var reversePacket = new CapturedFlowPacket(new PacketLease(new byte[] { 2 }), Context(key.Reverse()));

        await dispatcher.DispatchAsync(first, CancellationToken.None);
        await dispatcher.DispatchAsync(reversePacket, CancellationToken.None);

        Assert.Equal(2, reverse.HandleCount);
        Assert.Equal(1, executor.ProxyCount);
        Assert.Equal(PacketDisposition.ProxyConsumed, first.Lease.Disposition);
        Assert.Equal(PacketDisposition.ProxyConsumed, reversePacket.Lease.Disposition);
    }

    [Fact]
    public async Task RemoveExpiredFlowsSkipsHeldEntriesAndDefaultsToRemoveAll()
    {
        var config = CreateConfig();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), new FakeExecutor());
        var key = CreateKey(TransportProtocol.Tcp);
        await dispatcher.DispatchAsync(new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), Context(key)), CancellationToken.None);

        var now = DateTimeOffset.UtcNow.AddMinutes(10);

        // A held flow (relaying redirect session / grace tombstone) survives the sweep.
        Assert.Equal(0, dispatcher.RemoveExpiredFlows(now, TimeSpan.FromMinutes(1), isHeld: _ => true));
        // The hold lapsed: the entry expires at its original idle point.
        Assert.Equal(1, dispatcher.RemoveExpiredFlows(now, TimeSpan.FromMinutes(1), isHeld: _ => false));

        // A null predicate keeps the previous remove-everything behavior.
        await dispatcher.DispatchAsync(new CapturedFlowPacket(new PacketLease(new byte[] { 2 }), Context(key)), CancellationToken.None);
        Assert.Equal(1, dispatcher.RemoveExpiredFlows(now, TimeSpan.FromMinutes(1)));
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

    /// <summary>
    /// A reverse handler whose <see cref="WantsPacket"/> always diverts — the pre-X1 dispatcher
    /// shape these slow-path tests were written against — and whose handling outcome is scripted.
    /// </summary>
    private sealed class RecordingReverseHandler(Func<TcpRedirectOutcome>? outcome = null) : ITcpReverseHandler
    {
        private int _calls;
        public int HandleCount => _calls;
        public bool WantsPacket(in CapturedFlowPacket packet) => true;
        public ValueTask<TcpRedirectOutcome> HandleReverseIfApplicableAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
        {
            var calls = ++_calls;
            return ValueTask.FromResult(outcome?.Invoke() ?? (calls == 1 ? TcpRedirectOutcome.NotRelevant : TcpRedirectOutcome.Injected));
        }
    }
}
