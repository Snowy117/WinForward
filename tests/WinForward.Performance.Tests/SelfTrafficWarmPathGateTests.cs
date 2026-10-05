using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Performance.Tests;

public sealed class SelfTrafficWarmPathGateTests
{
    [Fact]
    public async Task WarmHitTakesZeroExactTupleGuardProbes()
    {
        var harness = new SelfTrafficHarness();
        var key = SelfTrafficHarness.ProxyTuple(45_100);

        await harness.DispatchAsync(key);
        Assert.Equal(1, harness.Guard.ExactChecks);
        Assert.Equal(1, harness.Guard.WildcardChecks);
        Assert.Equal(1, harness.Executor.ProxyCount);

        for (var index = 0; index < 256; index++) await harness.DispatchAsync(key);

        Assert.Equal(1, harness.Guard.ExactChecks);
        Assert.Equal(257, harness.Guard.WildcardChecks);
        Assert.Equal(257, harness.Executor.ProxyCount);
    }

    [Fact]
    public async Task EveryClaimCallsTheFullGuardExactlyOnce()
    {
        var harness = new SelfTrafficHarness();
        const int claims = 8;
        for (var index = 0; index < claims; index++) await harness.DispatchAsync(SelfTrafficHarness.ProxyTuple((ushort)(45_200 + index)));
        Assert.Equal(claims, harness.Guard.ExactChecks);

        for (var index = 0; index < claims; index++) await harness.DispatchAsync(SelfTrafficHarness.ProxyTuple((ushort)(45_200 + index)));
        Assert.Equal(claims, harness.Guard.ExactChecks);
        Assert.Equal(2 * claims, harness.Executor.ProxyCount);
    }

    [Fact]
    public async Task ARelayWildcardTupleIsNeverProxiedOnAWarmHit()
    {
        var harness = new SelfTrafficHarness();
        var key = SelfTrafficHarness.ProxyTuple(45_300);
        await harness.DispatchAsync(key);
        Assert.Equal(1, harness.Executor.ProxyCount);

        using var registration = harness.Registry.Register(new SelfTrafficRegistry.SelfTrafficKey(
            TransportProtocol.Tcp,
            Endpoint.From(IPAddress.Any, key.Local.Port),
            key.Remote));

        await harness.DispatchAsync(key);

        Assert.Equal(1, harness.Executor.ProxyCount);
        Assert.Equal(1, harness.Executor.PassCount);
    }

    [Fact]
    public async Task ASelfOwnedTupleIsNeverClaimed()
    {
        var harness = new SelfTrafficHarness();
        var key = SelfTrafficHarness.ProxyTuple(45_400);
        using var registration = harness.Registry.Register(new SelfTrafficRegistry.SelfTrafficKey(
            TransportProtocol.Tcp,
            key.Local,
            key.Remote));

        await harness.DispatchAsync(key);

        Assert.Equal(0, harness.Dispatcher.FlowCount);
        Assert.Equal(1, harness.Executor.PassCount);
        Assert.Equal(0, harness.Executor.ProxyCount);
        Assert.Equal(1, harness.Guard.ExactChecks);
    }

    private sealed class SelfTrafficHarness
    {
        internal SelfTrafficHarness()
        {
            Executor = new FakeExecutor();
            Guard = new CountingSelfTrafficGuard(new SelfTrafficRegistry());
            var server = new Socks5Server("primary", "127.0.0.1", 1080, Username: null, Password: null);
            var servers = new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase) { [server.Name] = ProxyTarget.FromServer(server) };
            var rules = new[] { new PolicyRule(new RuleMatcher(), new FlowDecision(FlowAction.Proxy, 0, server.Name)) };
            var config = new ValidatedConfiguration(servers, new PolicySnapshot(rules, FlowAction.Block));
            Dispatcher = new FlowDispatcher(config, Guard, Executor);
        }

        internal CountingSelfTrafficGuard Guard { get; }
        internal FakeExecutor Executor { get; }
        internal FlowDispatcher Dispatcher { get; }
        internal SelfTrafficRegistry Registry => Guard.Inner;

        internal static FlowKey ProxyTuple(ushort localPort) => FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
            Endpoint.From(IPAddress.Parse("127.0.0.1"), 1080),
            TransportProtocol.Tcp,
            FlowOriginKind.Host);

        internal ValueTask DispatchAsync(FlowKey key)
        {
            var context = FlowBuilders.Context(key, "app.exe", adapterId: "eth0");
            return Dispatcher.DispatchAsync(new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), context), CancellationToken.None);
        }
    }

    /// <summary>
    /// Records the two guard halves separately: <see cref="ExactChecks"/> counts full
    /// <see cref="ISelfTrafficGuard.IsOwned"/> calls and <see cref="WildcardChecks"/> the
    /// wildcard-only probes.
    /// </summary>
    private sealed class CountingSelfTrafficGuard(SelfTrafficRegistry inner) : ISelfTrafficGuard
    {
        private int _exactChecks;
        private int _wildcardChecks;

        internal SelfTrafficRegistry Inner { get; } = inner;

        internal int ExactChecks => Volatile.Read(ref _exactChecks);

        internal int WildcardChecks => Volatile.Read(ref _wildcardChecks);

        public bool IsOwned(FlowContext context)
        {
            Interlocked.Increment(ref _exactChecks);
            return Inner.IsOwned(context);
        }

        public bool IsWildcardOwned(FlowContext context)
        {
            Interlocked.Increment(ref _wildcardChecks);
            return Inner.IsWildcardOwned(context);
        }
    }
}
