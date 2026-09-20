using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.FlowBuilders;

namespace WinForward.Core.Tests;

/// <summary>
/// R2/R5 receive- and send-path resilience: one malformed, unexpected-source, or oversized relay
/// datagram must skip (never kill the session), a failed response reinjection must not tear the
/// session down, and concurrent transport sends must never interleave in the shared send buffer.
/// </summary>
public sealed class UdpReceiveResilienceTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    [Fact]
    public async Task ReceiveLoopSurvivesMalformedUnexpectedAndOversizedRelayDatagrams()
    {
        // AC2: valid datagrams before AND after each anomalous one are delivered, and the
        // session survives all three skip reasons.
        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new UdpProxyCoordinator(factory, sink, new UdpProxyOptions { Logger = logger });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [0], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });

        transport.EnqueueResponse(Datagram(1));
        transport.EnqueueSkip(Socks5UdpReceiveSkipReason.Malformed);
        transport.EnqueueResponse(Datagram(2));
        transport.EnqueueSkip(Socks5UdpReceiveSkipReason.UnexpectedSource);
        transport.EnqueueResponse(Datagram(3));
        transport.EnqueueSkip(Socks5UdpReceiveSkipReason.Oversized);
        transport.EnqueueResponse(Datagram(4));

        for (var expected = 1; expected <= 4; expected++)
        {
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var (responseFlow, _, payload, _) = await sink.Responses.Reader.ReadAsync(readTimeout.Token);
            Assert.Equal(flow, responseFlow);
            Assert.Equal(expected, Assert.Single(payload));
        }

        // The session survived: another send flows through the same transport.
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [5], default, CancellationToken.None));
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 2;
        });
        Assert.False(transport.IsDisposed);
        Assert.Contains(logger.Lines, line => line.Level == RuntimeLogLevel.Debug && line.Message.Contains("skipped", StringComparison.Ordinal));

        return;

        static Socks5UdpDatagram Datagram(byte payload) => new(IPAddress.Parse("192.0.2.53"), DestinationDomain: null, 53, new[] { payload });
    }

    [Fact]
    public async Task InjectionFailureSkipsOneResponseWithoutKillingTheSession()
    {
        var factory = new FakeTransportFactory();
        var sink = new ThrowingResponseSink();
        await using var coordinator = new UdpProxyCoordinator(factory, sink);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });

        transport.EnqueueResponse(Datagram(1));
        transport.EnqueueResponse(Datagram(2));
        await WaitForAsync(() => sink.Injections == 2);

        // Both responses were attempted; the session is still usable for sends.
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [3], default, CancellationToken.None));
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 2;
        });
        Assert.False(transport.IsDisposed);

        return;

        static Socks5UdpDatagram Datagram(byte payload) => new(IPAddress.Parse("192.0.2.53"), DestinationDomain: null, 53, new[] { payload });
    }

    [Fact]
    public async Task TransportClassifiesAnomalousRelayDatagramsAsSkipsInsteadOfThrowing()
    {
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var strangerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        strangerSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var associateRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Socks5TestServer.ServeAssociateOnlyAsync(tcpListener, relayEndpoint, associateRead, serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, new SelfTrafficRegistry(), CancellationToken.None, createControl: null, socketFactory: null);
        var transportEndpoint = new IPEndPoint(IPAddress.Loopback, transport.LocalEndpoint.Port);
        var buffer = new byte[64];
        var destination = IPAddress.Parse("192.0.2.53");

        // Valid datagram from the negotiated relay: delivered.
        await relaySocket.SendToAsync(Socks5UdpDatagrams.Encode(destination, 53, [1]), SocketFlags.None, transportEndpoint, CancellationToken.None);
        var valid = await transport.ReceiveAsync(buffer, CancellationToken.None);
        Assert.True(valid.HasDatagram);
        Assert.Equal((IPAddressValue?)destination, valid.Datagram.DestinationAddress);

        // Unexpected source (same family, different port): skipped, not thrown.
        await strangerSocket.SendToAsync(Socks5UdpDatagrams.Encode(destination, 53, [2]), SocketFlags.None, transportEndpoint, CancellationToken.None);
        var unexpected = await transport.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(Socks5UdpReceiveSkipReason.UnexpectedSource, unexpected.SkipReason);

        // Oversized (fills the 64-byte buffer, so it may be truncated): skipped, not thrown.
        await relaySocket.SendToAsync(new byte[100], SocketFlags.None, transportEndpoint, CancellationToken.None);
        var oversized = await transport.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(Socks5UdpReceiveSkipReason.Oversized, oversized.SkipReason);

        // Malformed (not a SOCKS5 UDP datagram): skipped, not thrown.
        await relaySocket.SendToAsync(new byte[] { 0xff, 0xff, 0xff }, SocketFlags.None, transportEndpoint, CancellationToken.None);
        var malformed = await transport.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(Socks5UdpReceiveSkipReason.Malformed, malformed.SkipReason);

        // The next valid datagram still flows: the receive path never tore anything down.
        await relaySocket.SendToAsync(Socks5UdpDatagrams.Encode(destination, 53, [3]), SocketFlags.None, transportEndpoint, CancellationToken.None);
        var after = await transport.ReceiveAsync(buffer, CancellationToken.None);
        Assert.True(after.HasDatagram);
        Assert.Equal(3, Assert.Single(after.Datagram.Payload.ToArray()));

        await transport.DisposeAsync();
        await serverCancellation.CancelAsync();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public async Task ConcurrentSendsSerializeIntoTheSharedSendBuffer()
    {
        // R5: two pumps dispatching the same flow must never interleave writes into the shared
        // SOCKS5 encode buffer; every received datagram must decode to exactly one input.
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const int datagramCount = 16;
        var received = new TaskCompletionSource<List<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Socks5TestServer.ServeAssociateAndCollectAsync(tcpListener, relaySocket, relayEndpoint, datagramCount, received, serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, new SelfTrafficRegistry(), CancellationToken.None, createControl: null, socketFactory: null);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);

        var payloads = Enumerable.Range(0, datagramCount)
            .Select(index => Enumerable.Repeat((byte)(0xA0 + index), 1024).ToArray())
            .ToList();
        await Task.WhenAll(payloads.Select(payload => transport.SendSpanAsync(destination, payload, CancellationToken.None).AsTask()));

        var datagrams = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(datagramCount, datagrams.Count);
        foreach (var datagram in datagrams)
        {
            Assert.True(Socks5UdpCodec.TryDecode(datagram, out var decoded), "A received datagram did not decode; the shared send buffer was corrupted by interleaving.");
            var expectedPayload = payloads.Single(payload => payload.AsSpan().SequenceEqual(decoded.Payload.Span));
            Assert.NotNull(expectedPayload);
        }

        await transport.DisposeAsync();
        await serverCancellation.CancelAsync();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public async Task ReceiveLoopSurvivesTransportConnectionReset()
    {
        // S2: one ICMP-driven ConnectionReset from the transport must skip (never kill the
        // session); datagrams after it are delivered and further sends reuse the same session.
        var transport = new ConnectionResetOnceTransport();
        var factory = new SingleTransportFactory(transport);
        var sink = new FakeResponseSink();
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new UdpProxyCoordinator(factory, sink, new UdpProxyOptions { Logger = logger });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => transport.SendCount == 1);

        transport.EnqueueResponse(Datagram(1));
        transport.EnqueueResponse(Datagram(2));

        for (var expected = 1; expected <= 2; expected++)
        {
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var (_, _, payload, _) = await sink.Responses.Reader.ReadAsync(readTimeout.Token);
            Assert.Equal(expected, Assert.Single(payload));
        }

        // The session survived: another send flows through the same transport, and the reset was
        // counted in the skip summary instead of tearing the session down.
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [3], default, CancellationToken.None));
        await WaitForAsync(() => transport.SendCount == 2);
        Assert.False(transport.IsDisposed);
        Assert.Contains(logger.Lines, line => line.Level == RuntimeLogLevel.Debug && line.Message.Contains("connectionReset=1", StringComparison.Ordinal));

        return;

        static Socks5UdpDatagram Datagram(byte payload) => new(IPAddress.Parse("192.0.2.53"), DestinationDomain: null, 53, new[] { payload });
    }

    [Fact]
    public async Task DomainTypedResponseIsCountedAndSkipped()
    {
        // S6a: a domain-typed relay response cannot be reinjected (no IP source to rebuild the
        // frame from); it is counted in the skip summary while address-typed responses flow on.
        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new UdpProxyCoordinator(factory, sink, new UdpProxyOptions { Logger = logger });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });

        transport.EnqueueResponse(new Socks5UdpDatagram(DestinationAddress: null, "example.com", 53, new byte[] { 0x7f }));
        transport.EnqueueResponse(Datagram(2));

        await WaitForAsync(() => logger.Lines.Any(line => line.Level == RuntimeLogLevel.Debug && line.Message.Contains("domainDestination=1", StringComparison.Ordinal)));
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, _, payload, _) = await sink.Responses.Reader.ReadAsync(readTimeout.Token);
        Assert.Equal(2, Assert.Single(payload));
        Assert.False(sink.Responses.Reader.TryRead(out _), "The domain-typed datagram must never reach the response sink.");

        // The session survived: another send flows through the same transport.
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [3], default, CancellationToken.None));
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 2;
        });
        Assert.False(transport.IsDisposed);

        return;

        static Socks5UdpDatagram Datagram(byte payload) => new(IPAddress.Parse("192.0.2.53"), DestinationDomain: null, 53, new[] { payload });
    }

    /// <summary>Yields one predetermined transport regardless of how many sessions ask for a relay.</summary>
    private sealed class SingleTransportFactory(IUdpProxyTransport transport) : IUdpProxyTransportFactory
    {
        public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken) =>
            ValueTask.FromResult(transport);
    }

    /// <summary>
    /// A transport whose first receive throws the ICMP-driven ConnectionReset (S2) and whose later
    /// receives behave like the channel-backed fake: proves the session loop treats the fault as
    /// skip-class and keeps delivering.
    /// </summary>
    private sealed class ConnectionResetOnceTransport : IUdpProxyTransport
    {
        private readonly FakeTransport _inner = new(AddressFamily.InterNetwork, 40010);
        private int _receiveCalls;

        public IPEndPoint RelayEndpoint => _inner.RelayEndpoint;
        public IPEndPoint LocalEndpoint => _inner.LocalEndpoint;
        public bool IsDisposed => _inner.IsDisposed;
        public int SendCount
        {
            get { lock (_inner.Sent) return _inner.Sent.Count; }
        }

        public void EnqueueResponse(Socks5UdpDatagram datagram) => _inner.EnqueueResponse(datagram);

        public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken) =>
            _inner.SendSpanAsync(destination, payload, cancellationToken);

        public async ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _receiveCalls) == 1) throw new SocketException((int)SocketError.ConnectionReset);
            return await _inner.ReceiveAsync(buffer, cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>A response sink that records every injection attempt and fails each one like a vanished adapter would.</summary>
    private sealed class ThrowingResponseSink : IUdpResponseSink
    {
        private int _injections;
        public int Injections => Volatile.Read(ref _injections);

        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _injections);
            throw new IOException("The adapter handle is no longer valid (synthetic reinjection failure).");
        }
    }
}
