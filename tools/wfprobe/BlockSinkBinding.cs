using System.Net;
using System.Net.Sockets;

namespace WinForward.Probe;

// block 规则命中后，驱动把连接改道到 block_sink_port；固定 auth filter 负责拒绝。
// 测试侧在 UDP v4/v6 wildcard 上收到即丢，作为 filter 失效时的兜底，并暴露 filter 是否命中。
// TCP 不绑定：关闭端口给出的 RST 就是兜底语义（不监听、不接受）。
internal sealed class BlockSinkBinding : IDisposable
{
    private const int BindAttempts = 32;
    private const int ReceiveBufferSize = 2048;

    private readonly Socket v4;
    private readonly Socket v6;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task[] loops;
    private long received;
    private bool disposed;

    public BlockSinkBinding()
    {
        for (int attempt = 0; ; attempt++)
        {
            if (attempt >= BindAttempts)
                throw new InvalidOperationException($"无法为 block sink 同时绑定 UDP v4/v6（尝试 {BindAttempts} 次）");
            if (!TryBind(out Socket? candidateV4, out Socket? candidateV6))
                continue;
            v4 = candidateV4!;
            v6 = candidateV6!;
            break;
        }

        Port = ((IPEndPoint)v4.LocalEndPoint!).Port;
        loops = new[] { Task.Run(() => ReceiveLoop(v4)), Task.Run(() => ReceiveLoop(v6)) };
    }

    public int Port { get; }

    public long Received => Interlocked.Read(ref received);

    private static bool TryBind(out Socket? v4Socket, out Socket? v6Socket)
    {
        v4Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        v6Socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            v4Socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            int port = ((IPEndPoint)v4Socket.LocalEndPoint!).Port;
            v6Socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return true;
        }
        catch (SocketException)
        {
            v4Socket.Dispose();
            v6Socket.Dispose();
            v4Socket = null;
            v6Socket = null;
            return false;
        }
    }

    private void ReceiveLoop(Socket socket)
    {
        byte[] buffer = new byte[ReceiveBufferSize];
        while (!cancellation.IsCancellationRequested)
        {
            EndPoint source = socket.AddressFamily == AddressFamily.InterNetworkV6
                                  ? new IPEndPoint(IPAddress.IPv6Any, 0)
                                  : new IPEndPoint(IPAddress.Any, 0);
            try
            {
                socket.ReceiveFrom(buffer, ref source);
                Interlocked.Increment(ref received);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        cancellation.Cancel();
        v4.Dispose();
        v6.Dispose();
        try
        {
            Task.WaitAll(loops, 500);
        }
        catch (AggregateException)
        {
            // 接收循环以关闭 socket 的方式退出，等待异常不影响清理。
        }
        cancellation.Dispose();
    }
}
