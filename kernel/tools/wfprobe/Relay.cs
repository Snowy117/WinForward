using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinForward.Probe;

// 代理的替身：接受被重定向的连接，并从同一个进程发起出站连接。
// 四道防循环闸需要「代理自己的出站连接」这个场景，只有独立进程才能把 self-PID 与发起方区分开。
internal static class Relay
{
    public static async Task<int> RunAsync(Options options)
    {
        IPAddress bind = IPAddress.Parse(options.Get("bind") ?? "127.0.0.1");
        int port = options.GetInt("port", 0);
        int expectedAccepts = options.GetInt("expect-accepts", 1);
        int firstTimeoutMs = options.GetInt("first-timeout-ms", 15000);
        int windowMs = options.GetInt("accept-window-ms", 2500);
        int outboundTimeoutMs = options.GetInt("outbound-timeout-ms", 5000);
        string? outboundText = options.Get("outbound");
        bool withRecords = options.GetInt("outbound-records", 0) != 0;
        int lingerMs = options.GetInt("linger-ms", 0);

        var listener = new TcpListener(bind, port);
        listener.Start();
        int actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine($"relay-ready pid={Environment.ProcessId} port={actualPort}");
        Console.Out.Flush();

        var accepted = new List<Socket>();
        try
        {
            using var firstCts = new CancellationTokenSource(firstTimeoutMs);
            accepted.Add(await listener.AcceptSocketAsync(firstCts.Token));
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("relay FAIL no-first-accept");
            listener.Stop();
            return 1;
        }

        Report(accepted[0], 0);
        Task<bool>? outboundTask = null;
        if (outboundText is not null)
            outboundTask = Task.Run(() => Outbound(accepted[0], outboundText, withRecords, outboundTimeoutMs));

        using var windowCts = new CancellationTokenSource(windowMs);
        while (true)
        {
            try
            {
                Socket extra = await listener.AcceptSocketAsync(windowCts.Token);
                accepted.Add(extra);
                Report(extra, accepted.Count - 1);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
        }
        listener.Stop();

        bool ok = accepted.Count == expectedAccepts;
        if (!ok)
            Console.WriteLine($"relay FAIL accepts={accepted.Count} expect={expectedAccepts}");
        if (outboundTask is not null)
            ok &= await outboundTask;

        foreach (Socket socket in accepted)
            socket.Dispose();
        if (lingerMs > 0)
            await Task.Delay(lingerMs);

        Console.WriteLine($"relay done accepts={accepted.Count} exit={(ok ? 0 : 1)}");
        return ok ? 0 : 1;
    }

    private static void Report(Socket socket, int index)
    {
        string context;
        try
        {
            context = $"{WfpSockets.QueryRedirectContext(socket).Length}B";
        }
        catch (Exception)
        {
            context = "0B";
        }
        Console.WriteLine($"relay accept idx={index} context={context} local={socket.LocalEndPoint} remote={socket.RemoteEndPoint}");
        Console.Out.Flush();
    }

    private static bool Outbound(Socket accepted, string targetText, bool withRecords, int timeoutMs)
    {
        byte[]? records = null;
        if (withRecords)
        {
            try
            {
                records = WfpSockets.QueryRedirectRecords(accepted);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"relay FAIL records={ex.GetType().Name}: {ex.Message}");
                return false;
            }
            Console.WriteLine($"relay records={records.Length}B");
            if (records.Length == 0)
                return false;
        }

        (string host, int port) = SocketHelpers.ParseEndPoint(targetText);
        IPAddress address = IPAddress.Parse(host);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        if (records is not null)
        {
            try
            {
                WfpSockets.SetRedirectRecords(socket, records);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"relay FAIL set-records={ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            socket.ConnectAsync(new IPEndPoint(address, port), cts.Token).AsTask().GetAwaiter().GetResult();
            byte[] payload = Encoding.UTF8.GetBytes("ping");
            socket.Send(payload);
            socket.ReceiveTimeout = timeoutMs;
            byte[] buffer = new byte[64];
            int received = socket.Receive(buffer);
            Console.WriteLine($"relay outbound=echo bytes={received.ToString(CultureInfo.InvariantCulture)}");
            return received > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"relay outbound=failed {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
