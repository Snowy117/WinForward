using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;

namespace WinForward.Core.Tests;

/// <summary>
/// R2/R5 receive- and send-path resilience: one malformed, unexpected-source, or oversized relay
/// datagram must skip (never kill the session), a failed response reinjection must not tear the
/// session down, and concurrent transport sends must never interleave in the shared send buffer.
/// </summary>
public sealed class UdpReceiveResilienceTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);

    [Fact]
    public async Task ReceiveLoopSurvivesMalformedUnexpectedAndOversizedRelayDatagrams()
    {
        // AC2: valid datagrams before AND after each anomalous one are delivered, and the
        // session survives all three skip reasons.
        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new UdpProxyCoordinator(factory, sink, logger: logger);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 0 }, CancellationToken.None));
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
            var response = await sink.Responses.Reader.ReadAsync(readTimeout.Token);
            Assert.Equal(flow, response.Flow);
            Assert.Equal(expected, Assert.Single(response.Payload));
        }

        // The session survived: another send flows through the same transport.
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 5 }, CancellationToken.None));
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 2;
        });
        Assert.False(transport.IsDisposed);
        Assert.Contains(logger.Lines, line => line.Level == RuntimeLogLevel.Debug && line.Message.Contains("skipped", StringComparison.Ordinal));

        static Socks5UdpDatagram Datagram(byte payload) => new(IPAddress.Parse("192.0.2.53"), null, 53, new[] { payload });
    }

    [Fact]
    public async Task InjectionFailureSkipsOneResponseWithoutKillingTheSession()
    {
        var factory = new FakeTransportFactory();
        var sink = new ThrowingResponseSink();
        await using var coordinator = new UdpProxyCoordinator(factory, sink);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
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
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 3 }, CancellationToken.None));
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 2;
        });
        Assert.False(transport.IsDisposed);

        static Socks5UdpDatagram Datagram(byte payload) => new(IPAddress.Parse("192.0.2.53"), null, 53, new[] { payload });
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
        var server = ServeAssociateOnlyAsync(tcpListener, relayEndpoint, associateRead, serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), null, null);
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, new SelfTrafficRegistry(), CancellationToken.None);
        var transportEndpoint = new IPEndPoint(IPAddress.Loopback, transport.LocalEndpoint.Port);
        var buffer = new byte[64];
        var destination = IPAddress.Parse("192.0.2.53");

        // Valid datagram from the negotiated relay: delivered.
        await relaySocket.SendToAsync(Socks5UdpCodec.Encode(destination, 53, new byte[] { 1 }), SocketFlags.None, transportEndpoint, CancellationToken.None);
        var valid = await transport.ReceiveAsync(buffer, CancellationToken.None);
        Assert.True(valid.HasDatagram);
        Assert.Equal(destination, valid.Datagram.DestinationAddress);

        // Unexpected source (same family, different port): skipped, not thrown.
        await strangerSocket.SendToAsync(Socks5UdpCodec.Encode(destination, 53, new byte[] { 2 }), SocketFlags.None, transportEndpoint, CancellationToken.None);
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
        await relaySocket.SendToAsync(Socks5UdpCodec.Encode(destination, 53, new byte[] { 3 }), SocketFlags.None, transportEndpoint, CancellationToken.None);
        var after = await transport.ReceiveAsync(buffer, CancellationToken.None);
        Assert.True(after.HasDatagram);
        Assert.Equal(3, Assert.Single(after.Datagram.Payload.ToArray()));

        await transport.DisposeAsync();
        serverCancellation.Cancel();
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
        var server = ServeAssociateAndCollectAsync(tcpListener, relaySocket, relayEndpoint, datagramCount, received, serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), null, null);
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, new SelfTrafficRegistry(), CancellationToken.None);
        var destination = new IPEndPoint(IPAddress.Parse("192.0.2.53"), 53);

        var payloads = Enumerable.Range(0, datagramCount)
            .Select(index => Enumerable.Repeat((byte)(0xA0 + index), 1024).ToArray())
            .ToList();
        await Task.WhenAll(payloads.Select(payload => transport.SendAsync(destination, payload, CancellationToken.None).AsTask()));

        var datagrams = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(datagramCount, datagrams.Count);
        foreach (var datagram in datagrams)
        {
            Assert.True(Socks5UdpCodec.TryDecode(datagram, out var decoded), "A received datagram did not decode; the shared send buffer was corrupted by interleaving.");
            var expectedPayload = payloads.Single(payload => payload.AsSpan().SequenceEqual(decoded.Payload.Span));
            Assert.NotNull(expectedPayload);
        }

        await transport.DisposeAsync();
        serverCancellation.Cancel();
        await IgnoreExpectedCancellationAsync(server);
    }

    /// <summary>Serves the SOCKS5 greeting + UDP ASSOCIATE exchange and then stops (no relay traffic).</summary>
    private static async Task ServeAssociateOnlyAsync(TcpListener listener, IPEndPoint relayEndpoint, TaskCompletionSource associateRead, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);
        _ = await Socks5TestServer.ReadSocksRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        associateRead.TrySetResult();

        var addressBytes = relayEndpoint.Address.GetAddressBytes();
        var reply = new byte[4 + addressBytes.Length + 2];
        reply[0] = 5;
        reply[1] = 0;
        reply[2] = 0;
        reply[3] = relayEndpoint.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        addressBytes.CopyTo(reply, 4);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4 + addressBytes.Length), checked((ushort)relayEndpoint.Port));
        await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serves the SOCKS5 greeting + UDP ASSOCIATE exchange, then collects sent relay datagrams.</summary>
    private static async Task ServeAssociateAndCollectAsync(TcpListener listener, Socket relaySocket, IPEndPoint relayEndpoint, int count, TaskCompletionSource<List<byte[]>> received, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);
        _ = await Socks5TestServer.ReadSocksRequestAsync(stream, cancellationToken).ConfigureAwait(false);

        var addressBytes = relayEndpoint.Address.GetAddressBytes();
        var reply = new byte[4 + addressBytes.Length + 2];
        reply[0] = 5;
        reply[1] = 0;
        reply[2] = 0;
        reply[3] = relayEndpoint.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        addressBytes.CopyTo(reply, 4);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4 + addressBytes.Length), checked((ushort)relayEndpoint.Port));
        await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[65_535];
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        var datagrams = new List<byte[]>();
        while (datagrams.Count < count)
        {
            var result = await relaySocket.ReceiveFromAsync(buffer, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
            lock (datagrams) datagrams.Add(buffer.AsSpan(0, result.ReceivedBytes).ToArray());
            if (datagrams.Count == count) received.TrySetResult(datagrams);
        }
    }

    private static FlowKey CreateFlow(string remoteAddress) =>
        FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse(remoteAddress), 53), TransportProtocol.Udp, FlowOriginKind.Host);

    /// <summary>A response sink that records every injection attempt and fails each one like a vanished adapter would.</summary>
    private sealed class ThrowingResponseSink : IUdpResponseSink
    {
        private int _injections;
        public int Injections => Volatile.Read(ref _injections);

        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _injections);
            throw new IOException("The adapter handle is no longer valid (synthetic reinjection failure).");
        }
    }
}
