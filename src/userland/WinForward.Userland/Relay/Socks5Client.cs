using System.Net;
using System.Net.Sockets;
using System.Text;
using WinForward.Userland.Configuration;

namespace WinForward.Userland.Relay;

/// <summary>与 SOCKS5 上游完成 no-auth CONNECT 握手，返回一条已经连到原始目的地的 socket。</summary>
/// <remarks>
/// 失败一律抛 <see cref="Socks5Exception"/>，消息里带上游地址、目标与 rep 码。调用方没有「换条路再试」的分支：
/// 静默退回直连会让「代理没生效」看起来像成功。<see cref="Socket"/> 的建立与超时都发生在这里，
/// 建连失败时不会留下没人持有的句柄。
///
/// 握手只发生一次；返回的 socket 数据面不经过本类，中继拿到就直接转发。
/// 上游域名取解析结果的第一个地址，不做多地址轮试——需要更多策略时再改这里，别在调用方补。
/// </remarks>
public static class Socks5Client
{
    private const byte Version = 0x05;
    private const byte MethodNoAuthentication = 0x00;
    private const byte MethodNoneAcceptable = 0xFF;
    private const byte CommandConnect = 0x01;
    private const byte AddressIpv4 = 0x01;
    private const byte AddressDomain = 0x03;
    private const byte AddressIpv6 = 0x04;
    private const int MaximumDomainLength = 255;

    private static readonly byte[] Greeting = [Version, 0x01, MethodNoAuthentication];

    /// <param name="proxy">上游 SOCKS5 服务。</param>
    /// <param name="destination">原始目的地：IPv4、IPv6 或域名。</param>
    /// <param name="prepareSocket">socket 刚建好、还没 connect 时调用；中继用它挂 redirect records。</param>
    /// <param name="timeout">建连加握手的预算，从调用开始计。</param>
    /// <param name="token">控制器停止信号；取消时抛 <see cref="OperationCanceledException"/>，不包装成握手失败。</param>
    public static async Task<Socket> ConnectAsync(Socks5Endpoint proxy,
                                                  EndPoint destination,
                                                  Action<Socket>? prepareSocket,
                                                  TimeSpan timeout,
                                                  CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(destination);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "SOCKS5 建连超时必须大于 0");
        }

        // 先把目标编码校验完，再碰网络：编不进去的目标不该先花一次 DNS 与 TCP。
        byte[] request = BuildConnectRequest(destination);
        string target = destination.ToString() ?? "?";

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);

        Socket? socket = null;
        try
        {
            IPAddress upstream = await ResolveAsync(proxy, deadline.Token);
            socket = new Socket(upstream.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            prepareSocket?.Invoke(socket);

            await socket.ConnectAsync(new IPEndPoint(upstream, proxy.Port), deadline.Token);
            await SendAllAsync(socket, Greeting, deadline.Token);
            await ExpectNoAuthAsync(socket, proxy, deadline.Token);
            await SendAllAsync(socket, request, deadline.Token);
            await ExpectConnectAsync(socket, proxy, target, deadline.Token);

            Socket connected = socket;
            socket = null;
            return connected;
        }
        catch (Socks5Exception)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        {
            throw new Socks5Exception(
                $"SOCKS5 上游 {proxy} 在 {timeout.TotalSeconds:0.###} 秒内没有完成 CONNECT {target}", null, exception);
        }
        catch (SocketException exception)
        {
            throw new Socks5Exception($"连接 SOCKS5 上游 {proxy} 失败（目标 {target}）：{exception.Message}", null,
                                      exception);
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private static async Task<IPAddress> ResolveAsync(Socks5Endpoint proxy, CancellationToken token)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(proxy.Host, token);
        if (addresses.Length == 0)
        {
            throw new Socks5Exception($"SOCKS5 上游 {proxy} 的域名没有解析出任何地址");
        }

        return addresses[0];
    }

    private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            sent += await socket.SendAsync(data[sent..], SocketFlags.None, token);
        }
    }

    private static async Task ReceiveExactlyAsync(Socket socket,
                                                  Memory<byte> buffer,
                                                  Socks5Endpoint proxy,
                                                  string target,
                                                  CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await socket.ReceiveAsync(buffer[offset..], SocketFlags.None, token);
            if (read == 0)
            {
                throw new Socks5Exception($"SOCKS5 上游 {proxy} 在握手途中关闭了连接（目标 {target}）");
            }

            offset += read;
        }
    }

    private static async Task ExpectNoAuthAsync(Socket socket, Socks5Endpoint proxy, CancellationToken token)
    {
        byte[] reply = new byte[2];
        await ReceiveExactlyAsync(socket, reply, proxy, "握手", token);
        if (reply[0] != Version)
        {
            throw new Socks5Exception($"SOCKS5 上游 {proxy} 的问候回复版本是 {reply[0]}，期望 {Version}");
        }

        if (reply[1] == MethodNoneAcceptable)
        {
            throw new Socks5Exception($"SOCKS5 上游 {proxy} 拒绝了 no-auth（回复 0xFF），本客户端不支持认证");
        }

        if (reply[1] != MethodNoAuthentication)
        {
            throw new Socks5Exception($"SOCKS5 上游 {proxy} 选择的方法 0x{reply[1]:X2} 不是 no-auth（0x00）");
        }
    }

    private static async Task ExpectConnectAsync(Socket socket,
                                                 Socks5Endpoint proxy,
                                                 string target,
                                                 CancellationToken token)
    {
        byte[] header = new byte[4];
        await ReceiveExactlyAsync(socket, header, proxy, target, token);
        if (header[0] != Version)
        {
            throw new Socks5Exception($"SOCKS5 上游 {proxy} 的 CONNECT 回复版本是 {header[0]}，期望 {Version}");
        }

        byte rep = header[1];
        if (rep != 0)
        {
            throw new Socks5Exception($"SOCKS5 上游 {proxy} 拒绝 CONNECT {target}：rep={rep}（{DescribeRep(rep)}）", rep);
        }

        int addressLength;
        switch (header[3])
        {
            case AddressIpv4:
                addressLength = 4;
                break;
            case AddressIpv6:
                addressLength = 16;
                break;
            case AddressDomain:
                byte[] length = new byte[1];
                await ReceiveExactlyAsync(socket, length, proxy, target, token);
                addressLength = length[0];
                break;
            default:
                throw new Socks5Exception($"SOCKS5 上游 {proxy} 的回复里出现未知地址类型 {header[3]}（目标 {target}）");
        }

        // BND.ADDR / BND.PORT 留着不解析，但必须读掉：它们是这一帧的一部分。
        byte[] bound = new byte[addressLength + 2];
        await ReceiveExactlyAsync(socket, bound, proxy, target, token);
    }

    private static byte[] BuildConnectRequest(EndPoint destination)
    {
        switch (destination)
        {
            case IPEndPoint { AddressFamily: AddressFamily.InterNetwork } ipv4:
                RequirePort(ipv4.Port, destination);
                byte[] v4 = new byte[10];
                WriteHeader(v4, AddressIpv4);
                ipv4.Address.GetAddressBytes().CopyTo(v4, 4);
                WritePort(v4, 8, ipv4.Port);
                return v4;

            case IPEndPoint { AddressFamily: AddressFamily.InterNetworkV6 } ipv6:
                RequirePort(ipv6.Port, destination);
                byte[] v6 = new byte[22];
                WriteHeader(v6, AddressIpv6);
                ipv6.Address.GetAddressBytes().CopyTo(v6, 4);
                WritePort(v6, 20, ipv6.Port);
                return v6;

            case DnsEndPoint domain:
                return BuildDomainRequest(domain);

            default:
                throw new ArgumentException($"SOCKS5 不认识的地址类型：{destination.GetType().Name}", nameof(destination));
        }
    }

    private static byte[] BuildDomainRequest(DnsEndPoint destination)
    {
        RequirePort(destination.Port, destination);
        string host = destination.Host.Trim();
        if (host.Length == 0)
        {
            throw new Socks5Exception("SOCKS5 的域名目标为空");
        }

        if (host.Length > MaximumDomainLength)
        {
            throw new Socks5Exception($"SOCKS5 域名 {host} 有 {host.Length} 个字符，超过 {MaximumDomainLength} 的上限");
        }

        foreach (char character in host)
        {
            if (character > 0x7F)
            {
                throw new Socks5Exception($"SOCKS5 域名只接受 ASCII，{host} 含非 ASCII 字符");
            }
        }

        byte[] name = Encoding.ASCII.GetBytes(host);
        byte[] request = new byte[5 + name.Length + 2];
        WriteHeader(request, AddressDomain);
        request[4] = (byte)name.Length;
        name.CopyTo(request, 5);
        WritePort(request, 5 + name.Length, destination.Port);
        return request;
    }

    private static void WriteHeader(byte[] request, byte addressType)
    {
        request[0] = Version;
        request[1] = CommandConnect;
        request[2] = 0x00;
        request[3] = addressType;
    }

    private static void WritePort(byte[] request, int offset, int port)
    {
        request[offset] = (byte)(port >> 8);
        request[offset + 1] = (byte)(port & 0xFF);
    }

    private static void RequirePort(int port, EndPoint destination)
    {
        if (port is < 1 or > 65535)
        {
            throw new Socks5Exception($"目标 {destination} 的端口 {port} 无法写进 SOCKS5 请求");
        }
    }

    private static string DescribeRep(byte rep) => rep switch
    {
        0x01 => "一般失败",
        0x02 => "规则不允许",
        0x03 => "网络不可达",
        0x04 => "主机不可达",
        0x05 => "连接被拒",
        0x06 => "TTL 过期",
        0x07 => "不支持的命令",
        0x08 => "不支持的地址类型",
        _ => "未定义",
    };
}
