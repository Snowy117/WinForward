using System.Buffers.Binary;
using System.Net;
using WinForward.Protocols;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class ProtocolAuditTests
{
    private static readonly IPAddress s_ipv4Source = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_ipv4Destination = IPAddress.Parse("192.0.2.53");
    private static readonly IPAddress s_ipv6Source = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress s_ipv6Destination = IPAddress.Parse("2001:db8::53");
    private static readonly byte[] s_sourceMac = [0x02, 0, 0, 0, 0, 1];
    private static readonly byte[] s_destinationMac = [0x02, 0, 0, 0, 0, 2];

    [Fact]
    public void TcpParserRejectsShortDeclaredSegmentWithoutThrowing()
    {
        var frame = new byte[14 + 20 + 8];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 28);
        frame[23] = 6;

        Assert.False(IpTcpUdpPacket.TryParse(frame, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void UdpIpv6RewriterRejectsShortPayloadWithoutMutating(int payloadLength)
    {
        var frame = new byte[14 + 40 + payloadLength];
        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), (ushort)payloadLength);
        frame[20] = 17;
        var original = frame.ToArray();

        Assert.False(PacketChecksums.TryRewriteUdpEndpoints(frame, s_ipv6Source, 1, s_ipv6Destination, 2));
        Assert.Equal(original, frame);
    }

    [Fact]
    public void ReservedIpv4FragmentFlagIsRejectedWithoutMutating()
    {
        var tcp = CreateIpv4TcpFrame();
        var udp = CreateIpv4UdpFrame();
        tcp[20] = 0x80;
        udp[20] = 0x80;
        var tcpOriginal = tcp.ToArray();
        var udpOriginal = udp.ToArray();

        Assert.False(IpTcpUdpPacket.TryParse(tcp, out _));
        Assert.False(IpUdpPacket.TryParse(udp, out _));
        Assert.False(PacketChecksums.TryRewriteTcpEndpoints(tcp, s_ipv4Destination, 1, s_ipv4Source, 2));
        Assert.False(PacketChecksums.TryRewriteUdpEndpoints(udp, s_ipv4Destination, 1, s_ipv4Source, 2));
        Assert.Equal(tcpOriginal, tcp);
        Assert.Equal(udpOriginal, udp);
    }

    [Fact]
    public void SocksReplyParsersRejectInvalidFixedFields()
    {
        var nonZeroReserved = new byte[] { 5, 0, 1, 1, 192, 0, 2, 53, 0x14, 0xe9 };
        var unknownAddressType = new byte[] { 5, 0, 0, 9, 0, 0, 0, 0 };
        var unknownReplyStatus = new byte[] { 5, 9, 0, 1, 192, 0, 2, 53, 0x14, 0xe9 };

        Assert.Equal(Socks5ReplyKind.Invalid, Socks5Messages.TryParseReply(nonZeroReserved, out _, out _, out _));
        Assert.Equal(Socks5ReplyKind.Invalid, Socks5Messages.TryParseReply(unknownAddressType, out _, out _, out _));
        Assert.Equal(Socks5ReplyKind.Invalid, Socks5Messages.TryParseReply(unknownReplyStatus, out _, out _, out _));
        Assert.False(Socks5Messages.TryParseReplyPrefix(new byte[] { 5, 0 }, out _));
        Assert.False(Socks5Messages.TryParseReplyPrefix(nonZeroReserved, out _));
        Assert.False(Socks5Messages.TryParseReplyPrefix(unknownReplyStatus, out _));
        Assert.False(Socks5Messages.TryGetReplyLength(unknownAddressType, out _));
    }

    [Fact]
    public void InternetChecksumFoldsLargeGenericInputWithoutOverflow()
    {
        var data = new byte[131_076];
        Array.Fill(data, byte.MaxValue);

        Assert.Equal((ushort)0, PacketChecksums.InternetChecksum(data));
    }

    [Fact]
    public void FrameBuilderRejectsPayloadsBeyondWireLengthFields()
    {
        var payload = new byte[65_528];

        Assert.False(UdpFrameBuilder.TryBuild(s_ipv4Source, 1, s_ipv4Destination, 2, payload, s_sourceMac, s_destinationMac, out var ipv4Frame, 65_570));
        Assert.Empty(ipv4Frame);
        Assert.False(UdpFrameBuilder.TryBuild(s_ipv6Source, 1, s_ipv6Destination, 2, payload, s_sourceMac, s_destinationMac, out var ipv6Frame, 65_590));
        Assert.Empty(ipv6Frame);
    }

    private static byte[] CreateIpv4TcpFrame()
    {
        var frame = new byte[14 + 20 + 20];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 40);
        frame[23] = 6;
        frame[46] = 0x50;
        return frame;
    }

    private static byte[] CreateIpv4UdpFrame()
    {
        var frame = new byte[14 + 20 + 8];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 28);
        frame[23] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38, 2), 8);
        return frame;
    }
}
