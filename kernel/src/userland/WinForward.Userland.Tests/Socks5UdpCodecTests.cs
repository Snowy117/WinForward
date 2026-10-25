using System.Net;
using WinForward.Userland.Relay;

namespace WinForward.Userland.Tests;

/// <summary>SOCKS5 UDP 封装的线格式与拒绝路径；不碰网络。</summary>
internal static class Socks5UdpCodecTests
{
    internal static void Register()
    {
        TestRunner.Add("socks5-udp/IPv4 封装：RSV/FRAG/ATYP/地址/端口/payload 的顺序", Ipv4Encode);
        TestRunner.Add("socks5-udp/IPv6 封装与解封装往返一致", Ipv6RoundTrip);
        TestRunner.Add("socks5-udp/FRAG 非 0 的响应被拒绝", FragmentRejected);
        TestRunner.Add("socks5-udp/域名响应被拒绝", DomainRejected);
        TestRunner.Add("socks5-udp/截断响应被拒绝", TruncatedRejected);
        TestRunner.Add("socks5-udp/ASSOCIATE 请求是固定的 10 字节", AssociateRequest);
    }

    private static void Ipv4Encode()
    {
        IPEndPoint destination = new(IPAddress.Parse("10.99.0.1"), 40250);
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] datagram = Socks5UdpCodec.Encode(destination, payload);
        byte[] expected =
        [
            0x00, 0x00, 0x00, 0x01, 0x0A, 0x63, 0x00, 0x01, 0x9D, 0x3A, 0xDE, 0xAD, 0xBE, 0xEF,
        ];
        Check.BytesEqual(expected, datagram, "IPv4 封装字节");
    }

    private static void Ipv6RoundTrip()
    {
        IPEndPoint destination = new(IPAddress.Parse("2001:db8::1"), 53);
        byte[] payload = [0x01, 0x02, 0x03];
        byte[] datagram = Socks5UdpCodec.Encode(destination, payload);
        Check.True(Socks5UdpCodec.TryDecode(datagram, out IPEndPoint? source, out int payloadOffset, out string error),
                   $"解封装成功：{error}");
        Check.True(source is not null && source.Equals(destination), $"源地址往返一致：{source}");
        Check.Equal(payload.Length, datagram.Length - payloadOffset, "payload 长度");
        Check.BytesEqual(payload, datagram.AsSpan(payloadOffset).ToArray(), "payload 往返一致");
    }

    private static void FragmentRejected()
    {
        byte[] datagram =
        [
            0x00, 0x00, 0x01, 0x01, 0x0A, 0x63, 0x00, 0x01, 0x9D, 0x3A, 0xAA,
        ];
        Check.True(!Socks5UdpCodec.TryDecode(datagram, out _, out _, out string error), $"FRAG=1 被拒绝：{error}");
        Check.True(error.Contains("FRAG", StringComparison.Ordinal), $"错误信息带 FRAG：{error}");
    }

    private static void DomainRejected()
    {
        byte[] datagram = [0x00, 0x00, 0x00, 0x03, 0x03, (byte)'a', (byte)'b', (byte)'c', 0x00, 0x35, 0xAA];
        Check.True(!Socks5UdpCodec.TryDecode(datagram, out _, out _, out string error), $"域名响应被拒绝：{error}");
    }

    private static void TruncatedRejected()
    {
        Check.True(!Socks5UdpCodec.TryDecode([0x00, 0x00, 0x00, 0x01, 0x0A], out _, out _, out _), "IPv4 截断");
        Check.True(!Socks5UdpCodec.TryDecode([0x00, 0x00], out _, out _, out _), "短于 4 字节");
    }

    private static void AssociateRequest()
    {
        Check.BytesEqual([0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0], Socks5UdpCodec.BuildAssociateRequest(),
                         "UDP ASSOCIATE 请求");
    }
}
