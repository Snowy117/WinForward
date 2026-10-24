using System.Net;
using System.Net.Sockets;

namespace WinForward.Userland.Relay;

/// <summary>
/// block 命中后驱动把连接 / UDP 关联改道到的保留端口。驱动侧的固定 auth filter 负责拒绝；
/// 这里的 UDP socket 收到即丢、TCP socket 只 bind 不 listen，是 filter 失效时的兜底。
/// </summary>
/// <remarks>
/// 端口必须在 enabled 期间稳定：先用 UDP v4 通配绑定让系统分配一个端口，再把 UDP v6 与
/// TCP v4/v6 绑到同一端口；任一侧被占用就整个换端口重试，不留「UDP 接得住、TCP 没保留」
/// 的半成品。TCP 不调用 <c>Listen</c>：SYN 得到 RST，对应 D4 的连接阶段拒绝语义。静态 filter
/// 是主路径，bind-not-listen 即使不产生 RST 也不影响正确性，只是失败方式退化为「连接被拒」。
/// 端口不能等于 relay 的 listener 端口：classify 对这两个端口都直接放行，混用会让 sink
/// 自身的流量绕过规则。
/// </remarks>
public sealed class BlockSink : IDisposable
{
    private const int BindAttempts = 5;
    private const int ReceiveBufferSize = 2048;
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(1);

    private readonly Socket _udp4;
    private readonly Socket _udp6;
    private readonly Socket _tcp4;
    private readonly Socket _tcp6;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _receiveLoops;
    private long _received;
    private bool _disposed;

    private BlockSink(Socket udp4, Socket udp6, Socket tcp4, Socket tcp6, int port)
    {
        _udp4 = udp4;
        _udp6 = udp6;
        _tcp4 = tcp4;
        _tcp6 = tcp6;
        Port = port;
        _receiveLoops = new[]
        {
            Task.Run(() => ReceiveLoopAsync(udp4, _stop.Token)),
            Task.Run(() => ReceiveLoopAsync(udp6, _stop.Token)),
        };
    }

    /// <summary>写进驱动 <c>block_sink_port</c> 的端口；生命周期内不变。</summary>
    public int Port { get; }

    /// <summary>收到的 UDP datagram 数。只做诊断：auth filter 生效时应保持 0。</summary>
    public long Received => Interlocked.Read(ref _received);

    /// <summary>
    /// 在 v4/v6 × UDP/TCP 上保留同一个端口。<paramref name="excludePort"/> 是 relay 的
    /// listener 端口（0 表示不排除）；系统把该端口分给 sink 时换下一个候选。
    /// </summary>
    public static BlockSink Bind(int excludePort = 0)
    {
        if (excludePort is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(excludePort), excludePort, "端口越界");
        }

        SocketException? lastError = null;
        for (int attempt = 0; attempt < BindAttempts; attempt++)
        {
            Socket? udp4 = null;
            Socket? udp6 = null;
            Socket? tcp4 = null;
            Socket? tcp6 = null;
            try
            {
                udp4 = BindSocket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp,
                                  IPAddress.Any, 0);
                int port = ((IPEndPoint)udp4.LocalEndPoint!).Port;
                if (port == excludePort)
                {
                    // 系统把 listener 端口分给了 sink：关掉重选，不能让两个用途混在一个端口上。
                    udp4.Dispose();
                    udp4 = null;
                    continue;
                }

                udp6 = BindSocket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp,
                                  IPAddress.IPv6Any, port);
                tcp4 = BindSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp,
                                  IPAddress.Any, port);
                tcp6 = BindSocket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp,
                                  IPAddress.IPv6Any, port);
                return new BlockSink(udp4, udp6, tcp4, tcp6, port);
            }
            catch (SocketException exception)
            {
                // 端口可能已被别的进程在另一个地址 / 协议上占用；换一个候选端口重来。
                lastError = exception;
                udp4?.Dispose();
                udp6?.Dispose();
                tcp4?.Dispose();
                tcp6?.Dispose();
            }
            catch
            {
                udp4?.Dispose();
                udp6?.Dispose();
                tcp4?.Dispose();
                tcp6?.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException(
            $"连续 {BindAttempts} 次都无法在 v4/v6 的 UDP 与 TCP 上保留同一个 block sink 端口", lastError);
    }

    private static Socket BindSocket(AddressFamily family,
                                     SocketType type,
                                     ProtocolType protocol,
                                     IPAddress address,
                                     int port)
    {
        var socket = new Socket(family, type, protocol);
        if (family == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = false;
        }

        try
        {
            socket.Bind(new IPEndPoint(address, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken token)
    {
        byte[] buffer = new byte[ReceiveBufferSize];
        EndPoint remote = socket.AddressFamily == AddressFamily.InterNetwork
                              ? new IPEndPoint(IPAddress.Any, 0)
                              : new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!token.IsCancellationRequested)
        {
            try
            {
                await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, token).ConfigureAwait(false);
                Interlocked.Increment(ref _received);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                // sink 不解析内容也不回复；偶发的接收错误不值得让兜底循环退出。
                continue;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _udp4.Dispose();
        _udp6.Dispose();
        _tcp4.Dispose();
        _tcp6.Dispose();

        try
        {
            Task.WaitAll(_receiveLoops, DisposeTimeout);
        }
        catch (AggregateException)
        {
            // 接收循环以取消 / 关闭 socket 的方式退出，不影响清理。
        }

        _stop.Dispose();
    }
}
