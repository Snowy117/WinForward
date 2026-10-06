using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FlowBuilders;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The UoT transport's whole flow lifecycle driven through the coordinator, the session, and the
/// scripted UoT server — the promises only a cross-layer fact can observe. A refused establishment
/// leaves the flow removed with the setup cooldown armed and is never mistaken for an association
/// loss; a connection that dies mid-flow is an association loss that re-establishes on the flow's
/// next datagram with a fresh connection and no cooldown; one flow owns one connection, so two
/// concurrent flows keep two connections and two distinct alias claims; and a reset stream tears the
/// flow down instead of being swallowed as the per-datagram <c>ConnectionReset</c> skip the session's
/// receive loop would spin on (design §6).
/// <para>
/// The class joins the association-lost counter collection: three of its facts read that
/// process-wide counter as an exact delta (one death is exactly one count, a rejection is none), so
/// no sibling collection may move it while they run.
/// </para>
/// </summary>
[Collection(UdpAssociationLostCounterCollection.Name)]
public sealed class UdpUotFlowLifecycleTests
{
    private const int PayloadLength = 16;

    private static readonly TimeSpan s_budget = TimeSpan.FromSeconds(30);

    /// <summary>The frame the scripted server injects as a flow's answer; the sink's copy is the establishment proof.</summary>
    private static readonly byte[] s_answer = "ANSWER"u8.ToArray();

    /// <summary>
    /// R4/design §7: a CONNECT the server refuses is discovered on the receive path after the
    /// pipelined flight, and the flow must leave as the setup failure the cooldown exists for —
    /// never as a counted association loss, which would re-dial once per client retransmit.
    /// </summary>
    [Fact]
    public async Task ARefusedConnectReplyIsASetupFailureThatArmsTheCooldown()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var logger = new RecordingLogger();
        await using var server = new ScriptedSocks5UotServer(connectReplyStatus: 5);
        var target = server.Server;
        await using var coordinator = CreateCoordinator(new NoopResponseSink(), new UdpProxyOptions { Capacity = 16, TimeProvider = time, Logger = logger }, logger);
        var flow = CreateFlow("192.0.2.53");
        var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);

        // Admitted and buffered while the flow dials; the refusal arrives only after the server has
        // read the pipelined first datagram, so the frame count is the R2 observation at this layer.
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(target), new byte[PayloadLength], default, CancellationToken.None));
        await WaitForAsync(() => coordinator.SessionCount == 0);
        Assert.Equal(1, server.FrameCount);

        // The classification discriminates: the rejection armed the cooldown, and it is not an
        // association loss, so nothing was counted for it.
        Assert.Equal(0, RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) - lostBefore);
        await AssertCooldownContractAsync(coordinator, flow, target, server, time, logger);
    }

    /// <summary>
    /// The sibling refusal shape R4 names: the server selects the credential method and refuses the
    /// presented pair, so the rejection is the method/auth exchange rather than the CONNECT reply.
    /// It must land on the same setup-failure contract — the refusal is a cooldown, not a loss —
    /// whether the receive path or the setup flush's fail-closed send discovers it first.
    /// </summary>
    [Fact]
    public async Task ARefusedCredentialExchangeIsASetupFailureThatArmsTheCooldown()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var logger = new RecordingLogger();
        await using var server = new ScriptedSocks5UotServer(username: "user", password: "secret");
        var target = new Socks5Server("scripted", "127.0.0.1", checked((ushort)server.ControlEndpoint.Port), "user", "wrong", UdpOverTcp: true);
        await using var coordinator = CreateCoordinator(new NoopResponseSink(), new UdpProxyOptions { Capacity = 16, TimeProvider = time, Logger = logger }, logger);
        var flow = CreateFlow("192.0.2.53");
        var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);

        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(target), new byte[PayloadLength], default, CancellationToken.None));
        await WaitForAsync(() => coordinator.SessionCount == 0);

        Assert.Equal(0, RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) - lostBefore);
        await AssertCooldownContractAsync(coordinator, flow, target, server, time, logger);
    }

    /// <summary>
    /// R4/I5: a connection that ends after establishment is the flow's association death. The
    /// receive path records the transport's typed <c>UdpAssociationLostException</c>, the coordinator
    /// removes the slot as exactly one counted association loss with no setup cooldown, and the very
    /// next datagram establishes a fresh connection instead of waiting out a cooldown.
    /// </summary>
    [Fact]
    public async Task AMidFlowConnectionDeathIsAnAssociationLossThatReestablishesOnTheNextDatagram()
    {
        var logger = new RecordingLogger();
        var sink = new FakeResponseSink();
        await using var server = new ScriptedSocks5UotServer();
        await using var coordinator = CreateCoordinator(sink, new UdpProxyOptions { Capacity = 16, Logger = logger }, logger);
        var target = ProxyTarget.FromServer(server.Server);
        var flow = CreateFlow("192.0.2.53");
        var payload = new byte[PayloadLength];
        var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);

        var session = await EstablishAnsweredFlowAsync(coordinator, flow, target, server, sink, payload);
        Assert.Equal(1, server.ConnectionCount);

        server.DropConnection();
        await WaitForAsync(() => coordinator.SessionCount == 0);

        // The death the receive path recorded is the typed one (so the loop never treated it as a
        // skip), classified once: one count, no cooldown, and the slot gone.
        var lost = Assert.IsType<UdpAssociationLostException>(session.RecordedFault);
        Assert.NotNull(lost.InnerException);
        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) - lostBefore);
        Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);

        // The next datagram re-establishes immediately on a fresh connection, and the re-established
        // flow is served: the second frame reaches the server and its session drains to ready.
        Assert.True(await coordinator.TrySendSpanAsync(flow, target, payload, default, CancellationToken.None));
        await WaitForAsync(() => server is { ConnectionCount: 2, FrameCount: 2 });
        Assert.True(coordinator.SessionReadyForDiagnostics(flow));
        Assert.Equal(1, coordinator.SessionCount);
        Assert.Equal(1, server.LiveConnectionCount);
        Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);
    }

    /// <summary>
    /// R1/design §2: one flow, one connection. Two concurrent flows dial two connections, and both
    /// alias claims succeed — the claim is keyed on the transport's own endpoint pair, so a shared
    /// connection would claim one alias twice and tear the second flow down as a setup failure. The
    /// converse is the same fact read backwards: a second flow never shares a connection, which is
    /// why the pair stays unique and the <c>UdpSessionSetup</c> alias guard is satisfied.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentUotFlowsOwnTwoConnectionsAndBothAliasClaimsSucceed()
    {
        await using var server = new ScriptedSocks5UotServer();
        await using var coordinator = CreateCoordinator(new NoopResponseSink(), new UdpProxyOptions { Capacity = 16 }, new RecordingLogger());
        var target = ProxyTarget.FromServer(server.Server);
        var first = CreateFlow("192.0.2.53");
        var second = CreateFlow("192.0.2.54");

        Assert.True(await coordinator.TrySendSpanAsync(first, target, [1], default, CancellationToken.None));
        Assert.True(await coordinator.TrySendSpanAsync(second, target, [2], default, CancellationToken.None));
        await WaitForAsync(() => coordinator.SessionReadyForDiagnostics(first) && coordinator.SessionReadyForDiagnostics(second));

        Assert.Equal(2, coordinator.SessionCount);
        Assert.Equal(2, server.ConnectionCount);
        Assert.Equal(2, server.LiveConnectionCount);
        Assert.Equal(2, server.FrameCount);
        Assert.Contains(server.Frames, frame => frame.AsSpan().SequenceEqual([(byte)1]));
        Assert.Contains(server.Frames, frame => frame.AsSpan().SequenceEqual([(byte)2]));

        // Each flow's alias is its own TCP pair; a collision would have been refused (and counted)
        // as a setup failure instead of leaving two live, ready sessions behind.
        var firstAlias = Assert.IsType<UdpProxySession>(coordinator.SessionForDiagnostics(first)).Association.RelayAlias;
        var secondAlias = Assert.IsType<UdpProxySession>(coordinator.SessionForDiagnostics(second)).Association.RelayAlias;
        Assert.NotEqual(firstAlias, secondAlias);
        Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);

        // R1's ownership half: the connections are disposed with the flows, none outlives them.
        // ReSharper disable once DisposeOnUsingVariable // The explicit DisposeAsync is the act under test: the wait below reads the fixture's live-connection count only after the coordinator released both connections; the await using declaration only backstops assertion-failure paths.
        await coordinator.DisposeAsync();
        await WaitForAsync(() => server.LiveConnectionCount == 0);
    }

    /// <summary>
    /// Design §6's hazard, made observable: the session's receive loop treats a raw
    /// <c>ConnectionReset</c> as a one-datagram skip and keeps looping, which on a dead stream is an
    /// infinite spin. A reset mid-flow must therefore reach that loop already translated into the
    /// typed death — the session is removed, its recorded fault is the association loss, and no skip
    /// was ever counted for this flow (the rate-limited summary line is the skip counter's only
    /// observable, and the first skip on a session always emits it).
    /// </summary>
    [Fact]
    public async Task AConnectionResetTearsTheFlowDownInsteadOfSpinningOnSkipClassResets()
    {
        var logger = new RecordingLogger();
        var sink = new FakeResponseSink();
        await using var server = new ScriptedSocks5UotServer();
        await using var coordinator = CreateCoordinator(sink, new UdpProxyOptions { Capacity = 16, Logger = logger }, logger);
        var target = ProxyTarget.FromServer(server.Server);
        var flow = CreateFlow("192.0.2.53");
        var payload = new byte[PayloadLength];
        var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);

        var session = await EstablishAnsweredFlowAsync(coordinator, flow, target, server, sink, payload);

        server.DropConnection(reset: true);
        await WaitForAsync(() => coordinator.SessionCount == 0);

        // The loop exited on the typed death instead of skipping: the reset never reached it as a
        // skip-class anomaly, exactly one loss was counted, and no cooldown was armed.
        var lost = Assert.IsType<UdpAssociationLostException>(session.RecordedFault);
        Assert.NotNull(lost.InnerException);
        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) - lostBefore);
        Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);
        Assert.DoesNotContain(logger.Lines, line => line.Message.Contains("skipped datagrams", StringComparison.Ordinal));
        await WaitForAsync(() => server.LiveConnectionCount == 0);
    }

    /// <summary>
    /// The cooldown contract a refused UoT establishment leaves behind, asserted the way batch 2's
    /// classification facts assert it: the tombstone is armed, the next datagram is refused
    /// fail-closed at the frozen clock without a new dial (a systematically refusing server is
    /// re-dialed once per cooldown, not once per client retransmit), and the flow sets up again once
    /// the one-second window elapses.
    /// </summary>
    private static async Task AssertCooldownContractAsync(
        UdpProxyCoordinator coordinator,
        FlowKey flow,
        Socks5Server target,
        ScriptedSocks5UotServer server,
        MutableTimeProvider time,
        RecordingLogger logger)
    {
        Assert.Equal(1, coordinator.Diagnostics.SetupCooldownCount);
        Assert.False(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(target), new byte[PayloadLength], default, CancellationToken.None));
        Assert.Equal(1, server.ConnectionCount);
        Assert.Contains(logger.Events, item => string.Equals(item.Name, "udp.setup.cooldown", StringComparison.Ordinal));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(target), new byte[PayloadLength], default, CancellationToken.None));
        await WaitForAsync(() => server.ConnectionCount == 2);
    }

    /// <summary>
    /// Drives one UoT flow through the coordinator until it is established and answered, and returns
    /// the live session so the caller can read its recorded fault after the death. The injected frame
    /// reaching the sink is the establishment proof: it can only be decoded after the deferred
    /// handshake and the CONNECT reply were consumed, so a death observed afterwards is a mid-flow
    /// death rather than a rejection discovered during setup.
    /// </summary>
    private static async Task<UdpProxySession> EstablishAnsweredFlowAsync(
        UdpProxyCoordinator coordinator,
        FlowKey flow,
        ProxyTarget target,
        ScriptedSocks5UotServer server,
        FakeResponseSink sink,
        byte[] payload)
    {
        Assert.True(await coordinator.TrySendSpanAsync(flow, target, payload, default, CancellationToken.None));
        await server.InjectFrameAsync(s_answer);

        using var budget = new CancellationTokenSource(s_budget);
        var (answeredFlow, source, answered, _) = await sink.Responses.Reader.ReadAsync(budget.Token);
        Assert.Equal(flow, answeredFlow);
        // Connect mode carries no on-wire source: the transport declares the flow's own destination,
        // which is what keeps the session's source check and the reinjector unchanged (design §6).
        Assert.Equal(flow.Remote, source);
        Assert.Equal(s_answer, answered);

        var session = coordinator.SessionForDiagnostics(flow);
        Assert.NotNull(session);
        return session;
    }

    private static UdpProxyCoordinator CreateCoordinator(IUdpResponseSink sink, UdpProxyOptions options, RecordingLogger logger) =>
        UdpCoordinatorFakes.CreateCoordinator(
            new Socks5UdpTransportFactory(new SelfTrafficRegistry(), UdpFrameBuilder.DefaultMaximumEthernetFrame, logger: logger),
            sink,
            options);
}
