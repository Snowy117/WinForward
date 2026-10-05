using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.Socks5;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FlowBuilders;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The local transport: one socket per flow, the payload forwarded verbatim to the configured
/// endpoint, and the flow's own destination declared as the reply's source so the session's
/// ownership check and the response reinjector work unchanged. Everything here that depends on the
/// socket path runs against real loopback sockets, because the properties under test (a distinct
/// local endpoint per flow, a reply visible only to its own flow's socket, an applied SIO posture)
/// are socket-level facts a fake cannot show. The class joins
/// <see cref="UdpLocalTargetCounterCollection"/> because several of its facts assert exact
/// <c>udpLocalTargetFailures</c> deltas.
/// </summary>
[Collection(UdpLocalTargetCounterCollection.Name)]
public sealed class LocalUdpTransportTests
{
    private static readonly IPAddress s_flowDestination = IPAddress.Parse("192.0.2.53");
    private const int MaximumFrameSize = 1_514;
    private static readonly NativeBufferPool s_receiveWindowPool = new(UdpProxyCoordinator.ReceiveWindowSize(MaximumFrameSize));

    [Fact]
    public async Task PayloadIsForwardedVerbatimAndTheReplyCarriesTheFlowDestinationAsItsSource()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder();
        var registry = new SelfTrafficRegistry();
        await using var transport = await CreateTransportAsync(responder.Endpoint, registry);
        var destination = Endpoint.From(s_flowDestination, 53);
        var payload = "dns-query"u8.ToArray();

        await transport.SendSpanAsync(destination, payload, sendTimeout.Token);

        await WaitForAsync(() => responder.ReceivedCount == 1);
        // Verbatim: the local hop adds no header and rewrites nothing.
        Assert.Equal(payload, responder.LastPayload);

        var received = await transport.ReceiveAsync(new byte[MaximumFrameSize], sendTimeout.Token);

        Assert.True(received.HasDatagram);
        Assert.Equal((IPAddressValue?)s_flowDestination, received.Datagram.SourceAddress);
        Assert.Equal((ushort)53, received.Datagram.SourcePort);
        Assert.Null(received.Datagram.SourceDomain);
        Assert.Equal(payload, received.Datagram.Payload.ToArray());
        Assert.Equal(1, ((IUdpExchangeCounters)transport).DatagramsSent);
        Assert.True(((IUdpExchangeCounters)transport).SawResponse);
    }

    [Fact]
    public async Task ReplyFromAnUnconfiguredSourceIsSkippedAsUnexpectedSource()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder();
        using var intruder = new LoopbackUdpSender();
        var registry = new SelfTrafficRegistry();
        await using var transport = await CreateTransportAsync(responder.Endpoint, registry);

        await transport.SendSpanAsync(Endpoint.From(s_flowDestination, 53), [1, 2, 3], sendTimeout.Token);
        var expected = await transport.ReceiveAsync(new byte[MaximumFrameSize], sendTimeout.Token);
        Assert.True(expected.HasDatagram);
        Assert.Equal(new byte[] { 1, 2, 3 }, expected.Datagram.Payload.ToArray());

        // A datagram from another port reaches the same socket: the target is one specific host, so
        // anything that is not exactly it is skipped rather than believed.
        // ReSharper disable once UseUtf8StringLiteral // The three bytes are an arbitrary non-text wire payload and the send helper takes byte[]: the suggested u8 literal is a ReadOnlySpan<byte> and cannot bind that parameter.
        await intruder.SendToAsync(transport.LocalEndpoint, [9, 9, 9], sendTimeout.Token);
        var skipped = await transport.ReceiveAsync(new byte[MaximumFrameSize], sendTimeout.Token);
        Assert.False(skipped.HasDatagram);
        Assert.Equal(UdpTransportSkipReason.UnexpectedSource, skipped.SkipReason);

        // The configured endpoint is still accepted afterwards: a skip is one datagram, not a state.
        await transport.SendSpanAsync(Endpoint.From(s_flowDestination, 53), [4, 5], sendTimeout.Token);
        var accepted = await transport.ReceiveAsync(new byte[MaximumFrameSize], sendTimeout.Token);
        Assert.True(accepted.HasDatagram);
        Assert.Equal(new byte[] { 4, 5 }, accepted.Datagram.Payload.ToArray());
        // The foreign datagram never reached a session: the skip stays inside this transport, so no
        // reply-ownership observation can be produced for it.
    }

    [Fact]
    public void SourceValidationIsExactOnAddressPortAndFamily()
    {
        var expected = new IPEndPoint(IPAddress.Parse("192.0.2.53"), 53);

        Assert.True(LocalUdpTransport.IsAcceptableLocalSource(new IPEndPoint(IPAddress.Parse("192.0.2.53"), 53), expected));
        Assert.False(LocalUdpTransport.IsAcceptableLocalSource(new IPEndPoint(IPAddress.Parse("192.0.2.54"), 53), expected));
        Assert.False(LocalUdpTransport.IsAcceptableLocalSource(new IPEndPoint(IPAddress.Parse("192.0.2.53"), 54), expected));
        Assert.False(LocalUdpTransport.IsAcceptableLocalSource(new IPEndPoint(IPAddress.Parse("2001:db8::53"), 53), expected));
        Assert.False(LocalUdpTransport.IsAcceptableLocalSource(new IPEndPoint(IPAddress.Loopback, 53), expected));
        Assert.False(LocalUdpTransport.IsAcceptableLocalSource(new DnsEndPoint("192.0.2.53", 53), expected));

        // The local rule is strictly tighter than the relay's port + family rule: the same port on
        // another address of the same family is a relay-legal reply and a local-target skip.
        var anotherAddressSamePort = new IPEndPoint(IPAddress.Parse("192.0.2.99"), 53);
        Assert.True(Socks5UdpTransport.IsAcceptableRelaySource(anotherAddressSamePort, expected));
        Assert.False(LocalUdpTransport.IsAcceptableLocalSource(anotherAddressSamePort, expected));
    }

    [Fact]
    public async Task AReplyWhoseDeclaredSourceIsNotTheFlowsDestinationIsCountedByTheR2Counter()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder();
        var registry = new SelfTrafficRegistry();
        var transport = await CreateTransportAsync(responder.Endpoint, registry);
        var sink = new FakeResponseSink();
        var flow = CreateFlow("192.0.2.53");
        await using var session = CreateSession(flow, transport, sink);
        session.Start(static _ => { });

        // The transport declares the destination it was last given as the reply's source, so sending a
        // destination this session's flow does not own is contract drift — and the R2 counter is what
        // makes the drift visible instead of letting the reply pass as the flow's own.
        var drifted = Endpoint.From(s_flowDestination, 54);
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch);
        Assert.True(await session.SendSpanAsync(drifted, [0x5a], sendTimeout.Token));

        var (responseFlow, remoteSource, payload, _) = await sink.Responses.Reader.ReadAsync(sendTimeout.Token);

        Assert.Equal(flow, responseFlow);
        Assert.Equal(drifted, remoteSource);
        Assert.Equal(0x5a, Assert.Single(payload));
        // The counter is process-wide and this suite runs beside others that legitimately move it, so
        // the delta is bounded below; the per-reply accounting exactly is UdpResponseSourceMismatchTests'.
        Assert.True(RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch) - before >= 1);
    }

    [Fact]
    public async Task AnUnsolicitedDatagramBeforeTheFirstSendIsSkippedAndNeverReachesTheResponseSink()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder();
        var registry = new SelfTrafficRegistry();
        var transport = await CreateTransportAsync(responder.Endpoint, registry);

        // The socket is bound from construction, so a datagram can reach it before this flow ever sent
        // one. There is no destination to declare as its source yet, so it is skipped at the transport.
        await responder.SendToAsync(transport.LocalEndpoint, [0xaa, 0xbb], sendTimeout.Token);
        var skipped = await transport.ReceiveAsync(new byte[MaximumFrameSize], sendTimeout.Token);
        Assert.False(skipped.HasDatagram);
        Assert.Equal(UdpTransportSkipReason.UnexpectedSource, skipped.SkipReason);

        // The same shape against a live session: the loop is parked on the transport, so the datagram
        // is processed before any send. The session's own skip summary is the deterministic proof that
        // it went through the receive path and was skipped — the responder is the configured endpoint,
        // so only the missing destination can skip it — and nothing reached the sink.
        var sink = new FakeResponseSink();
        var logger = new RecordingRuntimeLogger();
        var flow = CreateFlow("192.0.2.53");
        await using var session = CreateSession(flow, transport, sink, logger);
        session.Start(static _ => { });
        var mismatchBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch);
        await responder.SendToAsync(transport.LocalEndpoint, [0xaa, 0xbb], sendTimeout.Token);

        await WaitForAsync(() => logger.Lines.Any(line => line.Level == RuntimeLogLevel.Debug && line.Message.Contains("unexpectedSource=1", StringComparison.Ordinal)));
        Assert.False(sink.Responses.Reader.TryRead(out _));

        // The session's own exchange is the only thing that reaches the sink: the unsolicited datagram
        // was never injected with the default endpoint (a bogus frame toward the client).
        Assert.True(await session.SendSpanAsync(Endpoint.From(s_flowDestination, 53), [0x11], sendTimeout.Token));
        var (responseFlow, remoteSource, payload, _) = await sink.Responses.Reader.ReadAsync(sendTimeout.Token);

        Assert.Equal(flow, responseFlow);
        Assert.Equal(flow.Remote, remoteSource);
        Assert.Equal(new byte[] { 0x11 }, payload);
        Assert.False(sink.Responses.Reader.TryRead(out _));
        Assert.Equal(0, RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch) - mismatchBefore);
    }

    [Fact]
    public async Task TwoFlowsShareNeitherSocketNorSelfTrafficTuple()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var firstResponder = new LoopbackUdpResponder();
        await using var secondResponder = new LoopbackUdpResponder();
        var registry = new SelfTrafficRegistry();
        var factory = new LocalUdpTransportFactory(registry, MaximumFrameSize);
        await using var second = (LocalUdpTransport)await factory.CreateAsync(Local("dns-b", secondResponder.Endpoint), sendTimeout.Token);

        FlowKey firstTuple;
        FlowKey secondTuple;
        await using (var first = (LocalUdpTransport)await factory.CreateAsync(Local("dns-a", firstResponder.Endpoint), sendTimeout.Token))
        {
            Assert.NotEqual(first.LocalEndpoint.Port, second.LocalEndpoint.Port);
            Assert.NotEqual(first.LocalEndpoint, second.LocalEndpoint);
            Assert.Equal(firstResponder.Endpoint.Port, first.PeerEndpoint.Port);
            Assert.Equal(secondResponder.Endpoint.Port, second.PeerEndpoint.Port);

            // The tuples are read while both sockets are live: a disposed socket no longer answers for
            // its local endpoint, which is itself part of "the registration does not outlive the flow".
            firstTuple = ContextKeyFor(first);
            secondTuple = ContextKeyFor(second);
            Assert.True(registry.IsWildcardOwned(Context(firstTuple)));
            Assert.True(registry.IsWildcardOwned(Context(secondTuple)));
        }

        Assert.False(registry.IsWildcardOwned(Context(firstTuple)));
        Assert.True(registry.IsWildcardOwned(Context(secondTuple)));
    }

    [Fact]
    public async Task ASiblingFlowNeverObservesAnotherFlowsReply()
    {
        // The defect class this transport exists to remove: a reply delivered to a sibling flow's
        // session. Each flow owns its socket, so a datagram addressed to one flow's local endpoint is
        // unreachable from the other's transport — which the pending receive below proves.
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var firstResponder = new LoopbackUdpResponder(static payload => ReplyWithTrailer(payload, 0xa1));
        await using var secondResponder = new LoopbackUdpResponder(static payload => ReplyWithTrailer(payload, 0xb2));
        var registry = new SelfTrafficRegistry();
        var factory = new LocalUdpTransportFactory(registry, MaximumFrameSize);
        await using var first = await factory.CreateAsync(Local("dns-a", firstResponder.Endpoint), sendTimeout.Token);
        await using var second = await factory.CreateAsync(Local("dns-b", secondResponder.Endpoint), sendTimeout.Token);

        await first.SendSpanAsync(Endpoint.From(s_flowDestination, 53), [0x01], sendTimeout.Token);
        await WaitForAsync(() => firstResponder.ReceivedCount == 1 && secondResponder.ReceivedCount == 0);

        var firstReply = await first.ReceiveAsync(new byte[MaximumFrameSize], sendTimeout.Token);
        Assert.True(firstReply.HasDatagram);
        Assert.Equal(new byte[] { 0x01, 0xa1 }, firstReply.Datagram.Payload.ToArray());

        // The sibling's socket holds no datagram: its pending receive stays pending until the short
        // deadline, so serving the first flow's reply through the second flow's transport is
        // unrepresentable rather than merely unlikely.
        using var siblingWindow = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await second.ReceiveAsync(new byte[MaximumFrameSize], siblingWindow.Token));
        Assert.Equal(0, secondResponder.ReceivedCount);
    }

    [Fact]
    public async Task SiblingSessionsReceiveOnlyTheirOwnRepliesThroughTheCoordinator()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var firstResponder = new LoopbackUdpResponder(static payload => ReplyWithTrailer(payload, 0xa1));
        await using var secondResponder = new LoopbackUdpResponder(static payload => ReplyWithTrailer(payload, 0xb2));
        var registry = new SelfTrafficRegistry();
        var factory = new RecordingTransportFactory(new LocalUdpTransportFactory(registry, MaximumFrameSize));
        var sink = new FakeResponseSink();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, sink, new UdpProxyOptions { Capacity = 16, Logger = new RecordingRuntimeLogger() });
        var firstFlow = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53_000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var secondFlow = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.11"), 53_000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);

        Assert.True(await coordinator.TrySendSpanAsync(firstFlow, Local("dns-a", firstResponder.Endpoint), [0x11], default, sendTimeout.Token));
        Assert.True(await coordinator.TrySendSpanAsync(secondFlow, Local("dns-b", secondResponder.Endpoint), [0x22], default, sendTimeout.Token));

        // The two flows are independent, so their replies arrive in either order: key them by flow
        // rather than by read order, and assert each landed on its own session.
        var replies = new Dictionary<FlowKey, (Endpoint Source, byte[] Payload)>();
        for (var index = 0; index < 2; index++)
        {
            var (flow, source, payload, _) = await sink.Responses.Reader.ReadAsync(sendTimeout.Token);
            replies.Add(flow, (source, payload));
        }

        Assert.Equal(firstFlow.Remote, replies[firstFlow].Source);
        Assert.Equal(secondFlow.Remote, replies[secondFlow].Source);
        Assert.Equal(new byte[] { 0x11, 0xa1 }, replies[firstFlow].Payload);
        Assert.Equal(new byte[] { 0x22, 0xb2 }, replies[secondFlow].Payload);

        // Two flows, two sockets, and no third delivery.
        Assert.Equal(2, factory.Transports.Count);
        Assert.NotEqual(factory.Transports[0].LocalEndpoint.Port, factory.Transports[1].LocalEndpoint.Port);
        Assert.False(sink.Responses.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ASecondDatagramOnAReadyLocalSessionTakesTheWarmPath()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder();
        var factory = new RecordingTransportFactory(new LocalUdpTransportFactory(new SelfTrafficRegistry(), MaximumFrameSize));
        var sink = new FakeResponseSink();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, sink, new UdpProxyOptions { Capacity = 16 });
        var flow = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53_000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var target = Local("dns-in", responder.Endpoint);

        // The first datagram rides the cold setup path: it creates the flow's transport and flips the
        // slot ready.
        Assert.True(await coordinator.TrySendSpanAsync(flow, target, [0x01], default, sendTimeout.Token));
        await WaitForAsync(() => coordinator.SessionReadyForDiagnostics(flow));
        var (firstFlow, _, firstPayload, _) = await sink.Responses.Reader.ReadAsync(sendTimeout.Token);
        Assert.Equal(flow, firstFlow);
        Assert.Equal(new byte[] { 0x01 }, firstPayload);
        Assert.Equal(1, factory.CreateCalls);

        // The second must take the warm path — the same transport, no new setup, nothing queued. The
        // design's top risk is that a ready local flow stops resolving and pays setup per datagram.
        Assert.True(await coordinator.TrySendSpanAsync(flow, target, [0x02], default, sendTimeout.Token));
        var (secondFlow, secondSource, secondPayload, _) = await sink.Responses.Reader.ReadAsync(sendTimeout.Token);

        Assert.Equal(flow, secondFlow);
        Assert.Equal(flow.Remote, secondSource);
        Assert.Equal(new byte[] { 0x02 }, secondPayload);
        Assert.Equal(1, factory.CreateCalls);
        Assert.Single(factory.Transports);
        Assert.Equal(0, coordinator.Diagnostics.PendingSetupBytes);
        Assert.True(coordinator.SessionReadyForDiagnostics(flow));
    }

    [Fact]
    public async Task AFatalReceiveFaultOnAReadyLocalSessionRemovesItAndFreesTheCapacity()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder();
        TrackingSocket? socket = null;
        var factory = new RecordingTransportFactory(new LocalUdpTransportFactory(
            new SelfTrafficRegistry(),
            MaximumFrameSize,
            socketFactory: family => socket = new TrackingSocket(family)));
        var sink = new FakeResponseSink();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, sink, new UdpProxyOptions { Capacity = 1 });
        var target = Local("dns-in", responder.Endpoint);
        var firstFlow = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53_000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var secondFlow = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.11"), 53_000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);

        Assert.True(await coordinator.TrySendSpanAsync(firstFlow, target, [0x01], default, sendTimeout.Token));
        await WaitForAsync(() => coordinator.SessionReadyForDiagnostics(firstFlow) && socket is not null);

        // Kills the socket under the session's parked receive. The fault is not ConnectionReset, so it
        // is fatal: the session must reach the coordinator's receive-failure removal instead of holding
        // the only capacity slot with a dead transport.
        socket!.Dispose();

        await WaitForAsync(() => coordinator.SessionCount == 0);
        Assert.True(await coordinator.TrySendSpanAsync(secondFlow, target, [0x02], default, sendTimeout.Token));
        await WaitForAsync(() => coordinator.SessionReadyForDiagnostics(secondFlow));
        var (secondRemote, secondPayload) = await ReadResponseForAsync(sink, secondFlow, sendTimeout.Token);

        Assert.Equal(secondFlow.Remote, secondRemote);
        Assert.Equal(new byte[] { 0x02 }, secondPayload);
    }

    [Fact]
    public async Task AReceiveThatFillsTheWindowIsSkippedAsOversized()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder(static _ => new byte[64]);
        var registry = new SelfTrafficRegistry();
        await using var transport = await CreateTransportAsync(responder.Endpoint, registry);

        await transport.SendSpanAsync(Endpoint.From(s_flowDestination, 53), [0x01], sendTimeout.Token);
        var oversized = await transport.ReceiveAsync(new byte[16], sendTimeout.Token);
        Assert.False(oversized.HasDatagram);
        Assert.Equal(UdpTransportSkipReason.Oversized, oversized.SkipReason);

        await transport.SendSpanAsync(Endpoint.From(s_flowDestination, 53), [0x02], sendTimeout.Token);
        var whole = await transport.ReceiveAsync(new byte[128], sendTimeout.Token);
        Assert.True(whole.HasDatagram);
        Assert.Equal(64, whole.Datagram.Payload.Length);
    }

    [Fact]
    public async Task AnEmptyDatagramIsSkippedAsMalformed()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder(static _ => []);
        var registry = new SelfTrafficRegistry();
        await using var transport = await CreateTransportAsync(responder.Endpoint, registry);

        await transport.SendSpanAsync(Endpoint.From(s_flowDestination, 53), [0x01], sendTimeout.Token);
        await WaitForAsync(() => responder.ReceivedCount == 1);

        var empty = await transport.ReceiveAsync(new byte[MaximumFrameSize], sendTimeout.Token);
        Assert.False(empty.HasDatagram);
        Assert.Equal(UdpTransportSkipReason.Malformed, empty.SkipReason);
        Assert.False(((IUdpExchangeCounters)transport).SawResponse);
    }

    [Fact]
    public async Task TheFactoryRefusesASocks5Target()
    {
        var factory = new LocalUdpTransportFactory(new SelfTrafficRegistry(), MaximumFrameSize);
        var socks5 = ProxyTarget.FromServer(new Socks5Server("remote", "127.0.0.1", 1080, Username: null, Password: null));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () => await factory.CreateAsync(socks5, CancellationToken.None));
        Assert.Contains("remote", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCompositeFactoryDispatchesByKindAndRefusesAnUnshapedTarget()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder();
        var local = new RecordingTransportFactory(new LocalUdpTransportFactory(new SelfTrafficRegistry(), MaximumFrameSize));
        var socks5 = new StubTransportFactory(new FakeTransport(AddressFamily.InterNetwork, 40_100));
        var composite = new UdpTransportFactory(socks5, local);

        await using (await composite.CreateAsync(Local("dns-in", responder.Endpoint), sendTimeout.Token))
        {
            Assert.Equal(1, local.CreateCalls);
            Assert.Equal(0, socks5.CreateCalls);
        }

        await using (await composite.CreateAsync(ProxyTarget.FromServer(new Socks5Server("remote", "127.0.0.1", 1080, Username: null, Password: null)), sendTimeout.Token))
        {
            Assert.Equal(1, socks5.CreateCalls);
            Assert.Equal(1, local.CreateCalls);
        }

        var unshaped = new ProxyTarget("broken", Socks5: null, Local: null);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () => await composite.CreateAsync(unshaped, CancellationToken.None));
        Assert.Contains("broken", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedSendFailsClosedAndIsCounted()
    {
        await using var responder = new LoopbackUdpResponder();
        var registry = new SelfTrafficRegistry();
        await using var transport = await CreateTransportAsync(responder.Endpoint, registry);
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFailures);

        var refusal = await Assert.ThrowsAsync<IOException>(async () =>
            await transport.SendSpanAsync(Endpoint.From(s_flowDestination, 53), new byte[MaximumFrameSize + 1], CancellationToken.None));

        Assert.Contains("send buffer", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFailures) - before);
        Assert.Equal(0, ((IUdpExchangeCounters)transport).DatagramsSent);
        Assert.Equal(0, responder.ReceivedCount);
    }

    [Fact]
    public async Task ASendOnADeadSocketFailsClosedAndIsCounted()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder();
        var registry = new SelfTrafficRegistry();
        TrackingSocket? socket = null;
        await using var transport = (LocalUdpTransport)await new LocalUdpTransportFactory(registry, MaximumFrameSize, socketFactory: family => socket = new TrackingSocket(family))
            .CreateAsync(Local("dns-in", responder.Endpoint), sendTimeout.Token);
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFailures);

        // A socket that went away under the transport is the refused-send shape: the failure surfaces
        // to the caller that owns the flow's slot, is counted, and is never swallowed.
        socket!.Dispose();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await transport.SendSpanAsync(Endpoint.From(s_flowDestination, 53), [0x01, 0x02], CancellationToken.None));

        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFailures) - before);
        Assert.Equal(0, ((IUdpExchangeCounters)transport).DatagramsSent);
    }

    [Fact]
    public async Task ACancelledParkedReceiveIsNotCountedAsAFailure()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder();
        await using var transport = await CreateTransportAsync(responder.Endpoint, new SelfTrafficRegistry());
        using var parkedCancellation = new CancellationTokenSource();
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFailures);

        // The parked receive a session's scope cancellation ends: idle expiry and shutdown are the
        // normal end of a flow, so the cancellation must not be charged to the failure counter.
        var parked = transport.ReceiveAsync(new byte[MaximumFrameSize], parkedCancellation.Token).AsTask();
        await parkedCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parked);
        Assert.Equal(0, RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFailures) - before);
    }

    [Fact]
    public async Task AReceiveParkedAcrossSessionDisposalIsNotCountedAsAFailure()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder();
        var transport = await CreateTransportAsync(responder.Endpoint, new SelfTrafficRegistry());
        var sink = new FakeResponseSink();
        var flow = CreateFlow("192.0.2.53");
        var session = CreateSession(flow, transport, sink);
        session.Start(static _ => { });

        // The receive the session's loop parks, on the transport the session owns: disposal faults it,
        // and a faulted parked receive must not be charged to the flow.
        var parked = transport.ReceiveAsync(new byte[MaximumFrameSize], CancellationToken.None).AsTask();
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFailures);

        await session.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => parked);
        Assert.Equal(0, RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFailures) - before);
    }

    [Fact]
    public async Task ASingleFailedSendThroughASessionWithAParkedReceiveIsCountedExactlyOnce()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder();
        TrackingSocket? socket = null;
        var transport = (LocalUdpTransport)await new LocalUdpTransportFactory(new SelfTrafficRegistry(), MaximumFrameSize, socketFactory: family => socket = new TrackingSocket(family))
            .CreateAsync(Local("dns-in", responder.Endpoint), sendTimeout.Token);
        var flow = CreateFlow("192.0.2.53");
        var session = CreateSession(flow, transport, new FakeResponseSink());

        // The session's own receive loop cannot carry the park here: a fatal receive fault makes the
        // session refuse further sends by design, which would hide the accounting under test. The park
        // is therefore the one that loop would hold, on the transport the session owns, and the send
        // goes through the session so both its increments are reachable.
        var parked = transport.ReceiveAsync(new byte[MaximumFrameSize], CancellationToken.None).AsTask();
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFailures);

        // The socket dies under both: the parked receive faults (teardown, uncounted) and the send that
        // follows fails closed. One failure, one increment — not two.
        socket!.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => parked);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await session.SendSpanAsync(Endpoint.From(s_flowDestination, 53), [0x01, 0x02], sendTimeout.Token));

        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFailures) - before);
        Assert.Equal(0, ((IUdpExchangeCounters)transport).DatagramsSent);
    }

    [Fact]
    public async Task SocketDisablesUdpConnectionResetBeforeBindAndAppliesTheConfiguredReceiveBuffer()
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var responder = new LoopbackUdpResponder();
        TrackingSocket? socket = null;
        var disableCalls = new List<Socket>();
        bool? boundAtDisableCall = null;
        const int configuredBytes = 256 * 1024;

        await using var transport = (LocalUdpTransport)await new LocalUdpTransportFactory(
            new SelfTrafficRegistry(),
            MaximumFrameSize,
            receiveBufferBytes: configuredBytes,
            socketFactory: family => socket = new TrackingSocket(family),
            disableUdpConnectionReset: candidate =>
            {
                boundAtDisableCall = candidate.LocalEndPoint is not null;
                disableCalls.Add(candidate);
            }).CreateAsync(Local("dns-in", responder.Endpoint), sendTimeout.Token);

        Assert.Single(disableCalls);
        Assert.Same(socket, disableCalls[0]);
        Assert.False(boundAtDisableCall ?? true, "SIO_UDP_CONNRESET must be applied before the local socket binds.");
        Assert.NotNull(transport.LocalEndpoint);
        // The configured budget reached the kernel socket; the OS owns the applied value, so only a
        // value below the configured one proves the setting never took effect.
        Assert.True(socket!.ReceiveBufferSize >= configuredBytes);
        Assert.True(transport.AppliedReceiveBufferSize >= configuredBytes);
        Assert.False(socket.Blocking);
    }

    [Fact]
    public void ReceiveFaultClassificationSharesTheSeamRule()
    {
        Assert.Equal(UdpTransportSkipReason.ConnectionReset,
            UdpTransportReceiveClassifier.ClassifyFault(new SocketException((int)SocketError.ConnectionReset)));
        Assert.Null(UdpTransportReceiveClassifier.ClassifyFault(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Null(UdpTransportReceiveClassifier.ClassifyFault(new ObjectDisposedException("socket")));
    }

    private static byte[] ReplyWithTrailer(ReadOnlyMemory<byte> payload, byte trailer)
    {
        var reply = new byte[payload.Length + 1];
        payload.Span.CopyTo(reply);
        reply[^1] = trailer;
        return reply;
    }

    /// <summary>
    /// Reads the sink until <paramref name="flow"/>'s own response arrives: a flow that failed earlier
    /// in the same fact may have left its own response behind, which is not the one under assertion.
    /// </summary>
    private static async Task<(Endpoint Remote, byte[] Payload)> ReadResponseForAsync(FakeResponseSink sink, FlowKey flow, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (responseFlow, remote, payload, _) = await sink.Responses.Reader.ReadAsync(cancellationToken);
            if (responseFlow.Equals(flow)) return (remote, payload);
        }
    }

    private static ProxyTarget Local(string name, Endpoint endpoint) => new(name, Socks5: null, new LocalTarget(name, endpoint));

    private static async ValueTask<LocalUdpTransport> CreateTransportAsync(Endpoint target, SelfTrafficRegistry registry)
    {
        var factory = new LocalUdpTransportFactory(registry, MaximumFrameSize);
        return (LocalUdpTransport)await factory.CreateAsync(Local("dns-in", target), CancellationToken.None);
    }

    private static FlowKey ContextKeyFor(LocalUdpTransport transport) =>
        FlowKey.Create(
            Endpoint.From(IPAddress.Loopback, checked((ushort)transport.LocalEndpoint.Port)),
            Endpoint.From(transport.PeerEndpoint.Address, checked((ushort)transport.PeerEndpoint.Port)),
            TransportProtocol.Udp,
            FlowOriginKind.Host);

    private static UdpProxySession CreateSession(FlowKey flow, IUdpProxyTransport transport, IUdpResponseSink sink, IRuntimeLogger? logger = null) =>
        new(new UdpProxySessionContext(
            flow,
            1,
            new UdpAssociation(
                flow,
                new RelayAlias(FlowKey.Create(
                    Endpoint.From(IPAddress.Loopback, 40_000),
                    Endpoint.From(IPAddress.Loopback, 50_000),
                    TransportProtocol.Udp,
                    FlowOriginKind.Host)),
                1,
                DateTimeOffset.UnixEpoch),
            transport,
            sink,
            MacAddress.Invalid,
            TimeProvider.System,
            static (_, _) => { },
            logger ?? NullRuntimeLogger.Instance,
            s_receiveWindowPool,
            UdpProxyCoordinator.ReceiveWindowSize(MaximumFrameSize),
            CancellationToken.None));

    /// <summary>Returns one fixed transport, so a fact can prove which side of the composite ran.</summary>
    private sealed class StubTransportFactory(IUdpProxyTransport transport) : IUdpProxyTransportFactory
    {
        internal int CreateCalls { get; private set; }

        public ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
        {
            CreateCalls++;
            return ValueTask.FromResult(transport);
        }
    }

}
