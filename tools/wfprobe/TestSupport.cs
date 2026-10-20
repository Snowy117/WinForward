using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinForward.Probe;

internal static class Check
{
    public static int Failures;

    public static void True(bool condition, string name, string detail)
    {
        Console.WriteLine($"ASSERT {(condition ? "PASS" : "FAIL")}: {name} ({detail})");
        if (!condition)
            Failures++;
    }

    public static void Equal(long expected, long actual, string name) =>
        True(expected == actual, name, $"expected={expected} actual={actual}");

    public static void AtLeast(long expected, long actual, string name) =>
        True(actual >= expected, name, $"expected>={expected} actual={actual}");

    public static void Contains(string text, string needle, string name) =>
        True(text.Contains(needle, StringComparison.Ordinal), name, $"{needle}");
}

internal sealed class EchoServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task loop;
    private int accepted;

    public int Port { get; }

    public int Accepted => Volatile.Read(ref accepted);

    public EchoServer(IPAddress bind, int port = 0)
    {
        listener = new TcpListener(bind, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        loop = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using Socket client = await listener.AcceptSocketAsync(cancellation.Token);
                Interlocked.Increment(ref accepted);
                byte[] buffer = new byte[64];
                int received = await client.ReceiveAsync(buffer, SocketFlags.None, cancellation.Token);
                if (received > 0)
                    await client.SendAsync(buffer.AsMemory(0, received), SocketFlags.None, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellation.IsCancellationRequested)
                    break;
            }
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        listener.Stop();
        try
        {
            loop.Wait(1000);
        }
        catch
        {
            // 监听循环以取消/关闭套接字的方式退出，等待异常不影响断言。
        }
        cancellation.Dispose();
    }
}

internal static class SocketHelpers
{
    public static bool TryConnect(IPEndPoint target, int timeoutMs, out Socket? socket, out string error)
    {
        socket = null;
        error = string.Empty;
        var candidate = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            candidate.ConnectAsync(target, timeout.Token).AsTask().GetAwaiter().GetResult();
            socket = candidate;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            candidate.Dispose();
            return false;
        }
    }

    public static bool TryEcho(Socket socket, string payload, int timeoutMs, out string detail)
    {
        socket.ReceiveTimeout = timeoutMs;
        socket.SendTimeout = timeoutMs;
        byte[] data = Encoding.UTF8.GetBytes(payload);
        byte[] buffer = new byte[256];
        try
        {
            socket.Send(data);
            int received = socket.Receive(buffer);
            detail = $"echo={received}B";
            return received > 0 && Encoding.UTF8.GetString(buffer, 0, received) == payload;
        }
        catch (Exception ex)
        {
            detail = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public static (string Host, int Port) ParseEndPoint(string text)
    {
        int separator = text.LastIndexOf(':');
        if (separator <= 0)
            throw new ArgumentException($"地址应为 HOST:PORT：{text}");
        return (text[..separator], int.Parse(text[(separator + 1)..]));
    }
}

