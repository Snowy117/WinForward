using System.Net;
using System.Net.Sockets;

namespace WinForward.Userland.Relay;

/// <summary>
/// 同时绑定 <c>0.0.0.0</c> 与 <c>::</c> 的 TCP + UDP 监听套接字，端口由系统分配后两族、两协议共用。
/// </summary>
/// <remarks>
/// 只绑 <c>127.0.0.1</c> 收不到指向网卡地址的连接（D6 实测），所以 v4 用 ANY；
/// v6 显式关掉 DualMode：v4 已经单独绑了 ANY，双栈套接字会与它抢同一个端口。
/// 端口取 0 由系统分配，再把实际端口通过 IOCTL 告诉驱动，避免固定端口冲突。
/// TCP 与 UDP 的端口空间彼此独立，同一个编号可以同时绑定；任一绑定失败就换端口重来。
/// </remarks>
public sealed class ListenerSet : IDisposable
{
    private const int BindAttempts = 5;

    private readonly TcpListener _ipv4;
    private readonly TcpListener _ipv6;
    private readonly Socket _udp4;
    private readonly Socket _udp6;

    private ListenerSet(TcpListener ipv4, TcpListener ipv6, Socket udp4, Socket udp6, int port)
    {
        _ipv4 = ipv4;
        _ipv6 = ipv6;
        _udp4 = udp4;
        _udp6 = udp6;
        Port = port;
    }

    public int Port { get; }

    public IReadOnlyList<TcpListener> Listeners => new[] { _ipv4, _ipv6 };

    public IReadOnlyList<Socket> UdpSockets => new[] { _udp4, _udp6 };

    public static ListenerSet Bind()
    {
        SocketException? lastError = null;

        for (int attempt = 0; attempt < BindAttempts; attempt++)
        {
            var ipv4 = new TcpListener(IPAddress.Any, 0);
            TcpListener? ipv6 = null;
            Socket? udp4 = null;
            Socket? udp6 = null;
            try
            {
                ipv4.Start();
                int port = ((IPEndPoint)ipv4.LocalEndpoint).Port;

                ipv6 = new TcpListener(IPAddress.IPv6Any, port);
                ipv6.Server.DualMode = false;
                ipv6.Start();

                udp4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp4.Bind(new IPEndPoint(IPAddress.Any, port));

                udp6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = false };
                udp6.Bind(new IPEndPoint(IPAddress.IPv6Any, port));

                return new ListenerSet(ipv4, ipv6, udp4, udp6, port);
            }
            catch (SocketException exception)
            {
                // 系统刚分配给 v4 的端口可能已被别的进程在 v6 或 UDP 上占用；换一个端口重试。
                lastError = exception;
                udp6?.Dispose();
                udp4?.Dispose();
                ipv6?.Stop();
                ipv4.Stop();
            }
            catch
            {
                udp6?.Dispose();
                udp4?.Dispose();
                ipv6?.Stop();
                ipv4.Stop();
                throw;
            }
        }

        throw new InvalidOperationException(
            $"连续 {BindAttempts} 次都无法在 v4/v6 与 TCP/UDP 上绑定同一个端口", lastError);
    }

    public void Dispose()
    {
        _ipv4.Stop();
        _ipv6.Stop();
        _udp4.Dispose();
        _udp6.Dispose();
    }
}
