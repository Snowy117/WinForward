using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

public sealed class TcpProxyCoordinatorConcurrencyTests
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
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());
        var packet = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);

        var first = await coordinator.HandleSynAsync(packet, s_server, CancellationToken.None);
        await coordinator.DrainPendingSetupsAsync();
        var second = await coordinator.HandleSynAsync(packet, s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.SetupPending, first);
        // A retransmitted SYN after setup settles resolves the association and re-injects toward
        // the listener; the flow is still one association (claim exactly-once).
        Assert.Equal(TcpRedirectOutcome.Injected, second);
        Assert.Single(listenerFactory.Listeners);
        Assert.Equal(2, injector.InjectedFrames.Count);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void DistinctTranslatedTuplesMaySharePortWithoutPartialClaim()
    {
        var table = new TcpRedirectTable();
        var now = DateTimeOffset.UtcNow;
        var firstKey = FlowKey.Create(Endpoint.From(s_clientIpv4, 53000), Endpoint.From(s_destIpv4, 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var secondKey = FlowKey.Create(Endpoint.From(s_clientIpv6, 53001), Endpoint.From(s_destIpv6, 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var adapter = new AdapterContext("eth0", "Ethernet", 1);

        Assert.True(table.TryClaim(firstKey, firstKey.Remote, adapter, (nint)0x1234, Endpoint.From(IPAddress.Loopback, 42000), null, now, out var first));
        Assert.True(table.TryClaim(secondKey, secondKey.Remote, adapter, (nint)0x1234, Endpoint.From(IPAddress.IPv6Loopback, 42000), null, now, out var second));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public async Task TranslatedTupleAliasCollisionIsRejectedFailClosed()
    {
        var sharedTuple = Endpoint.From(IPAddress.Loopback, 9999);
        var listenerFactory = new FakeListenerFactory(sharedTuple);
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

        var first = await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        await coordinator.DrainPendingSetupsAsync();
        var second = await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80), s_server, CancellationToken.None);
        await coordinator.DrainPendingSetupsAsync();

        Assert.Equal(TcpRedirectOutcome.SetupPending, first);
        // The second flow's claim fails closed in the background (alias collision); the outcome
        // surface for the failure is the released listener and unchanged table, plus the setup
        // cooldown that consumes a same-tuple retransmission.
        Assert.Equal(TcpRedirectOutcome.SetupPending, second);
        Assert.Single(listenerFactory.Listeners, listener => !listener.IsDisposed);
        Assert.Single(injector.InjectedFrames);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public async Task SelfTrafficRegistryContainsListenerTupleBeforeInjection()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

        await HandleSynSettledAsync(coordinator, MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server);

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
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());
        var packet = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        const int count = 32;

        var tasks = Enumerable.Range(0, count)
            .Select(_ => coordinator.HandleSynAsync(packet, s_server, CancellationToken.None).AsTask())
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);

        // R8: callers inside the pending window return SetupPending; a caller that lands after
        // the background setup completes resolves the association and re-injects (Injected).
        // Both are exactly-once accepts — one association, at most one LIVE listener: a racer
        // whose fast path missed the claim but whose retain landed after the entry removal may
        // start a redundant setup whose loser branch releases it immediately.
        Assert.All(outcomes, outcome => Assert.True(outcome is TcpRedirectOutcome.SetupPending or TcpRedirectOutcome.Injected));
        await coordinator.DrainPendingSetupsAsync();
        Assert.Single(listenerFactory.Listeners, listener => !listener.IsDisposed);
        Assert.Equal(1, table.Count);
        Assert.Equal(0, coordinator.CapacityRejectionCount);
    }

    [Fact]
    public async Task ConcurrentSynBurstWhileListenerSetupIsParkedIsAbsorbedIntoOneSetup()
    {
        // R8 moved new-flow setup off the pump path, so the redirect-table exactly-once race the
        // historical barrier test forced is now unreachable through the coordinator: every burst
        // caller returns SetupPending immediately and the pending index absorbs retransmissions
        // (overwrite, never a second setup task) while the ONE background setup is parked inside
        // the gated factory. Exactly-once now holds by construction; the loser branch keeps its
        // own test below (pre-claim).
        var listenerFactory = new GatedListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());
        var packet = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        const int count = 8;

        var tasks = Enumerable.Range(0, count)
            .Select(_ => coordinator.HandleSynAsync(packet, s_server, CancellationToken.None).AsTask())
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);

        Assert.All(outcomes, outcome => Assert.Equal(TcpRedirectOutcome.SetupPending, outcome));
        // The single background setup is parked inside the factory while the whole burst is
        // already absorbed — the pump-side handler never waited on the bind.
        await listenerFactory.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        listenerFactory.Release();
        await coordinator.DrainPendingSetupsAsync();

        Assert.Single(listenerFactory.Listeners);
        Assert.Single(injector.InjectedFrames);
        Assert.Equal(1, table.Count);
        Assert.Equal(0, coordinator.ConcurrentLoserCount);
    }

    [Fact]
    public async Task PreClaimedAssociationReinjectsAgainstExistingClaim()
    {
        // A flow key claimed by an earlier association (e.g. a same-tuple SYN racing a teardown
        // whose tombstone has not armed yet, or any external pre-claim): the pump-side fast path
        // resolves the existing association and re-injects toward ITS listener tuple — no second
        // listener, no second session. The background concurrent-loser branch remains as
        // defense-in-depth for the TTL-expiry interleaving where two setup tasks can still meet
        // at the table claim.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());
        var key = FlowKey.Create(Endpoint.From(s_clientIpv4, 53000), Endpoint.From(s_destIpv4, 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var adapter = new AdapterContext("eth0", "Ethernet", 1);
        var now = DateTimeOffset.UtcNow;
        Assert.True(table.TryClaim(key, key.Remote, adapter, (nint)0x1234, Endpoint.From(IPAddress.Loopback, 42000), null, now, out _));

        var outcome = await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, outcome);
        Assert.Equal(1, table.Count);
        Assert.Empty(listenerFactory.Listeners);
        // The re-inject rewrote the SYN toward the pre-claimed association's listener tuple.
        var injected = Assert.Single(injector.InjectedFrames);
        Assert.Equal(42000, injected.Frame[14 + 20 + 2] << 8 | injected.Frame[14 + 20 + 3]);
    }

    [Fact]
    public async Task ExpiryRemovesIdleAssociations()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

        await HandleSynSettledAsync(coordinator, MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server);
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
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider());

        // Session 1 remains Redirecting (no accepted connection ever relayed).
        await HandleSynSettledAsync(coordinator, MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server);
        // Session 2 is promoted to Relaying once its accepted connection establishes a relay.
        await HandleSynSettledAsync(coordinator, MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.54"), 53001, 443), s_server);
        var relayingListener = listenerFactory.Listeners[1];
        await relayingListener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(IPAddress.Parse("192.0.2.54"), 53001)), CancellationToken.None);
        await WaitForAsync(() => table.Snapshot().Any(a => a.Phase == RelayPhase.Relaying));
        Assert.Equal(2, table.Count);

        var removed = await coordinator.RemoveExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1));

        Assert.Equal(1, removed);
        Assert.Equal(1, table.Count);
        Assert.Equal(RelayPhase.Relaying, Assert.Single(table.Snapshot()).Phase);
    }

    [Fact]
    public async Task ExpiryRacingRelayEstablishmentCannotAttachDetachedRelay()
    {
        var listenerFactory = new FakeListenerFactory();
        var relayFactory = new GatedRelayFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider());

        await HandleSynSettledAsync(coordinator, MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server);
        var listener = Assert.Single(listenerFactory.Listeners);
        var accepted = new FakeAcceptedConnection(Endpoint.From(s_destIpv4, 53000));
        await listener.AcceptChannel.Writer.WriteAsync(accepted, CancellationToken.None);
        await relayFactory.EstablishStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, await coordinator.RemoveExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1)));
        Assert.Equal(0, table.Count);
        Assert.True(listener.IsDisposed);

        relayFactory.Release();
        await WaitForAsync(() => relayFactory.CreatedRelay?.IsDisposed == true && accepted.IsDisposed);
        Assert.True(accepted.IsDisposed);
    }

    [Fact]
    public async Task SilentRelayingFlowSurvivesSweepsWithoutReevaluatingDecision()
    {
        var harness = CreateDispatcherHarness();
        await using var coordinator = harness.Coordinator;
        await EstablishRelayingSessionAsync(harness);
        Assert.Equal(1, harness.Logger.Events.Count(e => string.Equals(e.Name, "flow.created", StringComparison.Ordinal)));

        await using var sweeper = new IdleExpirySweeper(
            harness.Dispatcher,
            coordinator,
            udp: null,
            interval: TimeSpan.FromMilliseconds(100),
            flowIdleTimeout: TimeSpan.FromMilliseconds(250),
            redirectIdleTimeout: TimeSpan.FromMinutes(5),
            relayIdleTimeout: TimeSpan.FromMinutes(5),
            logger: harness.Logger);
        sweeper.Start();

        // Silent across many sweep ticks, far past the flow idle timeout: the relaying session
        // holds the flow decision, so a resumed connection is not re-evaluated.
        await Task.Delay(900);
        Assert.Equal(1, harness.Logger.Events.Count(e => string.Equals(e.Name, "flow.created", StringComparison.Ordinal)));
        Assert.True(coordinator.HoldsFlow(MakeHostFlowKey()));

        harness.Injector.InjectedFrames.Clear();
        var resume = MakeForwardTcpPacket(s_clientIpv4, s_destIpv4, 53000, 443, TcpFlagAck);
        await harness.Dispatcher.DispatchAsync(resume, CancellationToken.None);

        Assert.Equal(1, harness.Logger.Events.Count(e => string.Equals(e.Name, "flow.created", StringComparison.Ordinal)));
        Assert.Single(harness.Injector.InjectedFrames);
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
}
