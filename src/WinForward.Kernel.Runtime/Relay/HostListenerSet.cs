using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace WinForward.Userland.Relay;

/// <summary>
/// The Kernel host listener: one v4 TCP listener, one v6-only TCP listener and one dual-mode v6 UDP
/// socket, all bound to the same port.
/// </summary>
/// <remarks>
/// <para>
/// The driver's <c>probe_config</c> carries a single <c>listener_port</c> for both protocols, and its
/// first gate lets a connection through when its remote port matches either reserved port — so TCP and
/// UDP must land on one port, and the block sink must live on another.
/// </para>
/// <para>
/// The UDP socket is a single dual-mode v6 wildcard rather than a v4/v6 pair. A v4-mapped
/// <em>connected</em> association is redirected on the IPv6 layer to <c>::ffff:&lt;local&gt;</c>, and a
/// <c>IPV6_V6ONLY</c> socket does not receive v4-mapped traffic by Winsock's rules; native IPv4
/// datagrams are delivered to the same dual-mode socket as mapped endpoints. The wildcard bind is also
/// required because the redirect target address is the client's own local address, not a fixed one.
/// </para>
/// <para>
/// Both options must be set <em>before</em> the bind: the redirect-context option at the IPv4 and IPv6
/// protocol levels is what makes the driver attach the 36-byte association context to every datagram,
/// and <c>SIO_UDP_CONNRESET</c> off keeps an ICMP port-unreachable answering a reply from surfacing as
/// a receive failure on the next <c>WSARecvMsg</c>. The set at the level the socket was not created
/// with is rejected by Winsock, so each is attempted independently.
/// </para>
/// <para>
/// This set is the single owner of those sockets: shutting it down is what closes the UDP socket and
/// therefore unblocks the receiver's parking receive.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HostListenerSet : IDisposable
{
    private const int BindAttempts = 5;

    /// <summary><c>SIO_UDP_CONNRESET</c> (vendor IOCTL): the input is a 4-byte Win32 BOOL.</summary>
    private const int SioUdpConnreset = unchecked((int)0x9800000C);

    private static readonly byte[] s_disableValue = new byte[4];

    private readonly TcpListener[] _tcpListeners;
    private bool _disposed;

    private HostListenerSet(int port, TcpListener[] tcpListeners, Socket udpSocket)
    {
        Port = port;
        _tcpListeners = tcpListeners;
        UdpSocket = udpSocket;
    }

    /// <summary>The shared listener port the driver publishes in <c>listener_port</c>.</summary>
    public int Port { get; }

    /// <summary>The v4 and v6-only TCP listeners the accept loops serve.</summary>
    public IReadOnlyList<TcpListener> TcpListeners => _tcpListeners;

    /// <summary>The dual-mode v6 UDP socket: the datagram receive leg and the reply sender.</summary>
    public Socket UdpSocket { get; }

    /// <summary>
    /// Binds every socket on one port, retrying the whole set on a fresh port when the system hands out
    /// a port that is already taken on another protocol or address family. Any partially bound attempt
    /// is released before the retry, so a failed attempt leaves no half-owned port behind.
    /// </summary>
    public static HostListenerSet Bind()
    {
        SocketException? lastError = null;
        for (var attempt = 0; attempt < BindAttempts; attempt++)
        {
            TcpListener? ipv4 = null;
            TcpListener? ipv6 = null;
            Socket? udp = null;
            try
            {
                ipv4 = new TcpListener(IPAddress.Any, 0);
                ipv4.Start();
                var port = ((IPEndPoint)ipv4.LocalEndpoint).Port;

                ipv6 = new TcpListener(IPAddress.IPv6Any, port);
                ipv6.Server.DualMode = false;
                ipv6.Start();

                udp = BindUdp(port);
                return new HostListenerSet(port, [ipv4, ipv6], udp);
            }
            catch (SocketException exception)
            {
                lastError = exception;
                udp?.Dispose();
                ipv6?.Stop();
                ipv4?.Stop();
            }
            catch
            {
                udp?.Dispose();
                ipv6?.Stop();
                ipv4?.Stop();
                throw;
            }
        }

        throw new InvalidOperationException(
            $"Could not bind the TCP and UDP listeners on one port in {BindAttempts} attempts.", lastError);
    }

    private static Socket BindUdp(int port)
    {
        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        try
        {
            TryEnableRedirectContext(socket, SocketOptionLevel.IPv6);
            TryEnableRedirectContext(socket, SocketOptionLevel.IP);
            DisableUdpConnectionReset(socket);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Enables the WFP redirect-context option at one protocol level. A socket only accepts the level it
    /// was created with, so the rejected one is expected and the surviving one is what the layer that
    /// delivers the datagram reads.
    /// </summary>
    private static void TryEnableRedirectContext(Socket socket, SocketOptionLevel level)
    {
        try
        {
            socket.SetSocketOption(level, (SocketOptionName)WfpUdpContext.IpWfpRedirectContext, optionValue: true);
        }
        catch (SocketException)
        {
            // The other level's set is the live one for this socket's family.
        }
    }

    private static void DisableUdpConnectionReset(Socket socket)
    {
        try
        {
            socket.IOControl(SioUdpConnreset, s_disableValue, optionOutValue: null);
        }
        catch (SocketException)
        {
            // A host without the vendor IOCTL keeps the Windows default; the receiver treats a
            // ConnectionReset receive as a skip, so the loop still survives without it.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var listener in _tcpListeners)
        {
            listener.Stop();
        }

        UdpSocket.Dispose();
    }
}
