using System.Net;
using System.Net.Sockets;

namespace WinForward.Socks5Tool;

/// <summary>裸 TCP 回显目标：端到端测试里充当「跨机 target」，只做回显与计数。</summary>
internal static class EchoTarget
{
    internal static async Task<int> RunAsync(IPAddress listen,
                                             int port,
                                             int expectedConnections,
                                             int timeoutMs,
                                             Reporter report)
    {
        using Socket listener = new(listen.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(listen, port));
        listener.Listen(64);
        report.WriteLine($"echo-ready listen={listener.LocalEndPoint} pid={Environment.ProcessId}");

        using CancellationTokenSource stop = new();
        Counters counters = new(expectedConnections, stop);
        List<Task> sessions = new();
        using CancellationTokenSource timeout = new(timeoutMs);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(stop.Token, timeout.Token);

        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            while (true)
            {
                Socket client;
                try
                {
                    client = await listener.AcceptAsync(linked.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException
                                                             or ObjectDisposedException)
                {
                    break;
                }

                int id = Interlocked.Increment(ref counters.NextId);
                sessions.Add(ServeAsync(id, client, counters, report, linked.Token));
            }

            await Task.WhenAll(sessions);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        bool timedOut = timeout.IsCancellationRequested && !stop.IsCancellationRequested;
        string summary = $"connections={counters.Connections} " +
                         $"bytes_in={Interlocked.Read(ref counters.BytesIn)} " +
                         $"bytes_out={Interlocked.Read(ref counters.BytesOut)}";
        if (timedOut)
        {
            report.WriteLine($"echo-timeout {summary}");
            return 1;
        }

        report.WriteLine($"echo-done {summary}");
        return 0;
    }

    private static async Task ServeAsync(int id,
                                         Socket client,
                                         Counters counters,
                                         Reporter report,
                                         CancellationToken token)
    {
        string peer = client.RemoteEndPoint?.ToString() ?? "?";
        using (client)
        {
            client.NoDelay = true;
            long total = await EchoAsync(client, token);
            if (total == 0)
            {
                // 连通性探测（连上就关）不是一条 target 会话：不计入 --connections，也不参与退出判定。
                report.WriteLine($"echo probe from={peer}");
                return;
            }

            counters.Accepted();
            counters.AddBytes(total, total);
            report.WriteLine($"echo accept #{id} from={peer} bytes={total}");
        }

        counters.Completed();
    }

    private static async Task<long> EchoAsync(Socket socket, CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        try
        {
            while (true)
            {
                int read = await socket.ReceiveAsync(buffer, SocketFlags.None, token);
                if (read == 0)
                {
                    break;
                }

                int offset = 0;
                while (offset < read)
                {
                    offset += await socket.SendAsync(buffer.AsMemory(offset, read - offset), SocketFlags.None, token);
                }

                total += read;
            }

            try
            {
                socket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                // 对面已经关了。
            }
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException
                                               or ObjectDisposedException)
        {
            // 连接被关闭：已回显的字节数仍然有效。
        }

        return total;
    }
}
