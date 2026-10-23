using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinForward.Userland.Relay;

/// <summary>
/// SOCKS5 UDP 请求/响应的封装与解封装（RFC 1928 §7）。纯函数，便于在 Linux 上单测；
/// 只接受 IP 目标（驱动给的是原始服务器 IP，不存在域名解析问题）。
/// </summary>
public static class Socks5UdpCodec
{
    public const byte Version = 0x05;
    public const byte FragmentNone = 0x00;

    private const byte AddressIpv4 = 0x01;
    private const byte AddressDomain = 0x03;
    private const byte AddressIpv6 = 0x04;

    /// <summary>封装一条 datagram：RSV(2) + FRAG(1) + ATYP + DST.ADDR + DST.PORT + payload。</summary>
    public static byte[] Encode(IPEndPoint destination, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(destination);
        byte[] address = destination.Address.GetAddressBytes();
        int headerLength = 4 + address.Length + 2;
        byte[] datagram = new byte[headerLength + payload.Length];
        datagram[0] = 0x00;
        datagram[1] = 0x00;
        datagram[2] = FragmentNone;
        datagram[3] = address.Length == 4 ? AddressIpv4 : AddressIpv6;
        address.CopyTo(datagram, 4);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(4 + address.Length), (ushort)destination.Port);
        payload.CopyTo(datagram.AsSpan(headerLength));
        return datagram;
    }

    /// <summary>
    /// 解封装一条响应。FRAG != 0、地址类型未知或域名地址一律拒绝；<paramref name="payloadOffset"/>
    /// 是 payload 在输入缓冲区里的起点，调用方可以零拷贝转发。
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> datagram,
                                 out IPEndPoint? source,
                                 out int payloadOffset,
                                 out string error)
    {
        source = null;
        payloadOffset = 0;
        error = string.Empty;

        if (datagram.Length < 4)
        {
            error = "SOCKS5 UDP 响应短于 4 字节";
            return false;
        }

        if (datagram[0] != 0x00 || datagram[1] != 0x00)
        {
            error = "SOCKS5 UDP 响应的 RSV 不是 0";
            return false;
        }

        if (datagram[2] != FragmentNone)
        {
            error = $"SOCKS5 UDP 响应带分片（FRAG={datagram[2]}），不支持重组";
            return false;
        }

        int offset = 4;
        IPAddress address;
        switch (datagram[3])
        {
            case AddressIpv4:
                if (datagram.Length < offset + 4 + 2)
                {
                    error = "SOCKS5 UDP 响应的 IPv4 地址被截断";
                    return false;
                }
                address = new IPAddress(datagram.Slice(offset, 4));
                offset += 4;
                break;

            case AddressIpv6:
                if (datagram.Length < offset + 16 + 2)
                {
                    error = "SOCKS5 UDP 响应的 IPv6 地址被截断";
                    return false;
                }
                address = new IPAddress(datagram.Slice(offset, 16));
                offset += 16;
                break;

            case AddressDomain:
                // 请求侧只会写 IP；响应里出现域名说明上游行为异常，拒绝比在热路径里做 DNS 安全。
                error = "SOCKS5 UDP 响应的地址类型是域名，本实现不接受";
                return false;

            default:
                error = $"SOCKS5 UDP 响应的地址类型 {datagram[3]} 未知";
                return false;
        }

        int port = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(offset, 2));
        offset += 2;
        if (port == 0)
        {
            error = "SOCKS5 UDP 响应的端口是 0";
            return false;
        }

        source = new IPEndPoint(address, port);
        payloadOffset = offset;
        return true;
    }

    /// <summary>UDP ASSOCIATE 请求：CMD=0x03，DST.ADDR/PORT 全 0（由代理按控制连接推断）。</summary>
    public static byte[] BuildAssociateRequest() => [Version, 0x03, 0x00, AddressIpv4, 0, 0, 0, 0, 0, 0];

    /// <summary>构造域名编码的请求（测试与将来扩展用）；端口越界或域名非 ASCII 时抛异常。</summary>
    public static byte[] EncodeDomain(string host, int port, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "端口必须在 1..65535");
        }

        if (host.Length == 0 || host.Length > 255)
        {
            throw new ArgumentException("域名长度必须在 1..255", nameof(host));
        }

        foreach (char character in host)
        {
            if (character > 0x7F)
            {
                throw new ArgumentException("SOCKS5 UDP 域名只接受 ASCII", nameof(host));
            }
        }

        byte[] name = Encoding.ASCII.GetBytes(host);
        byte[] datagram = new byte[4 + 1 + name.Length + 2 + payload.Length];
        datagram[3] = AddressDomain;
        datagram[4] = (byte)name.Length;
        name.CopyTo(datagram, 5);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(5 + name.Length), (ushort)port);
        payload.CopyTo(datagram.AsSpan(7 + name.Length));
        return datagram;
    }
}
