using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinForward.Userland.Configuration;

namespace WinForward.Userland.Relay;

/// <summary>
/// 一条 SOCKS5 UDP ASSOCIATE：控制连接保持到关联释放，数据走一个独立的 UDP socket。
/// 封装/解封装由 <see cref="Socks5UdpCodec"/> 完成；这里只负责建链、保活与空闲判定。
/// </summary>
public sealed class Socks5UdpAssociation : IAsyncDisposable
{
    private const byte Version = 0x05;
    private const byte MethodNoAuthentication = 0x00;
    private const byte MethodNoneAcceptable = 0xFF;
    private const byte CommandAssociate = 0x03;
    private const byte AddressIpv4 = 0x01;
    private const byte AddressDomain = 0x03;
    private const byte AddressIpv6 = 0x04;

    private readonly Socket _control;
    private readonly Socket _data;
    private long _lastActivity = Stopwatch.GetTimestamp();

    private Socks5UdpAssociation(Socket control, Socket data, IPEndPoint relayEndPoint)
    {
        _control = control;
        _data = data;
        RelayEndPoint = relayEndPoint;
    }

    public IPEndPoint RelayEndPoint { get; }

    public TimeSpan IdleFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivity));

    public void Touch() => Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());

    public static async Task<Socks5UdpAssociation> EstablishAsync(Socks5Endpoint proxy,
                                                                  TimeSpan timeout,
                                                                  CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "SOCKS5 UDP 建链超时必须大于 0");
        }

        string target = proxy.ToString();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);

        Socket? control = null;
        Socket? data = null;
        try
        {
            IPAddress upstream = await ResolveAsync(proxy, deadline.Token);
            control = new Socket(upstream.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await control.ConnectAsync(new IPEndPoint(upstream, proxy.Port), deadline.Token);

            await control.SendAsync(new byte[] { Version, 0x01, MethodNoAuthentication }, deadline.Token);
            byte[] greeting = new byte[2];
            await ReadExactlyAsync(control, greeting, target, deadline.Token);
            if (greeting[0] != Version || greeting[1] == MethodNoneAcceptable)
            {
                throw new Socks5Exception($"SOCKS5 上游 {target} 拒绝 UDP ASSOCIATE 的 no-auth 协商");
            }

            if (greeting[1] != MethodNoAuthentication)
            {
                throw new Socks5Exception($"SOCKS5 上游 {target} 选择的方法 0x{greeting[1]:X2} 不是 no-auth");
            }

            await control.SendAsync(Socks5UdpCodec.BuildAssociateRequest(), deadline.Token);
            byte[] reply = new byte[4];
            await ReadExactlyAsync(control, reply, target, deadline.Token);
            if (reply[0] != Version)
            {
                throw new Socks5Exception($"SOCKS5 上游 {target} 的 UDP ASSOCIATE 回复版本是 {reply[0]}");
            }

            if (reply[1] != 0x00)
            {
                throw new Socks5Exception($"SOCKS5 上游 {target} 拒绝 UDP ASSOCIATE：rep={reply[1]}");
            }

            IPAddress? boundAddress;
            int addressLength;
            switch (reply[3])
            {
                case AddressIpv4:
                    addressLength = 4;
                    byte[] v4 = new byte[addressLength];
                    await ReadExactlyAsync(control, v4, target, deadline.Token);
                    boundAddress = new IPAddress(v4);
                    break;

                case AddressIpv6:
                    addressLength = 16;
                    byte[] v6 = new byte[addressLength];
                    await ReadExactlyAsync(control, v6, target, deadline.Token);
                    boundAddress = new IPAddress(v6);
                    break;

                case AddressDomain:
                    byte[] length = new byte[1];
                    await ReadExactlyAsync(control, length, target, deadline.Token);
                    byte[] domain = new byte[length[0]];
                    await ReadExactlyAsync(control, domain, target, deadline.Token);
                    try
                    {
                        boundAddress = IPAddress.Parse(Encoding.ASCII.GetString(domain));
                    }
                    catch (FormatException exception)
                    {
                        throw new Socks5Exception($"SOCKS5 上游 {target} 的 UDP ASSOCIATE 域名回复不是 IP：{exception.Message}");
                    }
                    break;

                default:
                    throw new Socks5Exception($"SOCKS5 上游 {target} 的 UDP ASSOCIATE 回复地址类型 {reply[3]} 未知");
            }

            byte[] portBytes = new byte[2];
            await ReadExactlyAsync(control, portBytes, target, deadline.Token);
            int port = (portBytes[0] << 8) | portBytes[1];
            if (port == 0)
            {
                throw new Socks5Exception($"SOCKS5 上游 {target} 的 UDP ASSOCIATE 没有返回中继端口");
            }

            if (boundAddress is null || boundAddress.Equals(IPAddress.Any) || boundAddress.Equals(IPAddress.IPv6Any))
            {
                boundAddress = ((IPEndPoint)control.RemoteEndPoint!).Address;
            }

            IPEndPoint relay = new(boundAddress, port);
            data = new Socket(relay.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            data.ReceiveBufferSize = 1 << 20;
            data.SendBufferSize = 1 << 20;
            data.Connect(relay);

            Socks5UdpAssociation association = new(control, data, relay);
            control = null;
            data = null;
            return association;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new Socks5Exception($"SOCKS5 上游 {target} 在 {timeout.TotalSeconds:0.###} 秒内没有完成 UDP ASSOCIATE");
        }
        finally
        {
            control?.Dispose();
            data?.Dispose();
        }
    }

    public async ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        byte[] datagram = Socks5UdpCodec.Encode(destination, payload.Span);
        await _data.SendAsync(datagram, SocketFlags.None, token);
        Touch();
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
    {
        int received = await _data.ReceiveAsync(buffer, SocketFlags.None, token);
        Touch();
        return received;
    }

    private static async Task<IPAddress> ResolveAsync(Socks5Endpoint proxy, CancellationToken token)
    {
        if (IPAddress.TryParse(proxy.Host, out IPAddress? literal))
        {
            return literal;
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(proxy.Host, token);
        if (addresses.Length == 0)
        {
            throw new Socks5Exception($"SOCKS5 上游 {proxy} 的域名没有解析出任何地址");
        }

        return addresses[0];
    }

    private static async Task ReadExactlyAsync(Socket socket, Memory<byte> buffer, string target, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await socket.ReceiveAsync(buffer[offset..], SocketFlags.None, token);
            if (read == 0)
            {
                throw new Socks5Exception($"SOCKS5 上游 {target} 在 UDP ASSOCIATE 握手中关闭了连接");
            }

            offset += read;
        }
    }

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        _control.Dispose();
        return ValueTask.CompletedTask;
    }
}
