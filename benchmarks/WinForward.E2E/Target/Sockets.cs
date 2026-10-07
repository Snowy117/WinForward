using System.Net;
using System.Net.Sockets;

namespace WinForward.E2E.Target;

/// <summary>
/// The one place the target's listening sockets are created and bound, so the three servers cannot
/// drift apart on a policy that decides whether a second target instance is loud or silent.
/// </summary>
/// <remarks>
/// Both halves of the policy are load-bearing on Linux, and neither is what a reader expects:
/// <list type="bullet">
/// <item><c>SO_REUSEPORT</c> is cleared on every listener. <c>SocketOptionName.ReuseAddress</c> sets
/// <c>SO_REUSEADDR</c> and <c>SO_REUSEPORT</c> together there (measured on Linux 6.18 with .NET 10:
/// a fresh socket reads <c>0/0</c>, and after <c>ReuseAddress = true</c> it reads <c>1/1</c>), and it
/// is <c>SO_REUSEPORT</c> that lets a second TCP listener take a port a live listener already owns.
/// <c>.NET</c> exposes no portable name for the option: on Unix the managed
/// <c>SocketOptionName.ReuseUnicastPort</c> is refused with "Operation not supported", and the raw
/// number is Linux's (macOS gives 15 a different meaning), so it is cleared on Linux only.</item>
/// <item>A datagram socket is bound <em>without</em> <c>SO_REUSEADDR</c>. On Linux <c>SO_REUSEADDR</c>
/// alone is enough for a second datagram socket to bind the same address and port (measured: both
/// binds succeed and the kernel then splits the datagrams between them), so clearing only
/// <c>SO_REUSEPORT</c> would leave the UDP listeners exactly as silent as before. Nothing is given up:
/// UDP has no TIME_WAIT, so a stopped target's port is free immediately.</item>
/// </list>
/// A leftover instance therefore fails its second bind with <c>AddressAlreadyInUse</c> instead of
/// binding successfully, receiving almost no traffic, and writing a ledger that reads as if the
/// measured product dropped everything. Measured: two datagram sockets with <c>SO_REUSEADDR</c> and no
/// <c>SO_REUSEPORT</c> both bind; one without <c>SO_REUSEADDR</c> fails.
/// </remarks>
internal static class Sockets
{
    /// <summary>SOL_SOCKET, the level of the raw option below on every platform .NET runs on.</summary>
    private const int SocketLevel = 1;

    /// <summary>Linux's <c>SO_REUSEPORT</c>.</summary>
    private const int ReusePortOption = 15;

    /// <summary>The accept queue both TCP listeners ask for.</summary>
    private const int ListenBacklog = 512;

    /// <summary>
    /// A TCP listener on <paramref name="endPoint"/>, with <c>SO_REUSEADDR</c> kept so a restart can
    /// rebind a port whose earlier connections are in TIME_WAIT.
    /// </summary>
    /// <exception cref="SocketException">The endpoint is taken; the message names it.</exception>
    internal static Socket BindTcpListener(EndPoint endPoint)
    {
        var listener = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);
        ClearReusePort(listener);

        try
        {
            listener.Bind(endPoint);
            listener.Listen(ListenBacklog);
            return listener;
        }
        catch (SocketException exception)
        {
            listener.Dispose();
            throw DescribeBindFailure(endPoint, exception);
        }
    }

    /// <summary>
    /// A datagram socket on <paramref name="endPoint"/>. It deliberately sets no reuse option: the
    /// second bind of a port has to fail, which is what makes a leftover instance loud.
    /// </summary>
    /// <exception cref="SocketException">The endpoint is taken; the message names it.</exception>
    internal static Socket BindUdp(EndPoint endPoint)
    {
        var socket = new Socket(endPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        ClearReusePort(socket);

        try
        {
            socket.Bind(endPoint);
            return socket;
        }
        catch (SocketException exception)
        {
            socket.Dispose();
            throw DescribeBindFailure(endPoint, exception);
        }
    }

    /// <summary>
    /// The endpoint <c>ReceiveFromAsync</c> fills in with each datagram's source. A receive keeps
    /// reusing one instance, so it must be a wildcard of the listener's family rather than the
    /// listener's own address.
    /// </summary>
    internal static EndPoint SourceTemplate(EndPoint endPoint) =>
        endPoint.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

    /// <summary>
    /// The bind failure with the endpoint that could not be taken named in it: the OS message alone
    /// ("Address already in use") does not say which of the target's listeners collided, and the
    /// collision is the one thing a leftover instance must make obvious.
    /// </summary>
    /// <remarks>
    /// The code is carried over as <see cref="SocketException.SocketErrorCode"/>, not as
    /// <see cref="SocketException.NativeErrorCode"/>: on Unix the latter is the errno, and a
    /// <see cref="SocketException"/> built from an errno reports that number instead of the error
    /// (rebuilt from the socket error, both properties are the same as the original's).
    /// </remarks>
    private static SocketException DescribeBindFailure(EndPoint endPoint, SocketException exception) =>
        new(
            (int)exception.SocketErrorCode,
            $"cannot bind {endPoint}: {exception.Message}");

    /// <summary>
    /// Clears <c>SO_REUSEPORT</c> on Linux, where .NET's <c>ReuseAddress</c> turns it on as a side
    /// effect. The four bytes are written as a zero rather than left to the stack buffer, so the
    /// option value does not depend on how that buffer was allocated.
    /// </summary>
    private static void ClearReusePort(Socket socket)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Span<byte> disabled = stackalloc byte[sizeof(int)];
        disabled.Clear();
        socket.SetRawSocketOption(SocketLevel, ReusePortOption, disabled);
    }
}
