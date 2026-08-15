using System.Buffers.Binary;
using System.Net;
using WinForward.Protocols;
using Xunit;

namespace WinForward.Core.Tests;

public class TcpResetBuilderTests
{
    private static readonly IPAddress s_serverV4 = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress s_clientV4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_serverV6 = IPAddress.Parse("2001:db8::53");
    private static readonly IPAddress s_clientV6 = IPAddress.Parse("2001:db8::10");

    [Fact]
    public void BuildResetIpv4ProducesWellFormedResetAck()
    {
        var template = BuildTemplateSyn(0x0800, srcMac: 0x11, dstMac: 0x22);
        var frame = TcpResetBuilder.BuildReset(template, s_serverV4, 443, s_clientV4, 53000, 1001, 2002);

        Assert.NotNull(frame);
        Assert.Equal(14 + 20 + 20, frame.Length);
        // MACs mirrored and swapped from the template.
        Assert.Equal(0x11, frame[0]);
        Assert.Equal(0x22, frame[6]);
        Assert.Equal(0x0800, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2)));
        // IPv4 header.
        Assert.Equal(0x45, frame[14]);
        Assert.Equal(40, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(16, 2)));
        Assert.Equal(6, frame[23]);
        Assert.Equal(s_serverV4, new IPAddress(frame.AsSpan(26, 4).ToArray()));
        Assert.Equal(s_clientV4, new IPAddress(frame.AsSpan(30, 4).ToArray()));
        Assert.Equal(0, IndependentChecksum(frame.AsSpan(14, 20)));
        // TCP header.
        Assert.Equal(443, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(34, 2)));
        Assert.Equal(53000, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(36, 2)));
        Assert.Equal(1001u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(38, 4)));
        Assert.Equal(2002u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(42, 4)));
        Assert.Equal(0x50, frame[46]);
        Assert.Equal(0x14, frame[47]);
        Assert.Equal(0, TcpChecksumContribution(frame.AsSpan(26, 8), frame.AsSpan(34, 20), isIpv6: false));
    }

    [Fact]
    public void BuildResetIpv6ProducesWellFormedResetAck()
    {
        var template = BuildTemplateSyn(0x86dd, srcMac: 0x33, dstMac: 0x44);
        var frame = TcpResetBuilder.BuildReset(template, s_serverV6, 443, s_clientV6, 53000, 77, 88);

        Assert.NotNull(frame);
        Assert.Equal(14 + 40 + 20, frame.Length);
        Assert.Equal(0x33, frame[0]);
        Assert.Equal(0x44, frame[6]);
        Assert.Equal(0x86dd, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2)));
        Assert.Equal(0x60, frame[14] & 0xf0);
        Assert.Equal(20, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(18, 2)));
        Assert.Equal(6, frame[20]);
        Assert.Equal(s_serverV6, new IPAddress(frame.AsSpan(22, 16).ToArray()));
        Assert.Equal(s_clientV6, new IPAddress(frame.AsSpan(38, 16).ToArray()));
        var tcp = frame.AsSpan(54, 20);
        Assert.Equal(443, BinaryPrimitives.ReadUInt16BigEndian(tcp.Slice(0, 2)));
        Assert.Equal(53000, BinaryPrimitives.ReadUInt16BigEndian(tcp.Slice(2, 2)));
        Assert.Equal(77u, BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(4, 4)));
        Assert.Equal(88u, BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(8, 4)));
        Assert.Equal(0x50, tcp[12]);
        Assert.Equal(0x14, tcp[13]);
        Assert.Equal(0, TcpChecksumContribution(frame.AsSpan(22, 32), tcp, isIpv6: true));
    }

    [Fact]
    public void BuildResetRejectsUnusableTemplates()
    {
        Assert.Null(TcpResetBuilder.BuildReset(new byte[10], s_serverV4, 443, s_clientV4, 53000, 1, 1));
        var arp = BuildTemplateSyn(0x0806, 0x11, 0x22);
        Assert.Null(TcpResetBuilder.BuildReset(arp, s_serverV4, 443, s_clientV4, 53000, 1, 1));
        var v4 = BuildTemplateSyn(0x0800, 0x11, 0x22);
        Assert.Null(TcpResetBuilder.BuildReset(v4, s_serverV6, 443, s_clientV6, 53000, 1, 1));
        Assert.Null(TcpResetBuilder.BuildReset(v4, s_serverV4, 443, s_clientV6, 53000, 1, 1));
    }

    private static byte[] BuildTemplateSyn(ushort etherType, byte srcMac, byte dstMac)
    {
        var frame = new byte[14 + 20 + 20];
        frame[0] = dstMac;
        frame[6] = srcMac;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12, 2), etherType);
        return frame;
    }

    // Independent checksum verification: naive one's-complement fold over 16-bit words. A frame
    // region whose embedded checksum is correct folds to zero.
    private static ushort IndependentChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var index = 0; index + 1 < data.Length; index += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
        }
        if (data.Length % 2 != 0) sum += (uint)(data[^1] << 8);
        while ((sum >> 16) != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }

    private static ushort TcpChecksumContribution(ReadOnlySpan<byte> addresses, ReadOnlySpan<byte> tcpSegment, bool isIpv6)
    {
        var buffer = new byte[addresses.Length + 12 + tcpSegment.Length];
        addresses.CopyTo(buffer);
        if (isIpv6)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(addresses.Length, 4), (uint)tcpSegment.Length);
            buffer[addresses.Length + 7] = 6;
            tcpSegment.CopyTo(buffer.AsSpan(addresses.Length + 8));
            return IndependentChecksum(buffer.AsSpan(0, addresses.Length + 8 + tcpSegment.Length));
        }
        buffer[addresses.Length + 1] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(addresses.Length + 2, 2), (ushort)tcpSegment.Length);
        tcpSegment.CopyTo(buffer.AsSpan(addresses.Length + 4));
        return IndependentChecksum(buffer.AsSpan(0, addresses.Length + 4 + tcpSegment.Length));
    }
}
