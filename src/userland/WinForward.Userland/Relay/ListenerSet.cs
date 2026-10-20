using System.Net;
using System.Net.Sockets;

namespace WinForward.Userland.Relay;

/// <summary>
/// 同时绑定 <c>0.0.0.0</c> 与 <c>::</c> 的一对监听套接字，端口由系统分配后两个族共用。
/// </summary>
/// <remarks>
/// 只绑 <c>127.0.0.1</c> 收不到指向网卡地址的连接（D6 实测），所以 v4 用 ANY；
/// v6 显式关掉 DualMode：v4 已经单独绑了 ANY，双栈套接字会与它抢同一个端口。
/// 端口取 0 由系统分配，再把实际端口通过 IOCTL 告诉驱动，避免固定端口冲突。
/// </remarks>
public sealed class ListenerSet : IDisposable
{
    private const int BindAttempts = 5;

    private readonly TcpListener _ipv4;
    private readonly TcpListener _ipv6;

    private ListenerSet(TcpListener ipv4, TcpListener ipv6, int port)
    {
        _ipv4 = ipv4;
        _ipv6 = ipv6;
        Port = port;
    }

    public int Port { get; }

    public IReadOnlyList<TcpListener> Listeners => new[] { _ipv4, _ipv6 };

    public static ListenerSet Bind()
    {
        SocketException? lastError = null;

        for (int attempt = 0; attempt < BindAttempts; attempt++)
        {
            var ipv4 = new TcpListener(IPAddress.Any, 0);
            TcpListener? ipv6 = null;
            try
            {
                ipv4.Start();
                int port = ((IPEndPoint)ipv4.LocalEndpoint).Port;

                ipv6 = new TcpListener(IPAddress.IPv6Any, port);
                ipv6.Server.DualMode = false;
                ipv6.Start();

                return new ListenerSet(ipv4, ipv6, port);
            }
            catch (SocketException exception)
            {
                // 系统刚分配给 v4 的端口可能已被别的进程在 v6 上占用；换一个端口重试。
                lastError = exception;
                ipv6?.Stop();
                ipv4.Stop();
            }
            catch
            {
                ipv6?.Stop();
                ipv4.Stop();
                throw;
            }
        }

        throw new InvalidOperationException(
            $"连续 {BindAttempts} 次都无法在 v4 与 v6 上绑定同一个端口", lastError);
    }

    public void Dispose()
    {
        _ipv4.Stop();
        _ipv6.Stop();
    }
}
