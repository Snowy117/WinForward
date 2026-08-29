using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// Minimal in-process SOCKS5 server helpers: greeting/method-selection exchanges, UDP
/// ASSOCIATE flows with either the bound or an advertised relay endpoint, and the SOCKS5
/// request reader shared by every serve routine.
/// </summary>
internal static class Socks5TestServer
{
    public static async Task StallAfterGreetingAsync(TcpListener listener, TaskCompletionSource<bool> greetingRead, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        greetingRead.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    public static async Task AcceptGreetingAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);
    }

    public static async Task StallAfterUdpAssociateRequestAsync(TcpListener listener, TaskCompletionSource<bool> commandRead, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);
        var request = new byte[10];
        await stream.ReadExactlyAsync(request, cancellationToken).ConfigureAwait(false);
        commandRead.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    public static async Task ServeUdpAssociateAsync(
        TcpListener listener,
        Socket relaySocket,
        IPEndPoint relayEndpoint,
        TaskCompletionSource<byte[]> associateRequest,
        TaskCompletionSource<RelayPacket> relayDatagram,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);

        var request = await ReadSocksRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        associateRequest.TrySetResult(request);

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
        EndPoint sender = relayEndpoint.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(IPAddress.Any, 0)
            : new IPEndPoint(IPAddress.IPv6Any, 0);
        var result = await relaySocket.ReceiveFromAsync(
            buffer,
            SocketFlags.None,
            sender,
            cancellationToken).ConfigureAwait(false);
        relayDatagram.TrySetResult(new RelayPacket(
            buffer.AsSpan(0, result.ReceivedBytes).ToArray(),
            Assert.IsType<IPEndPoint>(result.RemoteEndPoint)));

        var eofProbe = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(eofProbe, cancellationToken).ConfigureAwait(false));
    }

    public static async Task ServeAdvertisedAssociateAsync(TcpListener listener, IPEndPoint advertised, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);
        await ReadSocksRequestAsync(stream, cancellationToken).ConfigureAwait(false);

        var addressBytes = advertised.Address.GetAddressBytes();
        var reply = new byte[4 + addressBytes.Length + 2];
        reply[0] = 5;
        reply[1] = 0;
        reply[2] = 0;
        reply[3] = advertised.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        addressBytes.CopyTo(reply, 4);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4 + addressBytes.Length), checked((ushort)advertised.Port));
        await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);

        var eofProbe = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(eofProbe, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Serves the SOCKS5 greeting + UDP ASSOCIATE exchange and then stalls (no relay traffic).</summary>
    public static async Task ServeAssociateOnlyAsync(TcpListener listener, IPEndPoint relayEndpoint, TaskCompletionSource associateRead, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);
        _ = await ReadSocksRequestAsync(stream, cancellationToken).ConfigureAwait(false);
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
    public static async Task ServeAssociateAndCollectAsync(TcpListener listener, Socket relaySocket, IPEndPoint relayEndpoint, int count, TaskCompletionSource<List<byte[]>> received, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);
        _ = await ReadSocksRequestAsync(stream, cancellationToken).ConfigureAwait(false);

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

    internal static async Task<byte[]> ReadSocksRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var addressLength = prefix[3] switch
        {
            1 => 4,
            4 => 16,
            _ => throw new IOException($"Unexpected SOCKS5 address type {prefix[3]} in test server.")
        };
        var request = new byte[4 + addressLength + 2];
        prefix.CopyTo(request, 0);
        await stream.ReadExactlyAsync(request.AsMemory(prefix.Length), cancellationToken).ConfigureAwait(false);
        return request;
    }
}

internal sealed record RelayPacket(byte[] Datagram, IPEndPoint Sender);
