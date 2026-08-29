using System.ComponentModel;
using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

public sealed class TcpProxyCoordinatorCapacityTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);
    private static readonly IPAddress s_clientIpv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destIpv4 = IPAddress.Parse("192.0.2.53");

    [Fact]
    public async Task CapacityBoundBlocksFailClosed()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable(capacity: 1);
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider(), capacity: 1);

        var first = await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        var second = await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80), s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, first);
        Assert.Equal(TcpRedirectOutcome.Blocked, second);
        Assert.Single(listenerFactory.Listeners);
        // Frame 1 is the accepted flow's rewritten SYN; frame 2 is the S4 capacity RST|ACK that
        // fails the rejected client fast instead of leaving it to retransmit for the OS timeout.
        Assert.Equal(2, injector.InjectedFrames.Count);
        Assert.Equal(0x14, injector.InjectedFrames[1].Frame[47]);
    }

    [Fact]
    public async Task CapacityRejectionIsCountedTracedAndSummarizedAtInfoLevel()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable(capacity: 1);
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider(), logger, capacity: 1);

        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None));
        Assert.Equal(TcpRedirectOutcome.Blocked, await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80), s_server, CancellationToken.None));

        Assert.Equal(1, coordinator.CapacityRejectionCount);
        var rejection = Assert.Single(logger.Events, item => string.Equals(item.Name, "tcp.redirect.rejected", StringComparison.Ordinal));
        Assert.Contains(rejection.Fields, field => string.Equals(field.Key, "reason", StringComparison.Ordinal) && field.Value is "capacity");

        coordinator.LogCapacitySummary();
        var summary = Assert.Single(logger.Events, item => string.Equals(item.Name, "tcp.redirect.capacity", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Info, summary.Level);
        Assert.Contains(summary.Fields, field => string.Equals(field.Key, "budget", StringComparison.Ordinal) && field.Value is 1);
        Assert.Contains(summary.Fields, field => string.Equals(field.Key, "rejectedTotal", StringComparison.Ordinal) && field.Value is 1L);
        Assert.Contains(summary.Fields, field => string.Equals(field.Key, "rejectedSinceLastSummary", StringComparison.Ordinal) && field.Value is 1L);

        // The summary repeats only when further rejections arrived.
        var eventsAfterSummary = logger.Events.Count;
        coordinator.LogCapacitySummary();
        Assert.Equal(eventsAfterSummary, logger.Events.Count);
    }

    [Fact]
    public async Task OmittedCapacityKeepsLegacyDefaultBudget()
    {
        var listenerFactory = new FakeListenerFactory();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), new FakeInjector(), table, new SelfTrafficRegistry(), new FakeLocalAddressProvider());

        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None));
        Assert.Equal(0, coordinator.CapacityRejectionCount);
    }

    [Fact]
    public async Task ReverseInjectionWin32FailureFailsExplicitlyWithReasonTombstoneAndReset()
    {
        // D2: a stale-handle SendPacketToAdapter/ToMstcp failure (Win32Exception from the driver)
        // must surface as a warned, observable teardown — reason=injectionFailure with the native
        // error and flow key — plus a best-effort client reset and the grace tombstone, never a
        // silent passive fail.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector(throwOnCall: 2, exception: new Win32Exception(87));
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider(), logger);

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var synAck = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerTuple.Port, s_destIpv4, 53000, mutateFrame: f => f[47] = 0x12);
        Assert.Equal(TcpRedirectOutcome.Blocked, await coordinator.HandleReverseAsync(synAck, CancellationToken.None));

        var failure = Assert.Single(logger.Events, item => string.Equals(item.Name, "tcp.redirect.failed", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Warn, failure.Level);
        Assert.Contains(failure.Fields, field => string.Equals(field.Key, "reason", StringComparison.Ordinal) && field.Value is "injectionFailure");
        Assert.Contains(failure.Fields, field => string.Equals(field.Key, "nativeError", StringComparison.Ordinal) && field.Value is 87);
        Assert.Contains(failure.Fields, field => string.Equals(field.Key, "error", StringComparison.Ordinal) && field.Value is "Win32Exception");
        Assert.Contains(failure.Fields, field => string.Equals(field.Key, "adapterHandle", StringComparison.Ordinal) && field.Value is 0x1234L);
        Assert.Contains(failure.Fields, field => string.Equals(field.Key, "source", StringComparison.Ordinal) && field.Value is Endpoint source && source.Equals(Endpoint.From(s_clientIpv4, 53000)));
        Assert.Contains(failure.Fields, field => string.Equals(field.Key, "destination", StringComparison.Ordinal) && field.Value is Endpoint destination && destination.Equals(Endpoint.From(s_destIpv4, 443)));

        // The sequence recorders ran before the failed injection, so the best-effort reset was
        // still crafted: injected frames are exactly the SYN and the RST.
        Assert.Equal(2, injector.InjectedFrames.Count);
        Assert.Equal(0x14, injector.InjectedFrames[1].Frame[47]);
        Assert.Equal(0, table.Count);
        Assert.True(Assert.Single(listenerFactory.Listeners).IsDisposed);

        // The teardown went through the tombstone write point: a straggler on the original tuple is
        // consumed within the grace window instead of falling through to the executor.
        var straggler = MakeForwardTcpPacket(s_clientIpv4, s_destIpv4, 53000, 443, TcpFlagAck);
        Assert.Equal(TcpRedirectOutcome.Dropped, await coordinator.HandlePacketAsync(straggler, s_server, CancellationToken.None));
    }

    [Fact]
    public async Task SynInjectionWin32FailureFailsExplicitlyWithoutReset()
    {
        // A first-SYN injection failure surfaces through the same structured warn exit
        // (reason=injectionFailure with the native error) and releases the fresh session; no
        // reset is crafted because no server ISN was ever observed.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector(throwOnCall: 1, exception: new Win32Exception(87));
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider(), logger);

        Assert.Equal(TcpRedirectOutcome.Blocked, await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None));

        var failure = Assert.Single(logger.Events, item => string.Equals(item.Name, "tcp.redirect.failed", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Warn, failure.Level);
        Assert.Contains(failure.Fields, field => string.Equals(field.Key, "reason", StringComparison.Ordinal) && field.Value is "injectionFailure");
        Assert.Contains(failure.Fields, field => string.Equals(field.Key, "nativeError", StringComparison.Ordinal) && field.Value is 87);
        Assert.Contains(failure.Fields, field => string.Equals(field.Key, "adapterHandle", StringComparison.Ordinal) && field.Value is 0x1234L);

        Assert.Empty(injector.InjectedFrames);
        Assert.Equal(0, table.Count);
        Assert.True(Assert.Single(listenerFactory.Listeners).IsDisposed);
        var straggler = MakeForwardTcpPacket(s_clientIpv4, s_destIpv4, 53000, 443, TcpFlagAck);
        Assert.Equal(TcpRedirectOutcome.Dropped, await coordinator.HandlePacketAsync(straggler, s_server, CancellationToken.None));
    }

    [Fact]
    public async Task LateForwardPacketAfterTeardownIsDroppedWithinGrace()
    {
        // TIME_WAIT grace: after the relay completes and the redirect alias is removed, the
        // client's straggler ACK on the original tuple is consumed as Dropped — passing it toward
        // the real server would draw a bounced RST back into the client's finished connection.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var relayFactory = new CompletableRelayFactory();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider());

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));
        var listener = Assert.Single(listenerFactory.Listeners);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(s_destIpv4, 53000)), CancellationToken.None);
        await WaitForAsync(() => relayFactory.Relay is not null);

        relayFactory.Relay!.Complete();
        await WaitForAsync(() => table.Count == 0);

        injector.InjectedFrames.Clear();
        var straggler = MakeForwardTcpPacket(s_clientIpv4, s_destIpv4, 53000, 443, TcpFlagAck);
        var outcome = await coordinator.HandlePacketAsync(straggler, s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Dropped, outcome);
        Assert.Empty(injector.InjectedFrames);
    }

    [Fact]
    public async Task LateReversePacketAfterTeardownIsDroppedWithinGrace()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var relayFactory = new CompletableRelayFactory();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider());

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));
        var listener = Assert.Single(listenerFactory.Listeners);
        var listenerTuple = listener.TranslatedTuple;
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(s_destIpv4, 53000)), CancellationToken.None);
        await WaitForAsync(() => relayFactory.Relay is not null);

        relayFactory.Relay!.Complete();
        await WaitForAsync(() => table.Count == 0);

        injector.InjectedFrames.Clear();
        var straggler = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerTuple.Port, s_destIpv4, 53000, mutateFrame: f => f[47] = TcpFlagFinAck);
        var outcome = await coordinator.HandleReverseIfApplicableAsync(straggler, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Dropped, outcome);
        Assert.Empty(injector.InjectedFrames);
    }

    [Fact]
    public async Task DispatcherConsumesReverseStragglerAsProxyConsumedWithoutPolicyLabel()
    {
        // Through the wired dispatcher: a reverse straggler of a torn-down redirect completes as a
        // proxy-consumed packet — no reinjection, and no policy-drop mislabel from BlockAsync.
        var harness = CreateDispatcherHarness();
        await using var coordinator = harness.Coordinator;
        await EstablishRelayingSessionAsync(harness);
        var listenerTuple = Assert.Single(harness.ListenerFactory.Listeners).TranslatedTuple;

        harness.RelayFactory.Relay!.Complete();
        await WaitForAsync(() => harness.Table.Count == 0);
        harness.Injector.InjectedFrames.Clear();

        var straggler = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerTuple.Port, s_destIpv4, 53000, mutateFrame: f => f[47] = TcpFlagFinAck);
        await harness.Dispatcher.DispatchAsync(straggler, CancellationToken.None);

        Assert.Equal(PacketDisposition.ProxyConsumed, straggler.Lease.Disposition);
        Assert.Empty(harness.Injector.InjectedFrames);
        Assert.Contains(harness.Logger.Events, e => string.Equals(e.Name, "packet.reverseHandled", StringComparison.Ordinal)
            && e.Fields.Any(field => string.Equals(field.Key, "outcome", StringComparison.Ordinal) && field.Value is TcpRedirectOutcome.Dropped));
        Assert.DoesNotContain(harness.Logger.Events, e => string.Equals(e.Name, "packet.dropped", StringComparison.Ordinal)
            && e.Fields.Any(field => string.Equals(field.Key, "reason", StringComparison.Ordinal) && field.Value is "policy"));
    }

    [Fact]
    public async Task LatePacketFallsBackToNotRelevantAfterGraceExpiry()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var relayFactory = new CompletableRelayFactory();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider());

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));
        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        await Assert.Single(listenerFactory.Listeners).AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(s_destIpv4, 53000)), CancellationToken.None);
        await WaitForAsync(() => relayFactory.Relay is not null);
        relayFactory.Relay!.Complete();
        await WaitForAsync(() => table.Count == 0);

        // Fast-forward past the grace window by overwriting the tombstone with an elapsed expiry.
        coordinator.Tombstones.TryAdd(MakeHostFlowKey(), Endpoint.From(s_clientIpv4, listenerTuple.Port), Endpoint.From(s_destIpv4, 53000), DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));

        var forwardStraggler = MakeForwardTcpPacket(s_clientIpv4, s_destIpv4, 53000, 443, TcpFlagAck);
        Assert.Equal(TcpRedirectOutcome.NotRelevant, await coordinator.HandlePacketAsync(forwardStraggler, s_server, CancellationToken.None));
        var reverseStraggler = MakeReversePacketClassifierOrientation(s_clientIpv4, listenerTuple.Port, s_destIpv4, 53000, mutateFrame: f => f[47] = TcpFlagAck);
        Assert.Equal(TcpRedirectOutcome.NotRelevant, await coordinator.HandleReverseIfApplicableAsync(reverseStraggler, CancellationToken.None));
    }

    [Fact]
    public async Task RelaySetupFailureTombstonesTheFlow()
    {
        // Every teardown entry point writes the tombstone, including the relay-setup-failure path.
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

        var straggler = MakeForwardTcpPacket(s_clientIpv4, s_destIpv4, 53000, 443, TcpFlagAck);
        Assert.Equal(TcpRedirectOutcome.Dropped, await coordinator.HandlePacketAsync(straggler, s_server, CancellationToken.None));
    }

    [Fact]
    public async Task ExecutorSilentlyConsumesTombstoneHitWithTraceAndNoReinjection()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var relayFactory = new CompletableRelayFactory();
        var logger = new RecordingRuntimeLogger();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider(), logger);

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));
        var listener = Assert.Single(listenerFactory.Listeners);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(s_destIpv4, 53000)), CancellationToken.None);
        await WaitForAsync(() => relayFactory.Relay is not null);
        relayFactory.Relay!.Complete();
        await WaitForAsync(() => table.Count == 0);

        var reinjector = new CountingReinjector();
        var executor = new NdisPacketActionExecutor(reinjector, logger, tcpProxy: coordinator);
        var straggler = MakeForwardTcpPacket(s_clientIpv4, s_destIpv4, 53000, 443, TcpFlagAck);

        await executor.ProxyAsync(straggler, s_server, CancellationToken.None);

        Assert.Equal(0, reinjector.SendToAdapterCount);
        Assert.Equal(0, reinjector.SendToMstcpCount);
        Assert.Contains(logger.Events, e => string.Equals(e.Name, "tcp.packet.handled", StringComparison.Ordinal)
            && e.Fields.Any(field => string.Equals(field.Key, "outcome", StringComparison.Ordinal) && field.Value is TcpRedirectOutcome.Dropped));
        Assert.Contains(logger.Events, e => string.Equals(e.Name, "packet.dropped", StringComparison.Ordinal)
            && e.Fields.Any(field => string.Equals(field.Key, "reason", StringComparison.Ordinal) && field.Value is "grace"));
        // The grace drop must not emit its own packet.completed: the dispatcher owns that event and
        // logs it exactly once per packet after the disposition executes.
        Assert.DoesNotContain(logger.Events, e => string.Equals(e.Name, "packet.completed", StringComparison.Ordinal)
            && e.Fields.Any(field => string.Equals(field.Key, "outcome", StringComparison.Ordinal)));
        // Dropped must not trip the rate-limited proxy-unavailable warning (Blocked's side effect).
        Assert.DoesNotContain(logger.Lines, line => line.Message.Contains("proxy relay support", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HoldsFlowTracksSessionAndGraceWindow()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var relayFactory = new CompletableRelayFactory();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider());

        var key = MakeHostFlowKey();
        Assert.False(coordinator.HoldsFlow(key));

        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None));
        Assert.True(coordinator.HoldsFlow(key));

        var listener = Assert.Single(listenerFactory.Listeners);
        var listenerTuple = listener.TranslatedTuple;
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(s_destIpv4, 53000)), CancellationToken.None);
        await WaitForAsync(() => relayFactory.Relay is not null);
        Assert.True(coordinator.HoldsFlow(key));

        relayFactory.Relay!.Complete();
        await WaitForAsync(() => table.Count == 0);
        Assert.True(coordinator.HoldsFlow(key));

        // Once the grace window lapses, the hold releases and an unrelated flow never held.
        coordinator.Tombstones.TryAdd(key, Endpoint.From(s_clientIpv4, listenerTuple.Port), Endpoint.From(s_destIpv4, 53000), DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        Assert.False(coordinator.HoldsFlow(key));
        var unrelated = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.99"), 53001), Endpoint.From(s_destIpv4, 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        Assert.False(coordinator.HoldsFlow(unrelated));
    }

    [Fact]
    public async Task HeldFlowExpiresAtOriginalIdlePointAfterGraceLapses()
    {
        // The wired sweep sequence (tcp first, then flows with the hold predicate): a torn-down
        // session's flow decision is held by the grace tombstone, then removed at its original
        // idle point once the grace lapses — without the idle window being re-armed.
        var harness = CreateDispatcherHarness();
        await using var coordinator = harness.Coordinator;
        await EstablishRelayingSessionAsync(harness);
        var key = MakeHostFlowKey();
        Assert.True(coordinator.HoldsFlow(key));

        harness.RelayFactory.Relay!.Complete();
        await WaitForAsync(() => harness.Table.Count == 0);

        // The tcp sweep uses the real clock (the live tombstone must survive it); only the flow
        // sweep runs with a future clock to force the flow's idle boundary to have elapsed.
        var flowSweepNow = DateTimeOffset.UtcNow.AddMinutes(10);
        Assert.Equal(0, await coordinator.RemoveExpiredAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, harness.Dispatcher.RemoveExpiredFlows(flowSweepNow, TimeSpan.FromMinutes(1), coordinator.HoldsFlow));

        var listenerTuple = Assert.Single(harness.ListenerFactory.Listeners).TranslatedTuple;
        coordinator.Tombstones.TryAdd(key, Endpoint.From(s_clientIpv4, listenerTuple.Port), Endpoint.From(s_destIpv4, 53000), DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        Assert.Equal(1, harness.Dispatcher.RemoveExpiredFlows(flowSweepNow, TimeSpan.FromMinutes(1), coordinator.HoldsFlow));
    }

    [Fact]
    public async Task CapacityRejectedSynInjectsSingleRstPerTuplePerCooldownWindow()
    {
        // S4: the client is still in SYN_SENT, so the RST|ACK (ack = ISN+1) aborts it with
        // ECONNREFUSED; retransmissions inside the 1s window stay silent so a spoofed-source
        // flood cannot use the proxy as a reflection amplifier.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var logger = new RecordingRuntimeLogger();
        var table = new TcpRedirectTable(capacity: 1);
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider(), logger, capacity: 1);

        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None));
        var rejectedTuple = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.11"), 53001), Endpoint.From(IPAddress.Parse("192.0.2.99"), 80), TransportProtocol.Tcp, FlowOriginKind.Host);
        var rejectedSyn = MakeSynPacketWithSequence(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80, 0x11223344);

        Assert.Equal(TcpRedirectOutcome.Blocked, await coordinator.HandleSynAsync(rejectedSyn, s_server, CancellationToken.None));

        // Frame 1 is the first flow's rewritten SYN; frame 2 is the capacity RST.
        Assert.Equal(2, injector.InjectedFrames.Count);
        var reset = injector.InjectedFrames[1];
        Assert.True(reset.TowardMstcp);
        Assert.Equal(0x14, reset.Frame[47]);
        Assert.Equal(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(reset.Frame.AsSpan(38, 4)));
        Assert.Equal(0x11223345u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(reset.Frame.AsSpan(42, 4)));
        Assert.Contains(logger.Events, e => string.Equals(e.Name, "tcp.redirect.capacityReset", StringComparison.Ordinal));

        // A retransmitted SYN inside the cooldown window is still consumed (Blocked) and draws no
        // second reset.
        Assert.Equal(TcpRedirectOutcome.Blocked, await coordinator.HandleSynAsync(MakeSynPacketWithSequence(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80, 0x11223345), s_server, CancellationToken.None));
        Assert.Equal(2, injector.InjectedFrames.Count);

        // Once the window lapses, the next SYN earns a fresh reset.
        Assert.True(coordinator.CapacityResetCooldowns.Remove(rejectedTuple));
        Assert.Equal(TcpRedirectOutcome.Blocked, await coordinator.HandleSynAsync(MakeSynPacketWithSequence(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80, 0x11223346), s_server, CancellationToken.None));
        Assert.Equal(3, injector.InjectedFrames.Count);
        Assert.Equal(0x14, injector.InjectedFrames[2].Frame[47]);

        // The rejection accounting is unchanged.
        Assert.Equal(3, coordinator.CapacityRejectionCount);
        Assert.Contains(logger.Events, e => string.Equals(e.Name, "tcp.redirect.rejected", StringComparison.Ordinal)
            && e.Fields.Any(field => string.Equals(field.Key, "reason", StringComparison.Ordinal) && field.Value is "capacity"));
    }

    [Fact]
    public async Task CapacityResetFollowsOriginDirectionMatrix()
    {
        // Host shape injects the reset toward MSTCP; a forwarded flow's reset returns to the
        // origin adapter (the adapter the SYN was captured on) — same matrix as the redirect
        // and client-reset legs.
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable(capacity: 1);
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider(), capacity: 1);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None));

        var hostSyn = MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80);
        Assert.Equal(TcpRedirectOutcome.Blocked, await coordinator.HandleSynAsync(hostSyn, s_server, CancellationToken.None));
        var hostReset = injector.InjectedFrames[1];
        Assert.True(hostReset.TowardMstcp);
        Assert.Equal((nint)0x1234, hostReset.AdapterHandle);

        injector.InjectedFrames.Clear();
        var forwardedSyn = MakeForwardedSynPacket(IPAddress.Parse("192.0.2.12"), IPAddress.Parse("192.0.2.99"), 53002, 80);
        Assert.Equal(TcpRedirectOutcome.Blocked, await coordinator.HandleSynAsync(forwardedSyn, s_server, CancellationToken.None));
        var forwardedReset = injector.InjectedFrames[0];
        Assert.False(forwardedReset.TowardMstcp);
        Assert.Equal((nint)0x1234, forwardedReset.AdapterHandle);
        Assert.Equal(0x14, forwardedReset.Frame[47]);
    }

    [Fact]
    public async Task CapacityResetInjectionFailureWarnsWithoutChangingOutcome()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector(throwOnCall: 2, exception: new System.ComponentModel.Win32Exception(87));
        var selfTraffic = new SelfTrafficRegistry();
        var logger = new RecordingRuntimeLogger();
        var table = new TcpRedirectTable(capacity: 1);
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, new FakeLocalAddressProvider(), logger, capacity: 1);

        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None));
        Assert.Equal(TcpRedirectOutcome.Blocked, await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80), s_server, CancellationToken.None));

        // The best-effort reset never fails the rejection path: warned, no throw, and the tuple's
        // cooldown claim still holds (the retransmission draws nothing).
        Assert.Contains(logger.Lines, line => line.Message.Contains("capacity reset injection failed", StringComparison.Ordinal));
        Assert.Equal(TcpRedirectOutcome.Blocked, await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80), s_server, CancellationToken.None));
        Assert.Single(injector.InjectedFrames);
    }

    private static CapturedFlowPacket MakeSynPacketWithSequence(IPAddress client, IPAddress destination, ushort clientPort, ushort destinationPort, uint sequence)
    {
        var frame = FrameBuilders.BuildIpv4TcpFrame(client, destination, clientPort, destinationPort, FrameBuilders.TcpFlagSyn, sequence: sequence);
        var key = FlowKey.Create(Endpoint.From(client, clientPort), Endpoint.From(destination, destinationPort), TransportProtocol.Tcp, FlowOriginKind.Host);
        var context = new FlowContext(key, "app.exe", null, null, "eth0", destinationPort);
        return new CapturedFlowPacket(new PacketLease(frame), context, new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 0x1234));
    }
}
