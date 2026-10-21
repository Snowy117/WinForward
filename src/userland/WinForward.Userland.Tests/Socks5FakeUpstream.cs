using System.Net;
using System.Net.Sockets;
using System.Text;
using WinForward.Userland.Configuration;

namespace WinForward.Userland.Tests;

/// <summary>假上游的行为剧本；默认是「no-auth 成功、CONNECT 成功、不转发」。</summary>
internal sealed record Socks5FakeOptions
{
    /// <summary>方法协商回复里的方法字节；0xFF 表示拒绝 no-auth。</summary>
    internal byte MethodReply { get; init; } = 0x00;

    /// <summary>CONNECT 回复里的版本字节，用来构造「版本不对」的坏上游。</summary>
    internal byte ReplyVersion { get; init; } = 0x05;

    /// <summary>CONNECT 回复码；非 0 走失败路径。</summary>
    internal byte Rep { get; init; }

    /// <summary>false：读完请求后不回话，用来验证客户端不会挂住。</summary>
    internal bool ReplyConnect { get; init; } = true;

    /// <summary>true：按请求里的目标真的建一条连接，并双向转发。</summary>
    internal bool Forward { get; init; }
}

/// <summary>
/// loopback 上的假 SOCKS5 上游：独立实现服务端那一侧，记录客户端发来的字节，按剧本回话。
/// </summary>
/// <remarks>
/// 刻意不复用产品侧的编解码：替身要能独立指出「产品发出去的字节不对」，
/// 两条路共用一份实现时这类错看不见。一次只服务一条连接，够一个用例用。
/// </remarks>
internal sealed class Socks5FakeUpstream : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Socks5FakeOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _requestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _server;
    private int _connects;

    private Socks5FakeUpstream(TcpListener listener, Socks5FakeOptions options)
    {
        _listener = listener;
        _options = options;
        _server = Task.Run(ServeAsync);
    }

    internal static Socks5FakeUpstream Start(Socks5FakeOptions? options = null)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return new Socks5FakeUpstream(listener, options ?? new Socks5FakeOptions());
    }

    internal Socks5Endpoint Proxy => new("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port);

    /// <summary>客户端发来的方法协商三字节。</summary>
    internal byte[] Greeting { get; private set; } = [];

    /// <summary>客户端发来的完整 CONNECT 请求（含版本与命令）。</summary>
    internal byte[] Request { get; private set; } = [];

    internal int Connects => Volatile.Read(ref _connects);

    /// <summary>剧本没按预期跑完时的原因；客户端提前断开不算。</summary>
    internal string Error { get; private set; } = string.Empty;

    internal void WaitForRequest()
    {
        if (!_requestReceived.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TestFailure($"假上游 5 秒内没有收到 CONNECT 请求（error={Error}）");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _server;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException
                                               or ObjectDisposedException or IOException)
        {
            // 收尾时把 accept 打断，属于正常路径。
        }

        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        try
        {
            using Socket socket = await _listener.AcceptSocketAsync(_stop.Token);
            Greeting = await ReceiveAsync(socket, 3);
            byte[] methodReply = [0x05, _options.MethodReply];
            await socket.SendAsync(methodReply, SocketFlags.None, _stop.Token);
            if (_options.MethodReply != 0x00)
            {
                await HoldAsync(socket);
                return;
            }

            byte[] header = await ReceiveAsync(socket, 4);
            byte[] address;
            switch (header[3])
            {
                case 0x01:
                    address = await ReceiveAsync(socket, 4);
                    break;
                case 0x04:
                    address = await ReceiveAsync(socket, 16);
                    break;
                case 0x03:
                    byte[] length = await ReceiveAsync(socket, 1);
                    address = [.. length, .. await ReceiveAsync(socket, length[0])];
                    break;
                default:
                    throw new TestFailure($"假上游收到未知地址类型 {header[3]}");
            }

            byte[] port = await ReceiveAsync(socket, 2);
            Request = [.. header, .. address, .. port];
            _requestReceived.TrySetResult();

            if (!_options.ReplyConnect)
            {
                await HoldAsync(socket);
                return;
            }

            byte[] connectReply = [_options.ReplyVersion, _options.Rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0];
            await socket.SendAsync(connectReply, SocketFlags.None, _stop.Token);
            if (_options.Rep != 0 || !_options.Forward)
            {
                await HoldAsync(socket);
                return;
            }

            Interlocked.Increment(ref _connects);
            await ForwardAsync(socket, header[3], address, port);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException
                                               or ObjectDisposedException or IOException)
        {
            // 客户端提前关连接或测试收尾：不记为剧本失败。
        }
        catch (Exception exception)
        {
            Error = $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    private async Task ForwardAsync(Socket client, byte addressType, byte[] address, byte[] port)
    {
        int portNumber = (port[0] << 8) | port[1];
        IPEndPoint destination = addressType switch
        {
            0x01 or 0x04 => new IPEndPoint(new IPAddress(address), portNumber),
            0x03 => new IPEndPoint(
                (await Dns.GetHostAddressesAsync(Encoding.ASCII.GetString(address[1..]), _stop.Token))[0], portNumber),
            _ => throw new TestFailure($"假上游无法转发地址类型 {addressType}"),
        };

        using Socket upstream = new(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await upstream.ConnectAsync(destination, _stop.Token);
        Task going = CopyAsync(client, upstream);
        Task coming = CopyAsync(upstream, client);
        await Task.WhenAll(going, coming);
    }

    private async Task CopyAsync(Socket from, Socket to)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int read = await from.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                if (read == 0)
                {
                    break;
                }

                int offset = 0;
                while (offset < read)
                {
                    offset += await to.SendAsync(buffer.AsMemory(offset, read - offset), SocketFlags.None, _stop.Token);
                }
            }

            try
            {
                to.Shutdown(SocketShutdown.Send);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                // 对面已经关了。
            }
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException
                                               or ObjectDisposedException)
        {
            // 一端断开就把另一个方向留在这里收尾。
        }
    }

    private async Task HoldAsync(Socket socket)
    {
        byte[] buffer = new byte[256];
        try
        {
            while (await socket.ReceiveAsync(buffer, SocketFlags.None, _stop.Token) > 0)
            {
            }
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException
                                               or ObjectDisposedException)
        {
            // 客户端用完就关，属于正常路径。
        }
    }

    private async Task<byte[]> ReceiveAsync(Socket socket, int count)
    {
        byte[] data = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await socket.ReceiveAsync(data.AsMemory(offset), SocketFlags.None, _stop.Token);
            if (read == 0)
            {
                throw new TestFailure($"假上游只读到 {offset}/{count} 字节就 EOF");
            }

            offset += read;
        }

        return data;
    }
}
