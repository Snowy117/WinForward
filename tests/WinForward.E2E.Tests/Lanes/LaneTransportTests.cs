using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// The two real adapters against a real peer on loopback: what they report, and — the part that is not
/// inferable from the seam's contract — how each transport outcome is reached. The framing terminality
/// is D18.6 #3's mapping (a lost frame boundary stops the lane, one bad checksum does not), and the udp
/// truncation facts are the registration the task asks for: .NET exposes no <c>MSG_TRUNC</c>, so an
/// oversized datagram is named by the transport rather than read as a corrupt frame.
/// </summary>
public sealed class LaneTransportTests
{
    private const int PayloadBytes = 32;

    [Fact]
    public async Task TheUdpTransportSendsAndReceivesDatagrams()
    {
        using var peer = BindLoopbackUdp(out var peerEndPoint);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var transport = new UdpLaneTransport(socket, peerEndPoint, DestinationBytes());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var sent = BuildFrame(0x7100_0001u, 7);
        var open = await transport.OpenAsync(cancellation.Token);

        Assert.True(open.Ok, open.Error);

        var send = await transport.SendAsync(sent, cancellation.Token);
        Assert.True(send.Accepted, send.Error);

        var received = new byte[DestinationBytes()];
        peer.ReceiveTimeout = 5000;
        var datagram = peer.Receive(received);
        Assert.Equal(sent.Length, datagram);
        Assert.Equal(sent, received.AsSpan(0, datagram).ToArray());

        // ...and the other direction: a datagram the peer sends is handed up whole.
        peer.SendTo(sent, (IPEndPoint)socket.LocalEndPoint!);
        var arrival = await transport.ReceiveAsync(received, cancellation.Token);
        Assert.Equal(LaneReceiveKind.Payload, arrival.Kind);
        Assert.Equal(sent.Length, arrival.Length);
        Assert.Equal(sent, received.AsSpan(0, arrival.Length).ToArray());
    }

    [Fact]
    public async Task ADatagramTooLargeForTheDestinationIsNamedAsTruncatedAndNeverAsCorrupt()
    {
        using var peer = BindLoopbackUdp(out var peerEndPoint);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var transport = new UdpLaneTransport(socket, peerEndPoint, DestinationBytes());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await transport.OpenAsync(cancellation.Token);

        // One byte past what the engine can take, and still inside the transport's own wide buffer, so
        // the datagram is received whole and the oversize is measured rather than inferred.
        var oversized = new byte[DestinationBytes() + 1];
        peer.SendTo(oversized, (IPEndPoint)socket.LocalEndPoint!);

        var received = new byte[DestinationBytes()];
        var result = await transport.ReceiveAsync(received, cancellation.Token);

        Assert.Equal(LaneReceiveKind.Malformed, result.Kind);
        Assert.Equal(FrameDecodeError.Truncated, result.Detail);
        Assert.Equal(0, result.Length);
    }

    [Fact]
    public async Task AUdpTransportThatCannotConnectAnswersWithAResultRatherThanThrowing()
    {
        // A socket that is already closed cannot connect at all: the adapter has to answer with a result
        // rather than throw, because a lane that never opened is an outcome the arm records (D18.1).
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Dispose();
        using var transport = new UdpLaneTransport(socket, new IPEndPoint(IPAddress.Loopback, 9), DestinationBytes());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var open = await transport.OpenAsync(cancellation.Token);

        Assert.False(open.Ok);
        Assert.NotNull(open.Error);
    }

    [Fact]
    public async Task TheTcpTransportOpensTheLaneSendsFramesAndReportsWhatTheStreamReads()
    {
        using var listener = ListenLoopbackTcp(out var endPoint);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var transport = new TcpLaneTransport(socket, endPoint, 0x7400_0001u);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // The accept is started before the connect: a listener that accepts first would block the
        // thread the connect needs.
        var accept = listener.AcceptAsync(cancellation.Token);
        var open = await transport.OpenAsync(cancellation.Token);
        Assert.True(open.Ok, open.Error);
        using var peer = await accept;

        // The lane's one command frame reaches the peer before any request does.
        var command = new byte[FrameCodec.HeaderSize + TcpCommand.PayloadLength + FrameCodec.TrailerSize];
        await peer.ReceiveAsync(command, SocketFlags.None, cancellation.Token);
        Assert.True(FrameCodec.TryDecode(command, out var commandHeader, out _, out var commandError), commandError.ToString());
        Assert.Equal(0x7400_0001u, commandHeader.ConnectionId);
        Assert.Equal(FrameCodec.CommandSequence, commandHeader.Sequence);

        var sent = BuildFrame(0x7400_0001u, 3);
        var send = await transport.SendAsync(sent, cancellation.Token);
        Assert.True(send.Accepted, send.Error);
        var onWire = new byte[sent.Length];
        await peer.ReceiveAsync(onWire, SocketFlags.None, cancellation.Token);
        Assert.Equal(sent, onWire);

        // One frame comes back: the transport hands up its payload, not its header or trailer.
        await peer.SendAsync(sent, SocketFlags.None, cancellation.Token);
        var received = new byte[DestinationBytes()];
        var arrival = await transport.ReceiveAsync(received, cancellation.Token);
        Assert.Equal(LaneReceiveKind.Payload, arrival.Kind);
        Assert.Equal(PayloadBytes, arrival.Length);
        Assert.Equal(sent.AsSpan(FrameCodec.HeaderSize, PayloadBytes).ToArray(), received.AsSpan(0, arrival.Length).ToArray());
    }

    [Fact]
    public async Task AFrameWhoseChecksumFailsIsOneBadMessageAndAFrameBoundaryLossIsTerminal()
    {
        using var listener = ListenLoopbackTcp(out var endPoint);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var transport = new TcpLaneTransport(socket, endPoint, 0x7400_0002u);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var accept = listener.AcceptAsync(cancellation.Token);
        await transport.OpenAsync(cancellation.Token);
        using var peer = await accept;
        await DrainAsync(peer, FrameCodec.HeaderSize + TcpCommand.PayloadLength + FrameCodec.TrailerSize, cancellation.Token);

        var received = new byte[DestinationBytes()];

        // A frame whose payload was flipped without recomputing its checksum: the boundary is intact,
        // so the lane reads the next message (D18.6 #3).
        var damaged = new FrameBuffer(PayloadBytes);
        damaged.Build(0x7400_0002u, 1, 0);
        damaged.FlipPayloadByte(PayloadBytes, 0);
        var corrupt = damaged.Memory[..(FrameCodec.HeaderSize + PayloadBytes + FrameCodec.TrailerSize)].ToArray();
        await peer.SendAsync(corrupt, SocketFlags.None, cancellation.Token);
        var malformed = await transport.ReceiveAsync(received, cancellation.Token);
        Assert.Equal(LaneReceiveKind.Malformed, malformed.Kind);
        Assert.Equal(FrameDecodeError.BadChecksum, malformed.Detail);

        // A header whose magic is gone: no later frame can be found, so the lane stops.
        var lost = new byte[FrameCodec.HeaderSize];
        await peer.SendAsync(lost, SocketFlags.None, cancellation.Token);
        var ioError = await transport.ReceiveAsync(received, cancellation.Token);
        Assert.Equal(LaneReceiveKind.IOError, ioError.Kind);
        Assert.Equal(FrameDecodeError.BadMagic, ioError.Detail);
    }

    [Fact]
    public async Task APeerFinEndsTheStream()
    {
        using var listener = ListenLoopbackTcp(out var endPoint);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var transport = new TcpLaneTransport(socket, endPoint, 0x7400_0004u);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var accept = listener.AcceptAsync(cancellation.Token);
        await transport.OpenAsync(cancellation.Token);
        using var peer = await accept;
        await DrainAsync(peer, FrameCodec.HeaderSize + TcpCommand.PayloadLength + FrameCodec.TrailerSize, cancellation.Token);

        peer.Shutdown(SocketShutdown.Send);

        var arrival = await transport.ReceiveAsync(new byte[DestinationBytes()], cancellation.Token);
        Assert.Equal(LaneReceiveKind.EndOfStream, arrival.Kind);
    }

    /// <summary>
    /// The other side of the pair above: the same close, ten bytes into a frame, is a lost boundary
    /// rather than the end of a stream. The lane stops either way, and the reason it carries is what
    /// separates a clean half-close from a truncation (D19.3 D).
    /// </summary>
    [Fact]
    public async Task APeerFinInsideAFrameIsTerminalTruncation()
    {
        using var listener = ListenLoopbackTcp(out var endPoint);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var transport = new TcpLaneTransport(socket, endPoint, 0x7400_0005u);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var accept = listener.AcceptAsync(cancellation.Token);
        await transport.OpenAsync(cancellation.Token);
        using var peer = await accept;
        await DrainAsync(peer, FrameCodec.HeaderSize + TcpCommand.PayloadLength + FrameCodec.TrailerSize, cancellation.Token);

        await peer.SendAsync(BuildFrame(0x7400_0005u, 1).AsMemory(0, 10), SocketFlags.None, cancellation.Token);
        peer.Shutdown(SocketShutdown.Send);

        var truncated = await transport.ReceiveAsync(new byte[DestinationBytes()], cancellation.Token);
        Assert.Equal(LaneReceiveKind.IOError, truncated.Kind);
        Assert.Equal(FrameDecodeError.Truncated, truncated.Detail);
        Assert.Equal(0, truncated.Length);
    }

    [Fact]
    public async Task AHeaderClaimingAnImpossibleLengthIsTerminal()
    {
        using var listener = ListenLoopbackTcp(out var endPoint);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var transport = new TcpLaneTransport(socket, endPoint, 0x7400_0003u);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var accept = listener.AcceptAsync(cancellation.Token);
        await transport.OpenAsync(cancellation.Token);
        using var peer = await accept;
        await DrainAsync(peer, FrameCodec.HeaderSize + TcpCommand.PayloadLength + FrameCodec.TrailerSize, cancellation.Token);

        var received = new byte[DestinationBytes()];

        var absurd = new byte[FrameCodec.HeaderSize];
        FrameCodec.WriteHeader(absurd, 0x7400_0003u, 1, 0, FrameCodec.MaxPayloadLength + 1);
        await peer.SendAsync(absurd, SocketFlags.None, cancellation.Token);
        var badLength = await transport.ReceiveAsync(received, cancellation.Token);
        Assert.Equal(LaneReceiveKind.IOError, badLength.Kind);
        Assert.Equal(FrameDecodeError.BadLength, badLength.Detail);
    }

    private static int DestinationBytes() => FrameCodec.HeaderSize + PayloadBytes + FrameCodec.TrailerSize;

    private static byte[] BuildFrame(uint connectionId, ulong sequence)
    {
        var frame = new FrameBuffer(PayloadBytes);
        var length = frame.Build(connectionId, sequence, 0);
        return frame.Memory[..length].ToArray();
    }

    private static Socket BindLoopbackUdp(out IPEndPoint endPoint)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        endPoint = (IPEndPoint)socket.LocalEndPoint!;
        return socket;
    }

    private static Socket ListenLoopbackTcp(out IPEndPoint endPoint)
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        endPoint = (IPEndPoint)listener.LocalEndPoint!;
        return listener;
    }

    private static async Task DrainAsync(Socket peer, int bytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[bytes];
        var read = 0;
        while (read < bytes)
        {
            read += await peer.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, cancellationToken);
        }
    }
}
