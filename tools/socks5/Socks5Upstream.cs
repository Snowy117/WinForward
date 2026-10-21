using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinForward.Socks5Tool;

/// <summary>
/// 最小 SOCKS5 服务端：no-auth + CONNECT，IPv4 / IPv6 / 域名三种目标，双向透传并计数。
/// </summary>
/// <remarks>
/// 这是测试替身，不是产品代码，也不引用产品工程：线与产品侧独立实现，
/// 这样它记录的每一字节都是对产品客户端的独立核对，而不是同一份编解码的自证。
/// 反例：替身和客户端共用一份编解码时，两边一起写错也能全绿。
/// </remarks>
internal static class Socks5Upstream
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<int> RunAsync(IPAddress listen,
                                             int port,
                                             int expectedConnections,
                                             int timeoutMs,
                                             Reporter report)
    {
        using Socket listener = Bind(listen, port);
        report.WriteLine($"socks5-ready listen={listener.LocalEndPoint} pid={Environment.ProcessId}");

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
                counters.Accepted();
                sessions.Add(ServeAsync(id, client, counters, report, linked.Token));
            }

            await Task.WhenAll(sessions);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        bool timedOut = timeout.IsCancellationRequested && !stop.IsCancellationRequested;
        string summary = $"connections={counters.Connections} connects={counters.Connects} " +
                         $"bytes_in={Interlocked.Read(ref counters.BytesIn)} " +
                         $"bytes_out={Interlocked.Read(ref counters.BytesOut)}";
        if (timedOut)
        {
            report.WriteLine($"socks5-timeout {summary}");
            return 1;
        }

        report.WriteLine($"socks5-done {summary}");
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
            try
            {
                client.NoDelay = true;
                byte[] greeting = await ReceiveAsync(client, 2, token);
                byte[] methods = await ReceiveAsync(client, greeting[1], token);
                if (greeting[0] != 0x05 || !methods.Contains((byte)0x00))
                {
                    await SendAsync(client, [0x05, 0xFF], token);
                    report.WriteLine($"socks5 reject #{id} from={peer} reason=no-auth-unsupported");
                    return;
                }

                await SendAsync(client, [0x05, 0x00], token);

                byte[] header = await ReceiveAsync(client, 4, token);
                if (header[0] != 0x05 || header[1] != 0x01)
                {
                    await ReplyAsync(client, 0x07, token);
                    report.WriteLine($"socks5 reject #{id} from={peer} reason=command-unsupported");
                    return;
                }

                (IPEndPoint? target, string targetText) = await ReadTargetAsync(client, header[3], token);
                if (target is null)
                {
                    await ReplyAsync(client, 0x08, token);
                    report.WriteLine($"socks5 reject #{id} from={peer} atyp={header[3]} reason=address-unsupported");
                    return;
                }

                report.WriteLine($"socks5 accept #{id} from={peer} atyp={header[3]} target={targetText}");
                using Socket outbound = new(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    using CancellationTokenSource connectTimeout =
                        CancellationTokenSource.CreateLinkedTokenSource(token);
                    connectTimeout.CancelAfter(ConnectTimeout);
                    await outbound.ConnectAsync(target, connectTimeout.Token);
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                {
                    byte rep = exception is SocketException socketException
                                   ? MapConnectError(socketException.SocketErrorCode)
                                   : (byte)0x06;
                    await ReplyAsync(client, rep, token);
                    report.WriteLine($"socks5 connect #{id} target={targetText} ok=false rep={rep} " +
                                     $"detail={exception.Message}");
                    return;
                }

                await ReplyAsync(client, 0x00, token);
                counters.Connected();
                report.WriteLine($"socks5 connect #{id} target={targetText} ok=true");

                Task<long> going = SocketCopy.CopyAsync(client, outbound, token);
                Task<long> coming = SocketCopy.CopyAsync(outbound, client, token);
                await Task.WhenAll(going, coming);
                counters.AddBytes(going.Result, coming.Result);
                report.WriteLine($"socks5 close #{id} in={going.Result} out={coming.Result}");
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException or IOException
                                                   or ObjectDisposedException)
            {
                report.WriteLine($"socks5 error #{id} {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                counters.Completed();
            }
        }
    }

    private static async Task<(IPEndPoint? Target, string Text)> ReadTargetAsync(Socket client,
                                                                                byte addressType,
                                                                                CancellationToken token)
    {
        IPAddress address;
        string text;
        switch (addressType)
        {
            case 0x01:
            case 0x04:
                address = new IPAddress(await ReceiveAsync(client, addressType == 0x01 ? 4 : 16, token));
                text = address.ToString();
                break;

            case 0x03:
                byte length = (await ReceiveAsync(client, 1, token))[0];
                if (length == 0)
                {
                    return (null, string.Empty);
                }

                string host = Encoding.ASCII.GetString(await ReceiveAsync(client, length, token));
                IPAddress[] resolved = await Dns.GetHostAddressesAsync(host, token);
                if (resolved.Length == 0)
                {
                    return (null, host);
                }

                address = resolved[0];
                text = host;
                break;

            default:
                return (null, string.Empty);
        }

        byte[] portBytes = await ReceiveAsync(client, 2, token);
        int port = (portBytes[0] << 8) | portBytes[1];
        if (port == 0)
        {
            return (null, text);
        }

        return (new IPEndPoint(address, port), $"{text}:{port}");
    }

    private static async Task ReplyAsync(Socket client, byte rep, CancellationToken token) =>
        await SendAsync(client, [0x05, rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0], token);

    private static async Task SendAsync(Socket client, byte[] data, CancellationToken token)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            sent += await client.SendAsync(data.AsMemory(sent), SocketFlags.None, token);
        }
    }

    private static async Task<byte[]> ReceiveAsync(Socket client, int count, CancellationToken token)
    {
        byte[] data = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await client.ReceiveAsync(data.AsMemory(offset), SocketFlags.None, token);
            if (read == 0)
            {
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            offset += read;
        }

        return data;
    }

    private static byte MapConnectError(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => 0x05,
        SocketError.HostUnreachable => 0x04,
        SocketError.NetworkUnreachable => 0x03,
        SocketError.TimedOut => 0x04,
        _ => 0x01,
    };

    private static Socket Bind(IPAddress address, int port)
    {
        Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(address, port));
        listener.Listen(64);
        return listener;
    }
}

/// <summary>替身的计数器：一条连接一行、结束时一行汇总。</summary>
internal sealed class Counters
{
    private readonly int _expected;
    private readonly CancellationTokenSource _stop;
    private int _completed;

    internal Counters(int expected, CancellationTokenSource stop)
    {
        _expected = expected;
        _stop = stop;
    }

    internal int NextId;
    internal int Connections;
    internal int Connects;
    internal long BytesIn;
    internal long BytesOut;

    internal void Accepted() => Interlocked.Increment(ref Connections);

    internal void Connected() => Interlocked.Increment(ref Connects);

    internal void AddBytes(long clientToTarget, long targetToClient)
    {
        Interlocked.Add(ref BytesIn, clientToTarget);
        Interlocked.Add(ref BytesOut, targetToClient);
    }

    internal void Completed()
    {
        if (Interlocked.Increment(ref _completed) >= _expected && _expected > 0)
        {
            _stop.Cancel();
        }
    }
}
