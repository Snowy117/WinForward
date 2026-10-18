using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace WinForward.E2E.Client;

/// <summary>
/// A measured TCP connection could not be established through the SOCKS5 proxy. The exception is
/// deliberately distinct from a <see cref="SocketException"/>: the arms book a socket-level connect
/// failure as an outcome, while a proxy failure has to fail the run instead of producing an arm that
/// silently measured less traffic.
/// </summary>
#pragma warning disable S3871, RCS1194 // Internal to the harness client; the friend test assembly asserts this type and its message.
internal sealed class Socks5ConnectException : Exception
{

    /// <summary>A failure with the stage and the socket error folded into the message.</summary>
    internal Socks5ConnectException(string message)
        : base(message)
    {
    }

    /// <summary>A failure that wraps the socket error that caused it.</summary>
    internal Socks5ConnectException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
#pragma warning restore S3871, RCS1194

/// <summary>
/// The client half of a SOCKS5 CONNECT (RFC 1928): one no-auth greeting, one CONNECT request, and the
/// reply header plus the proxy's bound address. The socket is connected to the proxy before this type
/// is called and carries the target's traffic after it returns.
/// </summary>
/// <remarks>
/// <para><b>CONNECT is the only command.</b> BIND and UDP ASSOCIATE are not implemented and no option
/// selects them: a plan that needs UDP or DNS is refused before a socket is opened, because a
/// CONNECT-only hop cannot carry either and forwarding them directly would measure a different path
/// than the one the row declares.</para>
/// <para><b>No-auth is the only method offered.</b> The sing-box calibration inbound accepts no
/// credentials; a proxy that selects another method is a configuration mismatch and fails loudly
/// rather than falling back to a method the client did not negotiate.</para>
/// <para><b>Every failure is an exception.</b> Unlike a direct connect, which an arm books as a
/// scheduled shortfall, a failed proxy hop means the measurement path itself is absent. Returning a
/// result would let the run exit 0 with empty arms.</para>
/// </remarks>
internal static class Socks5Connector
{
    private const byte Version = 5;

    private const byte MethodNoAuthentication = 0x00;

    private const byte MethodNoneAcceptable = 0xFF;

    private const byte CommandConnect = 0x01;

    private const byte AddressIPv4 = 0x01;

    private const byte AddressDomain = 0x03;

    private const byte AddressIPv6 = 0x04;

    private const byte ReplySucceeded = 0x00;

    /// <summary>
    /// Connects <paramref name="socket"/> to <paramref name="proxy"/>, performs the no-auth greeting,
    /// and asks the proxy for a CONNECT to <paramref name="target"/>. The socket is left positioned at
    /// the start of the target's byte stream.
    /// </summary>
    /// <exception cref="Socks5ConnectException">Any stage of the proxy hop failed.</exception>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    internal static async ValueTask ConnectAsync(
        Socket socket,
        Socks5Proxy proxy,
        EndPoint target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(target);

        if (target is not IPEndPoint targetEndPoint)
        {
            throw new ArgumentException(
                "a SOCKS5 CONNECT needs the target's address type, and the harness target is an IP literal",
                nameof(target));
        }

        try
        {
            await socket.ConnectAsync(proxy.EndPoint, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            throw new Socks5ConnectException(
                $"connecting to the SOCKS5 proxy {proxy.Display} failed: {exception.Message}",
                exception);
        }

        await GreetAsync(socket, proxy, cancellationToken).ConfigureAwait(false);
        await RequestConnectAsync(socket, proxy, targetEndPoint, cancellationToken).ConfigureAwait(false);
        await ReadReplyAsync(socket, proxy, targetEndPoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The CONNECT request bytes for one target: version 5, CONNECT, reserved 0, the target's address
    /// type and bytes, and the port in network order.
    /// </summary>
    private static byte[] BuildConnectRequest(IPEndPoint target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var address = target.Address;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var addressBytes = address.GetAddressBytes();
        byte addressType;
        // ReSharper disable once ConvertIfStatementToSwitchStatement // Only IPv4 and IPv6 have a SOCKS5 address type; the else is a refusal, not a third case.
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            addressType = AddressIPv4;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            addressType = AddressIPv6;
        }
        else
        {
            throw new ArgumentException($"the target address family '{address.AddressFamily}' has no SOCKS5 address type", nameof(target));
        }

        var request = new byte[4 + addressBytes.Length + 2];
        request[0] = Version;
        request[1] = CommandConnect;
        request[2] = 0x00;
        request[3] = addressType;
        addressBytes.CopyTo(request, 4);
        request[^2] = (byte)(target.Port >> 8);
        request[^1] = (byte)(target.Port & 0xFF);
        return request;
    }

    private static async ValueTask GreetAsync(Socket socket, Socks5Proxy proxy, CancellationToken cancellationToken)
    {
        var greeting = new byte[] { Version, 0x01, MethodNoAuthentication };
        await SendAllAsync(socket, greeting, proxy, "sending the no-auth greeting", cancellationToken).ConfigureAwait(false);

        var selection = new byte[2];
        await ReceiveExactlyAsync(socket, selection, proxy, "reading the authentication method", cancellationToken).ConfigureAwait(false);
        if (selection[0] != Version)
        {
            throw new Socks5ConnectException($"the SOCKS5 proxy {proxy.Display} answered version {selection[0]} instead of {Version}");
        }

        if (selection[1] == MethodNoneAcceptable)
        {
            throw new Socks5ConnectException(
                $"the SOCKS5 proxy {proxy.Display} offered no acceptable authentication method; this client offers no-auth (0x00) only");
        }

        if (selection[1] != MethodNoAuthentication)
        {
            throw new Socks5ConnectException($"the SOCKS5 proxy {proxy.Display} selected authentication method 0x{selection[1]:X2}; this client offers no-auth (0x00) only");
        }
    }

    private static async ValueTask RequestConnectAsync(
        Socket socket,
        Socks5Proxy proxy,
        IPEndPoint target,
        CancellationToken cancellationToken)
    {
        var request = BuildConnectRequest(target);
        await SendAllAsync(socket, request, proxy, $"sending CONNECT to {Display(target)}", cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReadReplyAsync(
        Socket socket,
        Socks5Proxy proxy,
        IPEndPoint target,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReceiveExactlyAsync(socket, header, proxy, "reading the CONNECT reply", cancellationToken).ConfigureAwait(false);
        if (header[0] != Version)
        {
            throw new Socks5ConnectException($"the SOCKS5 proxy {proxy.Display} answered version {header[0]} in its CONNECT reply instead of {Version}");
        }

        if (header[1] != ReplySucceeded)
        {
            throw new Socks5ConnectException($"the SOCKS5 proxy {proxy.Display} refused CONNECT to {Display(target)}: {ReplyText(header[1])}");
        }

        var addressLength = header[3] switch
        {
            AddressIPv4 => 4,
            AddressIPv6 => 16,
            AddressDomain => await ReadDomainLengthAsync(socket, proxy, cancellationToken).ConfigureAwait(false),
            _ => throw new Socks5ConnectException($"the SOCKS5 proxy {proxy.Display} answered CONNECT with unknown address type 0x{header[3]:X2}"),
        };

        var boundAddress = new byte[addressLength + 2];
        await ReceiveExactlyAsync(socket, boundAddress, proxy, "reading the CONNECT bound address", cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadDomainLengthAsync(Socket socket, Socks5Proxy proxy, CancellationToken cancellationToken)
    {
        var length = new byte[1];
        await ReceiveExactlyAsync(socket, length, proxy, "reading the CONNECT bound address length", cancellationToken).ConfigureAwait(false);
        return length[0] == 0
            ? throw new Socks5ConnectException($"the SOCKS5 proxy {proxy.Display} answered CONNECT with an empty domain address")
            : length[0];
    }

    private static async ValueTask SendAllAsync(
        Socket socket,
        ReadOnlyMemory<byte> payload,
        Socks5Proxy proxy,
        string stage,
        CancellationToken cancellationToken)
    {
        var sent = 0;
        while (sent < payload.Length)
        {
            int written;
            try
            {
                written = await socket.SendAsync(payload[sent..], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException exception)
            {
                throw new Socks5ConnectException($"{stage} through the SOCKS5 proxy {proxy.Display} failed: {exception.Message}", exception);
            }

            if (written <= 0)
            {
                throw new Socks5ConnectException($"{stage} through the SOCKS5 proxy {proxy.Display} failed: the socket accepted no bytes");
            }

            sent += written;
        }
    }

    private static async ValueTask ReceiveExactlyAsync(
        Socket socket,
        Memory<byte> destination,
        Socks5Proxy proxy,
        string stage,
        CancellationToken cancellationToken)
    {
        var received = 0;
        while (received < destination.Length)
        {
            int read;
            try
            {
                read = await socket.ReceiveAsync(destination[received..], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException exception)
            {
                throw new Socks5ConnectException($"{stage} from the SOCKS5 proxy {proxy.Display} failed: {exception.Message}", exception);
            }

            if (read == 0)
            {
                throw new Socks5ConnectException($"{stage} from the SOCKS5 proxy {proxy.Display} failed: the proxy closed the connection");
            }

            received += read;
        }
    }

    private static string ReplyText(byte reply) => reply switch
    {
        0x01 => "general SOCKS server failure",
        0x02 => "connection not allowed by ruleset",
        0x03 => "network unreachable",
        0x04 => "host unreachable",
        0x05 => "connection refused",
        0x06 => "TTL expired",
        0x07 => "command not supported",
        0x08 => "address type not supported",
        _ => $"unknown reply 0x{reply:X2}",
    };

    private static string Display(IPEndPoint endpoint) => string.Create(
        CultureInfo.InvariantCulture,
        $"{endpoint.Address}:{endpoint.Port}");
}
