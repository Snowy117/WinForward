using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;

namespace WinForward.Runtime.Socks5.Tests;

/// <summary>
/// The UoT connect-mode transport's establishment, framing, and fault contract against the scripted
/// server: the pipelined flight is an observation (the fixture reads the first frame before it
/// writes the CONNECT reply), frames are reassembled and bounded, replies carry the flow's captured
/// destination as their source, and a refusal or a death arrives as its typed exception rather than
/// as a raw socket fault the session's receive loop would treat as a one-datagram skip.
/// </summary>
public sealed class Socks5UotTransportTests
{
    private static readonly TimeSpan s_budget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task FirstFrameIsWrittenBeforeTheConnectReplyIsWritten()
    {
        // The fixture defers its CONNECT reply until the first frame has been read, so reaching its
        // reply gate proves the flow's first datagram did not wait for any handshake reply. A client
        // that waited could never reach the gate.
        var replyGateReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedSocks5UotServer();
        server.ConnectReplyWriteGate = async _ =>
        {
            replyGateReached.TrySetResult();
            await releaseReply.Task.ConfigureAwait(false);
        };
        await using var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = "QRS"u8.ToArray();
        var observedBeforeReply = ObserveFramesBeforeTheConnectReply(server);

        await transport.SendSpanAsync(destination, payload, CancellationToken.None);

        await replyGateReached.Task.WaitAsync(s_budget);
        Assert.True(await observedBeforeReply.Task.WaitAsync(s_budget), "the frame observer runs while the CONNECT reply is still unwritten");
        Assert.True(server.FirstFrameBeforeConnectReply, "the fixture read a frame before it wrote the CONNECT reply");
        Assert.False(server.ConnectReplyWritten.Task.IsCompleted, "the reply is deliberately held while the frame is already on the wire");
        Assert.Equal(1, server.FrameCount);
        Assert.Equal(payload, server.Frames[0]);
        Assert.Equal(destination, await server.UotDestination.Task.WaitAsync(s_budget));

        releaseReply.TrySetResult();
        await server.ConnectReplyWritten.Task.WaitAsync(s_budget);
        Assert.Equal(1, server.ConnectReplyCount);
        var echo = "echo"u8.ToArray();
        await server.InjectFrameAsync(echo);
        var receive = await transport.ReceiveAsync(new byte[512], CancellationToken.None).AsTask().WaitAsync(s_budget);
        Assert.True(receive.HasDatagram);
        Assert.Equal(echo, receive.Datagram.Payload.ToArray());
        Assert.False(server.ProtocolViolation.Task.IsCompleted);
    }

    [Fact]
    public async Task EchoRoundTripDeclaresTheCapturedDestinationAsTheReplySource()
    {
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("198.51.100.7"), 4433);
        var payload = "PING"u8.ToArray();

        await transport.SendSpanAsync(destination, payload, CancellationToken.None);
        await server.FirstFrameRead.Task.WaitAsync(s_budget);
        var echo = "PONG"u8.ToArray();
        await server.InjectFrameAsync(echo);

        var receive = await transport.ReceiveAsync(new byte[512], CancellationToken.None);
        Assert.True(receive.HasDatagram);
        Assert.Equal(echo, receive.Datagram.Payload.ToArray());
        // Connect mode carries no on-wire source: the synthesized source is the flow's destination,
        // which is what keeps the session's source check and the response reinjector unchanged.
        Assert.Equal(destination.Address, receive.Datagram.SourceAddress);
        Assert.Equal(destination.Port, receive.Datagram.SourcePort);
        Assert.Null(receive.Datagram.SourceDomain);
        Assert.False(server.ProtocolViolation.Task.IsCompleted);
    }

    [Fact]
    public async Task CredentialedFlowEstablishesAndEchoes()
    {
        // The deferred dial writes the RFC 1929 message before the method selection, so the deferred
        // completion has two replies to consume before the CONNECT reply — the ordering the transport
        // must keep exactly.
        await using var server = new ScriptedSocks5UotServer(username: "user", password: "secret");
        await using var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = "AUTH"u8.ToArray();
        await transport.SendSpanAsync(destination, payload, CancellationToken.None);
        await server.ConnectReplyWritten.Task.WaitAsync(s_budget);
        Assert.True(await server.CredentialsAccepted.Task.WaitAsync(s_budget));
        await server.InjectFrameAsync(payload);

        var receive = await transport.ReceiveAsync(new byte[512], CancellationToken.None);
        Assert.True(receive.HasDatagram);
        Assert.Equal(payload, receive.Datagram.Payload.ToArray());
        Assert.False(server.ProtocolViolation.Task.IsCompleted);
    }

    [Fact]
    public async Task CredentialRefusalSurfacesAsHandshakeRejected()
    {
        // The server selects username/password and refuses the pair: the completion consumes the
        // refusal reply and the CONNECT reply never arrives, which is a setup rejection, not a death.
        await using var server = new ScriptedSocks5UotServer(username: "user", password: "secret");
        var target = new Socks5Server("scripted", "127.0.0.1", checked((ushort)server.ControlEndpoint.Port), "user", "wrong", UdpOverTcp: true);
        var factory = new Socks5UdpTransportFactory(new SelfTrafficRegistry(), UdpFrameBuilder.DefaultMaximumEthernetFrame);
        await using var transport = (Socks5UotTransport)await factory.CreateAsync(ProxyTarget.FromServer(target), CancellationToken.None);

        await transport.SendSpanAsync(Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), "AUTH"u8.ToArray(), CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<UdpTransportHandshakeRejectedException>(async () =>
            await transport.ReceiveAsync(new byte[512], CancellationToken.None));
        Assert.Contains("username/password authentication failed", refusal.InnerException!.Message, StringComparison.Ordinal);
        Assert.False(await server.CredentialsAccepted.Task.WaitAsync(s_budget));
    }

    [Fact]
    public async Task SplitFrameDeliveredOneByteAtATimeIsReassembled()
    {
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.10"), 5000);
        await transport.SendSpanAsync(destination, "EST"u8.ToArray(), CancellationToken.None);
        await server.ConnectReplyWritten.Task.WaitAsync(s_budget);

        // The whole frame — prefix included — crosses the stream one byte per write, so the transport
        // can only deliver it by reassembling across partial reads.
        var payload = new byte[37];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index + 1);
        server.InjectByteDelay = TimeSpan.FromMilliseconds(2);
        await server.InjectFrameAsync(payload);

        var receive = await transport.ReceiveAsync(new byte[512], CancellationToken.None).AsTask().WaitAsync(s_budget);
        Assert.True(receive.HasDatagram);
        Assert.Equal(payload, receive.Datagram.Payload.ToArray());
    }

    [Fact]
    public async Task OversizedFrameIsConsumedAndTheNextFrameIsStillReadable()
    {
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server);
        await transport.SendSpanAsync(Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), "EST"u8.ToArray(), CancellationToken.None);
        await server.ConnectReplyWritten.Task.WaitAsync(s_budget);
        var buffer = new byte[64];
        await server.InjectFrameAsync(new byte[200]);
        await server.InjectFrameAsync("NEXT"u8.ToArray());

        var oversized = await transport.ReceiveAsync(buffer, CancellationToken.None);
        Assert.False(oversized.HasDatagram);
        Assert.Equal(UdpTransportSkipReason.Oversized, oversized.SkipReason);

        // The oversized frame was consumed to its end, so the stream is still aligned for the next one.
        var next = await transport.ReceiveAsync(buffer, CancellationToken.None);
        Assert.True(next.HasDatagram);
        Assert.Equal("NEXT"u8.ToArray(), next.Datagram.Payload.ToArray());
    }

    [Fact]
    public async Task AFrameThatExactlyFillsTheBufferIsDelivered()
    {
        // The oversized rule is the frame's declared length against the caller's window: a frame of
        // exactly that length is complete by construction, so it is not a truncation candidate.
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server);
        await transport.SendSpanAsync(Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), "EST"u8.ToArray(), CancellationToken.None);
        await server.ConnectReplyWritten.Task.WaitAsync(s_budget);
        var payload = new byte[64];
        await server.InjectFrameAsync(payload);

        var receive = await transport.ReceiveAsync(new byte[64], CancellationToken.None);
        Assert.True(receive.HasDatagram);
        Assert.Equal(64, receive.Datagram.Payload.Length);
    }

    [Fact]
    public async Task ZeroLengthFrameIsALegalEmptyDatagram()
    {
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        await transport.SendSpanAsync(destination, "EST"u8.ToArray(), CancellationToken.None);
        await server.ConnectReplyWritten.Task.WaitAsync(s_budget);
        await server.InjectFrameAsync(ReadOnlyMemory<byte>.Empty);

        var receive = await transport.ReceiveAsync(new byte[512], CancellationToken.None);
        Assert.True(receive.HasDatagram);
        Assert.Empty(receive.Datagram.Payload.ToArray());
        Assert.Equal(destination.Port, receive.Datagram.SourcePort);
    }

    [Fact]
    public async Task JumboCapFramesAPayloadBeyondTheDefaultCap()
    {
        // The frame ceiling follows the pinned frame cap, exactly as the native transport's send buffer
        // does — a 2000-byte payload is far beyond the default ABI's deliverable ceiling and must still
        // be framed, read, and echoed.
        const int jumboCap = 9014;
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server, maximumFrameSize: jumboCap);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[2000];
        await transport.SendSpanAsync(destination, payload, CancellationToken.None);
        await server.ConnectReplyWritten.Task.WaitAsync(s_budget);
        Assert.Equal(payload.Length, server.Frames[0].Length);
        await server.InjectFrameAsync(payload);

        var receive = await transport.ReceiveAsync(new byte[2000], CancellationToken.None);
        Assert.True(receive.HasDatagram);
        Assert.Equal(payload.Length, receive.Datagram.Payload.Length);
    }

    [Fact]
    public async Task DestinationMismatchFailsClosedWithoutWritingASecondFrame()
    {
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        await transport.SendSpanAsync(destination, "ONE"u8.ToArray(), CancellationToken.None);
        await server.ConnectReplyWritten.Task.WaitAsync(s_budget);

        // Connect mode binds the stream to the first send's destination; a second destination cannot
        // be framed on it, so the send fails closed instead of mis-framing the stream.
        await Assert.ThrowsAsync<IOException>(async () =>
            await transport.SendSpanAsync(Endpoint.From(IPAddress.Parse("192.0.2.54"), 53), "TWO"u8.ToArray(), CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(async () =>
            await transport.SendSpanAsync(Endpoint.From(IPAddress.Parse("192.0.2.53"), 54), "TWO"u8.ToArray(), CancellationToken.None));

        await Task.Delay(50);
        Assert.Equal(1, server.FrameCount);
    }

    [Fact]
    public async Task PayloadAboveTheSixteenBitFrameLengthFailsClosed()
    {
        // The jumbo cap clears the buffer bound, so this pins the u16be frame ceiling itself: a
        // payload the frame prefix cannot express must throw rather than truncate.
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server, maximumFrameSize: 70_000);
        var payload = new byte[ushort.MaxValue + 1];

        await Assert.ThrowsAsync<IOException>(async () =>
            await transport.SendSpanAsync(Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), payload, CancellationToken.None));
    }

    [Fact]
    public async Task ConnectRefusalSurfacesAsHandshakeRejectedOnReceiveAndOnEveryLaterSend()
    {
        await using var server = new ScriptedSocks5UotServer(connectReplyStatus: 5);
        await using var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        await transport.SendSpanAsync(destination, "EST"u8.ToArray(), CancellationToken.None);

        // The refusal is discovered late — the first datagram was already written — and is typed so
        // the coordinator arms the setup cooldown instead of re-dialing per client retransmit.
        var refusal = await Assert.ThrowsAsync<UdpTransportHandshakeRejectedException>(async () =>
            await transport.ReceiveAsync(new byte[512], CancellationToken.None));
        Assert.Contains("REP 5", refusal.InnerException!.Message, StringComparison.Ordinal);
        Assert.False(server.ProtocolViolation.Task.IsCompleted);

        // The recorded fault fails every later send closed before the gate and the socket, and a
        // later receive reports the same refusal instead of re-entering the one-shot completion.
        var repeated = await Assert.ThrowsAsync<UdpTransportHandshakeRejectedException>(async () =>
            await transport.SendSpanAsync(destination, "AGAIN"u8.ToArray(), CancellationToken.None));
        Assert.Same(refusal, repeated);
        var repeatedReceive = await Assert.ThrowsAsync<UdpTransportHandshakeRejectedException>(async () =>
            await transport.ReceiveAsync(new byte[512], CancellationToken.None));
        Assert.Same(refusal, repeatedReceive);
    }

    [Fact]
    public async Task ConnectionDropMidFlowSurfacesAsAssociationLost()
    {
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        await transport.SendSpanAsync(destination, "EST"u8.ToArray(), CancellationToken.None);
        await server.ConnectReplyWritten.Task.WaitAsync(s_budget);
        await server.InjectFrameAsync("FIRST"u8.ToArray());
        Assert.True((await transport.ReceiveAsync(new byte[512], CancellationToken.None)).HasDatagram);

        server.DropConnection();
        await WaitForAsync(() => server.LiveConnectionCount == 0);

        var lost = await Assert.ThrowsAsync<UdpAssociationLostException>(async () =>
            await transport.ReceiveAsync(new byte[512], CancellationToken.None));
        Assert.NotNull(lost.InnerException);
    }

    [Fact]
    public async Task ConnectionResetNeverSurfacesAsASkip()
    {
        // The session's receive loop treats SocketError.ConnectionReset as a one-datagram skip, which
        // is right for an ICMP answer to a UDP send and an infinite spin on a dead stream. A reset
        // connection must therefore arrive as the typed association-lost fault from both directions.
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        await transport.SendSpanAsync(destination, "EST"u8.ToArray(), CancellationToken.None);
        await server.ConnectReplyWritten.Task.WaitAsync(s_budget);
        await server.InjectFrameAsync("FIRST"u8.ToArray());
        Assert.True((await transport.ReceiveAsync(new byte[512], CancellationToken.None)).HasDatagram);

        server.DropConnection(reset: true);
        await WaitForAsync(() => server.LiveConnectionCount == 0);
        await Task.Delay(100);

        // The receive observes the death first: the reset read either surfaces the reset itself or an
        // EOF of the same read, and neither is a skip result the session's loop would spin on.
        var receiveFault = await Record.ExceptionAsync(async () => await transport.ReceiveAsync(new byte[512], CancellationToken.None));

        var inner = Assert.IsType<UdpAssociationLostException>(receiveFault).InnerException;
        Assert.True(IsResetOrEof(inner), DescribeFaultChain(inner));

        // The send leg fails closed on the recorded death rather than writing to a dead stream.
        var sendFault = await Record.ExceptionAsync(async () => await transport.SendSpanAsync(destination, "AFTER"u8.ToArray(), CancellationToken.None));
        Assert.Same(receiveFault, sendFault);
    }

    [Fact]
    public async Task AReceiveThatNeverPipelinesFailsVisiblyInsteadOfHanging()
    {
        // A client that waited for the CONNECT reply before sending its first datagram would deadlock
        // the fixture, which is exactly what the fixture must not do: its bounded first-frame read
        // records a violation and closes the connection, so the client's reply read fails with EOF.
        await using var server = new ScriptedSocks5UotServer(firstFrameTimeout: TimeSpan.FromSeconds(2));
        await using var transport = await CreateTransportAsync(server);

        var receive = transport.ReceiveAsync(new byte[512], CancellationToken.None).AsTask();
        await Assert.ThrowsAsync<UdpTransportHandshakeRejectedException>(async () => await receive.WaitAsync(s_budget));

        Assert.True(server.ProtocolViolation.Task.IsCompleted, "the fixture must record the pipelining violation");
        var violation = await server.ProtocolViolation.Task;
        Assert.Contains("request window", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFixtureRefusesARequestDestinationTheSocksMappingDoesNotDefine()
    {
        // The server half of the structural guard: the fixture decodes the request destination with
        // the ordinary SOCKS address mapping, so the protocol's per-datagram address type for IPv4
        // (0x00) is refused as an unknown family — the pinned server's own rejection — instead of
        // being mirrored into a flow that only works against a client sharing its misreading.
        await using var server = new ScriptedSocks5UotServer();
        using var client = new TcpClient();
        await client.ConnectAsync(server.ControlEndpoint).ConfigureAwait(false);

        await WriteFlightAsync(client.GetStream(), requestAddressType: 0x00, IPAddress.Parse("192.0.2.53")).ConfigureAwait(false);

        var violation = await server.ProtocolViolation.Task.WaitAsync(s_budget);
        Assert.Contains("unknown address family: 0", violation.Message, StringComparison.Ordinal);
        Assert.False(server.UotDestination.Task.IsCompleted, "an unknown family must not produce a destination");
    }

    [Fact]
    public async Task TheFixtureDecodesAnIPv6RequestDestinationThroughTheSocksAddressType()
    {
        // The other half: 0x04 is the SOCKS IPv6 form, consumed as 16 address bytes, so a client that
        // wrote the per-datagram 0x01 for an IPv6 destination is read as a four-byte IPv4 address and
        // desyncs the stream instead of producing this endpoint.
        await using var server = new ScriptedSocks5UotServer();
        using var client = new TcpClient();
        await client.ConnectAsync(server.ControlEndpoint).ConfigureAwait(false);
        var address = IPAddress.Parse("2001:db8::53");

        await WriteFlightAsync(client.GetStream(), Socks5Messages.AddressTypeIPv6, address).ConfigureAwait(false);

        Assert.Equal(Endpoint.From(address, 5353), await server.UotDestination.Task.WaitAsync(s_budget));
        Assert.False(server.ProtocolViolation.Task.IsCompleted);
    }

    /// <summary>
    /// Writes the pipelined flight a client opens with — the greeting, the domain-typed CONNECT to
    /// the magic address, and a UoT request header whose destination is <paramref name="address"/>
    /// under <paramref name="requestAddressType"/> — without reading any reply.
    /// </summary>
    private static async Task WriteFlightAsync(Stream stream, byte requestAddressType, IPAddress address)
    {
        var greeting = Socks5Messages.Greeting(credentials: false);
        await stream.WriteAsync(greeting).ConfigureAwait(false);

        var connect = new byte[Socks5Messages.RequestLength(UotCodec.MagicAddress)];
        var connectLength = Socks5Messages.WriteRequest(Socks5Command.Connect, UotCodec.MagicAddress, 0, connect);
        await stream.WriteAsync(connect.AsMemory(0, connectLength)).ConfigureAwait(false);

        var addressBytes = address.GetAddressBytes();
        var header = new byte[2 + addressBytes.Length + 2];
        header[0] = 1;
        header[1] = requestAddressType;
        addressBytes.CopyTo(header, 2);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2 + addressBytes.Length), 5353);
        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task ExchangeCountersFollowTheAcceptedSendsAndTheFirstDecodedFrame()
    {
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server);
        var counters = Assert.IsType<IUdpExchangeCounters>(transport, exactMatch: false);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);

        Assert.Equal(0, counters.DatagramsSent);
        Assert.False(counters.SawResponse);

        await transport.SendSpanAsync(destination, "ONE"u8.ToArray(), CancellationToken.None);
        await server.ConnectReplyWritten.Task.WaitAsync(s_budget);
        await transport.SendSpanAsync(destination, "TWO"u8.ToArray(), CancellationToken.None);
        Assert.Equal(2, counters.DatagramsSent);
        Assert.False(counters.SawResponse);

        await server.InjectFrameAsync("ECHO"u8.ToArray());
        var receive = await transport.ReceiveAsync(new byte[512], CancellationToken.None);
        Assert.True(receive.HasDatagram);
        Assert.True(counters.SawResponse);
        Assert.Equal(2, counters.DatagramsSent);
    }

    [Fact]
    public async Task TheControlTupleIsRegisteredForTheFlowAndReleasedOnDispose()
    {
        // The dial registers the flow's control tuple before the SYN (the deferred dial's own
        // contract, pinned by Socks5ControlConnectionDeferredHandshakeTests); this pins that the UoT
        // create path wires that callback and that the connection — which owns the token — releases
        // it with the flow.
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UotServer();
        var transport = await CreateTransportAsync(server, registry);
        var local = Endpoint.From(transport.LocalEndpoint.Address, checked((ushort)transport.LocalEndpoint.Port));
        var remote = Endpoint.From(transport.PeerEndpoint.Address, checked((ushort)transport.PeerEndpoint.Port));
        var context = FlowBuilders.Context(FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Host));

        Assert.True(registry.IsOwned(context));

        await transport.DisposeAsync();
        Assert.False(registry.IsOwned(context));
    }

    [Fact]
    public async Task EndpointsAreTheConnectionsOwnEndpoints()
    {
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server);
        await server.Accepted.Task.WaitAsync(s_budget);

        Assert.Equal(server.ControlEndpoint, transport.PeerEndpoint);
        Assert.Equal(server.AcceptedRemoteEndpoint, transport.LocalEndpoint);
        Assert.Equal(AddressFamily.InterNetwork, transport.LocalEndpoint.AddressFamily);
    }

    [Fact]
    public async Task FactoryCreatesTheUotTransportWithoutWritingTheConnectRequest()
    {
        // Create dials and publishes the endpoints, and returns before the CONNECT request or the UoT
        // header exists — the fixture cannot have read a destination yet.
        await using var server = new ScriptedSocks5UotServer();
        var transport = await CreateTransportAsync(server);

        Assert.IsType<Socks5UotTransport>(transport);
        Assert.Equal(1, server.ConnectionCount);
        Assert.False(server.UotDestination.Task.IsCompleted, "the create path must not write the CONNECT request");
        Assert.False(server.ConnectReplyWritten.Task.IsCompleted);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task FactoryReleasesTheUotConnectionWhenTheTransportCannotBeConstructed()
    {
        await using var server = new ScriptedSocks5UotServer();
        var factory = new Socks5UdpTransportFactory(
            new SelfTrafficRegistry(),
            UdpFrameBuilder.DefaultMaximumEthernetFrame,
            createControl: async (dialServer, cancellationToken) =>
            {
                // A connection that is already closed fails the transport's construction after the
                // factory acquired it; the factory's catch must release it and propagate.
                var control = await Socks5ControlConnection.ConnectDeferredHandshakeAsync(dialServer, cancellationToken).ConfigureAwait(false);
                await control.DisposeAsync().ConfigureAwait(false);
                return control;
            });

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await factory.CreateAsync(ProxyTarget.FromServer(server.Server), CancellationToken.None));

        await WaitForAsync(() => server.LiveConnectionCount == 0);
        Assert.Equal(1, server.ConnectionCount);
    }

    /// <summary>
    /// Observes every frame the fixture reads, recording whether the CONNECT reply was still
    /// unwritten at that moment — taken from the fixture's own frame hook rather than from the
    /// reply gate.
    /// </summary>
    private static TaskCompletionSource<bool> ObserveFramesBeforeTheConnectReply(ScriptedSocks5UotServer server)
    {
        var observedBeforeReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.FrameObserver = (_, _) =>
        {
            observedBeforeReply.TrySetResult(!server.ConnectReplyWritten.Task.IsCompleted);
            return ValueTask.CompletedTask;
        };

        return observedBeforeReply;
    }

    /// <summary>
    /// A reset connection reaches a stream read as either the socket error itself, an EOF of the same
    /// read, or the framework's IOException wrapper carrying the socket error as its inner fault.
    /// Every shape is the same death; none of them is a skip-class per-datagram anomaly.
    /// </summary>
    private static bool IsResetOrEof(Exception? fault) =>
        fault is EndOfStreamException
        || fault is SocketException { SocketErrorCode: SocketError.ConnectionReset }
        || (fault is IOException && IsResetOrEof(fault.InnerException));

    private static string DescribeFaultChain(Exception? fault) =>
        fault is null ? "none" : $"{fault.GetType().Name}:{fault.Message} <- {DescribeFaultChain(fault.InnerException)}";

    private static async ValueTask<Socks5UotTransport> CreateTransportAsync(
        ScriptedSocks5UotServer server,
        SelfTrafficRegistry? registry = null,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame,
        Func<Socks5Server, CancellationToken, ValueTask<Socks5ControlConnection>>? createControl = null,
        CancellationToken cancellationToken = default)
    {
        var factory = new Socks5UdpTransportFactory(registry ?? new SelfTrafficRegistry(), maximumFrameSize, createControl: createControl);
        return (Socks5UotTransport)await factory.CreateAsync(ProxyTarget.FromServer(server.Server), cancellationToken).ConfigureAwait(false);
    }
}
