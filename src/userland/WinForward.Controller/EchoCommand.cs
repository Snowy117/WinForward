using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace WinForward.Controller;

/// <summary>
/// 端到端测试用的回显服务：独立进程、独立端口，充当「真实目标」。
/// 默认绑 <c>[::]</c> 双栈，同一个端口同时接受 127.0.0.1 与 ::1 的连接。
/// </summary>
internal static class EchoCommand
{
    internal static async Task<int> ExecuteAsync(string[] args)
    {
        CommandLine line = CommandLine.Parse(args);
        int port = line.GetInt("port", 0);
        int connections = line.GetInt("connections", 1);
        int timeoutMs = line.GetInt("timeout-ms", 60000);
        using ReportWriter report = new(line.Get("report"));

        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();
        int actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        report.WriteLine($"echo-ready port={actualPort} pid={Environment.ProcessId}");

        using CancellationTokenSource timeout = new(timeoutMs);
        List<Task<long>> sessions = new();
        try
        {
            while (sessions.Count < connections)
            {
                Socket socket = await listener.AcceptSocketAsync(timeout.Token);
                sessions.Add(ServeAsync(socket, timeout.Token));
            }

            long[] totals = await Task.WhenAll(sessions);
            report.WriteLine($"echo-done connections={totals.Length} " +
                             $"bytes={totals.Sum().ToString(CultureInfo.InvariantCulture)}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            report.WriteLine($"echo-timeout connections={sessions.Count}");
            return 1;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<long> ServeAsync(Socket socket, CancellationToken token)
    {
        using (socket)
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
                    // 对端已经断开。
                }
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // 超时或对端复位：已回显的字节数仍然有效。
            }

            return total;
        }
    }
}
