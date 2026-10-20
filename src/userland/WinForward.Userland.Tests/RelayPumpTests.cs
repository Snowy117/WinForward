using System.Net;
using System.Net.Sockets;
using System.Text;
using WinForward.Userland.Relay;

namespace WinForward.Userland.Tests;

/// <summary>
/// 中继收尾语义：单方向异常必须让兄弟泵立刻退出；正常 EOF 必须保留半关闭。
/// </summary>
/// <remarks>
/// 这两条是 E2E 依赖的行为，也是「一个方向 RST、另一方向永远等数据」那个 bug 的回归测试。
/// 用 loopback 的真实 socket，不经过驱动：这里测的是 <see cref="PumpPair"/> 的收尾规则本身。
/// </remarks>
internal static class RelayPumpTests
{
    internal static void Register()
    {
        TestRunner.Add("relay/单方向 RST 后兄弟泵 1 秒内退出", RstCancelsSiblingPump);
        TestRunner.Add("relay/双向 EOF 保留半关闭且数据完整", HalfCloseKeepsDataFlowing);
    }

    private static void RstCancelsSiblingPump()
    {
        (Socket clientA, Socket serverA) = ConnectPair();
        (Socket clientB, Socket serverB) = ConnectPair();
        Task<(PumpOutcome FirstToSecond, PumpOutcome SecondToFirst, string Reason)> pumps = default!;
        try
        {
            pumps = PumpPair.RunAsync(serverA, serverB, TimeSpan.FromSeconds(30), CancellationToken.None);
            Thread.Sleep(100);

            // 客户端侧 RST：serverA 的 Receive 会抛 ConnectionReset，兄弟泵（serverB→clientB）本该一直等数据。
            clientA.LingerState = new LingerOption(enable: true, seconds: 0);
            clientA.Dispose();

            Check.True(pumps.Wait(TimeSpan.FromSeconds(1)), "单方向 RST 后 PumpPair 必须在 1 秒内结束");
            (PumpOutcome up, PumpOutcome down, string reason) = pumps.Result;
            Check.True(!up.CleanEof, "RST 方向的 CleanEof 必须是 false");
            Check.Equal("pump-failed", reason, "收尾原因");
            _ = down;
        }
        finally
        {
            DisposeQuietly(clientA);
            DisposeQuietly(clientB);
            DisposeQuietly(serverA);
            DisposeQuietly(serverB);
        }
    }

    private static void HalfCloseKeepsDataFlowing()
    {
        (Socket clientA, Socket serverA) = ConnectPair();
        (Socket clientB, Socket serverB) = ConnectPair();
        try
        {
            Task<(PumpOutcome FirstToSecond, PumpOutcome SecondToFirst, string Reason)> pumps =
                PumpPair.RunAsync(serverA, serverB, TimeSpan.FromSeconds(5), CancellationToken.None);

            clientA.Send(Encoding.ASCII.GetBytes("ping"));
            clientA.Shutdown(SocketShutdown.Send);

            Check.Equal("ping", Encoding.ASCII.GetString(ReceiveExactly(clientB, 4)), "目标侧收到 ping");
            Check.Equal(0, ReceiveEof(clientB), "目标侧看到半关闭");

            clientB.Send(Encoding.ASCII.GetBytes("pong"));
            clientB.Shutdown(SocketShutdown.Send);

            Check.Equal("pong", Encoding.ASCII.GetString(ReceiveExactly(clientA, 4)), "客户端收到 pong");
            Check.Equal(0, ReceiveEof(clientA), "客户端看到半关闭");

            Check.True(pumps.Wait(TimeSpan.FromSeconds(5)), "双向 EOF 后 PumpPair 必须正常结束");
            (PumpOutcome up, PumpOutcome down, string reason) = pumps.Result;
            Check.True(up.CleanEof && down.CleanEof, "两个方向都必须是正常 EOF");
            Check.Equal(4L, up.Bytes, "client→target 字节数");
            Check.Equal(4L, down.Bytes, "target→client 字节数");
            Check.Equal("peer-closed", reason, "收尾原因");
        }
        finally
        {
            DisposeQuietly(clientA);
            DisposeQuietly(clientB);
            DisposeQuietly(serverA);
            DisposeQuietly(serverB);
        }
    }

    private static (Socket Client, Socket Server) ConnectPair()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect((IPEndPoint)listener.LocalEndpoint);
            Socket server = listener.AcceptSocket();
            return (client, server);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static byte[] ReceiveExactly(Socket socket, int count)
    {
        socket.ReceiveTimeout = 5000;
        byte[] data = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = socket.Receive(data, offset, count - offset, SocketFlags.None);
            if (read == 0)
            {
                throw new TestFailure($"只读到 {offset}/{count} 字节就 EOF");
            }

            offset += read;
        }

        return data;
    }

    private static int ReceiveEof(Socket socket)
    {
        socket.ReceiveTimeout = 5000;
        byte[] probe = new byte[1];
        return socket.Receive(probe, 0, 1, SocketFlags.None);
    }

    private static void DisposeQuietly(Socket socket)
    {
        try
        {
            socket.Dispose();
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // 测试清理路径。
        }
    }
}
