using System.Net;
using System.Net.Sockets;

namespace WinForward.Userland.Relay;

/// <summary>一个 (L, 端口) 上的 UDP 监听 socket；ForwardedUdpRelay 用它把回程从收到 datagram 的 L 发出。</summary>
public readonly record struct UdpListener(IPEndPoint EndPoint, Socket Socket);

/// <summary>一个 (L, 端口) 上的 TCP + UDP 监听 endpoint。</summary>
public sealed class ListenerEndpoint
{
    public required IPEndPoint EndPoint { get; init; }

    public required TcpListener Tcp { get; init; }

    public required Socket Udp { get; init; }
}

/// <summary>
/// 在一个端口上绑定一组本机地址（L）的 TCP + UDP 监听套接字。地址列表为空时退回
/// <c>0.0.0.0</c> + <c>::</c> 的通配绑定（B6a 的单 listener 行为）。
/// </summary>
/// <remarks>
/// 端口取 0 由系统分配，再把实际端口通过 IOCTL 告诉驱动。多个 L 共用同一个端口，驱动按
/// 入口映射选择 DNAT 目标 L；回程帧的源地址就是 L，因此每个 L 一个 UDP socket 才能把
/// 回程关联回正确的表项。任一绑定失败就换端口重来。
/// </remarks>
public sealed class ListenerSet : IDisposable
{
    private const int BindAttempts = 5;

    private readonly List<ListenerEndpoint> _endpoints;

    private ListenerSet(List<ListenerEndpoint> endpoints, int port)
    {
        _endpoints = endpoints;
        Port = port;
    }

    public int Port { get; }

    public IReadOnlyList<ListenerEndpoint> Endpoints => _endpoints;

    public IReadOnlyList<TcpListener> Listeners => _endpoints.Select(endpoint => endpoint.Tcp).ToArray();

    public IReadOnlyList<UdpListener> UdpSockets =>
        _endpoints.Select(endpoint => new UdpListener(endpoint.EndPoint, endpoint.Udp)).ToArray();

    public static ListenerSet Bind() => Bind(Array.Empty<IPAddress>());

    public static ListenerSet Bind(IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
        {
            return BindWildcard();
        }

        IPAddress[] targets = addresses.Distinct().ToArray();
        SocketException? lastError = null;
        for (int attempt = 0; attempt < BindAttempts; attempt++)
        {
            var endpoints = new List<ListenerEndpoint>(targets.Length);
            int port = 0;
            try
            {
                foreach (IPAddress address in targets)
                {
                    var tcp = new TcpListener(address, port);
                    if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        tcp.Server.DualMode = false;
                    }
                    tcp.Start();
                    if (port == 0)
                    {
                        port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                    }

                    var udp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        udp.DualMode = false;
                    }
                    udp.Bind(new IPEndPoint(address, port));
                    endpoints.Add(new ListenerEndpoint
                    {
                        EndPoint = new IPEndPoint(address, port),
                        Tcp = tcp,
                        Udp = udp,
                    });
                }

                return new ListenerSet(endpoints, port);
            }
            catch (SocketException exception)
            {
                // 系统刚分配的端口可能已被别的进程在另一个地址/协议上占用；换一个端口重试。
                lastError = exception;
                foreach (ListenerEndpoint endpoint in endpoints)
                {
                    endpoint.Udp.Dispose();
                    endpoint.Tcp.Stop();
                }
            }
            catch
            {
                foreach (ListenerEndpoint endpoint in endpoints)
                {
                    endpoint.Udp.Dispose();
                    endpoint.Tcp.Stop();
                }
                throw;
            }
        }

        throw new InvalidOperationException(
            $"连续 {BindAttempts} 次都无法在同一端口上绑定 {targets.Length} 个 listener 地址", lastError);
    }

    private static ListenerSet BindWildcard()
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

                var endpoints = new List<ListenerEndpoint>(2)
                {
                    new()
                    {
                        EndPoint = new IPEndPoint(IPAddress.Any, port),
                        Tcp = ipv4,
                        Udp = udp4,
                    },
                    new()
                    {
                        EndPoint = new IPEndPoint(IPAddress.IPv6Any, port),
                        Tcp = ipv6,
                        Udp = udp6,
                    },
                };
                return new ListenerSet(endpoints, port);
            }
            catch (SocketException exception)
            {
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
        foreach (ListenerEndpoint endpoint in _endpoints)
        {
            endpoint.Tcp.Stop();
            endpoint.Udp.Dispose();
        }
    }
}
