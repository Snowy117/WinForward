using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using WinForward.Protocols;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class TcpEndpointRewriteTests
{
    private static readonly IPAddress s_ipv4Source = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_ipv4Dest = IPAddress.Parse("192.0.2.53");
    private static readonly IPAddress s_ipv6Source = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress s_ipv6Dest = IPAddress.Parse("2001:db8::53");
    private static readonly IPAddress s_newIpv4Source = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress s_newIpv4Dest = IPAddress.Parse("127.0.0.1");
    private static readonly IPAddress s_newIpv6Source = IPAddress.Parse("2001:db8:1::7");
    private static readonly IPAddress s_newIpv6Dest = IPAddress.Parse("::1");

    [Fact]
    public void Ipv4RewritesAddressesPortsAndRecomputesBothChecksums()
    {
        var frame = BuildIpv4TcpFrame();
        var original = frame.ToArray();

        var ok = PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv4Source, 1111, s_newIpv4Dest, 2222);

        Assert.True(ok);
        AssertChangedOnly(Ipv4ExpectedMutableOffsets(), original, frame);

        Assert.Equal(s_newIpv4Source, new IPAddress(frame.AsSpan(26, 4).ToArray()));
        Assert.Equal(s_newIpv4Dest, new IPAddress(frame.AsSpan(30, 4).ToArray()));
        Assert.Equal((ushort)1111, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(34, 2)));
        Assert.Equal((ushort)2222, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(36, 2)));

        Assert.True(ValidateIpv4HeaderChecksum(frame));
        Assert.True(ValidateIpv4TcpChecksum(frame));
    }

    [Fact]
    public void Ipv6RewritesAddressesPortsAndRecomputesTcpChecksumOnly()
    {
        var frame = BuildIpv6TcpFrame();
        var original = frame.ToArray();

        var ok = PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv6Source, 3333, s_newIpv6Dest, 4444);

        Assert.True(ok);
        AssertChangedOnly(Ipv6ExpectedMutableOffsets(), original, frame);

        Assert.Equal(s_newIpv6Source, new IPAddress(frame.AsSpan(22, 16).ToArray()));
        Assert.Equal(s_newIpv6Dest, new IPAddress(frame.AsSpan(38, 16).ToArray()));
        Assert.Equal((ushort)3333, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(54, 2)));
        Assert.Equal((ushort)4444, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(56, 2)));

        Assert.True(ValidateIpv6TcpChecksum(frame));
    }

    [Fact]
    public void RoundTripIpv4RestoresOriginalFrameByteForByte()
    {
        var frame = BuildIpv4TcpFrame(payload: [0xde, 0xad, 0xbe, 0xef]);
        var original = frame.ToArray();

        Assert.True(PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv4Source, 1111, s_newIpv4Dest, 2222));
        Assert.True(PacketChecksums.TryRewriteTcpEndpoints(frame, s_ipv4Source, 53000, s_ipv4Dest, 443));

        Assert.Equal(original, frame);
    }

    [Fact]
    public void RoundTripIpv6RestoresOriginalFrameByteForByte()
    {
        var frame = BuildIpv6TcpFrame(payload: [0x01, 0x02, 0x03, 0x04]);
        var original = frame.ToArray();

        Assert.True(PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv6Source, 3333, s_newIpv6Dest, 4444));
        Assert.True(PacketChecksums.TryRewriteTcpEndpoints(frame, s_ipv6Source, 53000, s_ipv6Dest, 443));

        Assert.Equal(original, frame);
    }

    [Fact]
    public void SynWithOptionsSurvivesRewriteIntact()
    {
        var options = new byte[] { 0x02, 0x04, 0x05, 0xb4 };
        var frame = BuildIpv4TcpFrame(tcpDataOffsetWords: 6, options: options);
        var originalOptions = options.ToArray();

        var ok = PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv4Source, 1111, s_newIpv4Dest, 2222);

        Assert.True(ok);
        Assert.True(ValidateIpv4TcpChecksum(frame));
        Assert.Equal(originalOptions, frame.AsSpan(54, 4).ToArray());
    }

    [Fact]
    public void RejectsTooShortFrameWithoutMutating()
    {
        var frame = new byte[20];
        var copy = frame.ToArray();

        var ok = PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv4Source, 1, s_newIpv4Dest, 2);

        Assert.False(ok);
        Assert.Equal(copy, frame);
    }

    [Fact]
    public void RejectsNonIpEtherTypeWithoutMutating()
    {
        var frame = BuildIpv4TcpFrame();
        frame[12] = 0x08;
        frame[13] = 0x06;
        var copy = frame.ToArray();

        var ok = PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv4Source, 1, s_newIpv4Dest, 2);

        Assert.False(ok);
        Assert.Equal(copy, frame);
    }

    [Fact]
    public void RejectsIpv4FragmentWithoutMutating()
    {
        var frame = BuildIpv4TcpFrame();
        frame[20] = 0x20;
        var copy = frame.ToArray();

        var ok = PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv4Source, 1, s_newIpv4Dest, 2);

        Assert.False(ok);
        Assert.Equal(copy, frame);
    }

    [Fact]
    public void RejectsUdpFramePassedToTcpRewriterWithoutMutating()
    {
        var frame = BuildIpv4UdpFrame();
        var copy = frame.ToArray();

        var ok = PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv4Source, 1, s_newIpv4Dest, 2);

        Assert.False(ok);
        Assert.Equal(copy, frame);
    }

    [Fact]
    public void RejectsMalformedTcpDataOffsetWithoutMutating()
    {
        var frame = BuildIpv4TcpFrame();
        frame[46] = 0x10;
        var copy = frame.ToArray();

        var ok = PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv4Source, 1, s_newIpv4Dest, 2);

        Assert.False(ok);
        Assert.Equal(copy, frame);
    }

    [Fact]
    public void RejectsIpv6FragmentExtensionWithoutMutating()
    {
        var frame = BuildIpv6TcpFrame();
        frame[20] = 44;
        var copy = frame.ToArray();

        var ok = PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv6Source, 1, s_newIpv6Dest, 2);

        Assert.False(ok);
        Assert.Equal(copy, frame);
    }

    [Fact]
    public void RejectsUnsupportedIpv6ExtensionChainWithoutMutating()
    {
        var frame = BuildIpv6TcpFrame();
        frame[20] = 60;
        frame[40] = 59;
        frame[41] = 0;
        var copy = frame.ToArray();

        var ok = PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv6Source, 1, s_newIpv6Dest, 2);

        Assert.False(ok);
        Assert.Equal(copy, frame);
    }

    [Fact]
    public void RejectsAddressFamilyMismatchWithoutMutating()
    {
        var frame = BuildIpv4TcpFrame();
        var copy = frame.ToArray();

        var ok = PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv6Source, 1, s_newIpv6Dest, 2);

        Assert.False(ok);
        Assert.Equal(copy, frame);
    }

    [Fact]
    public void Ipv6HopByHopExtensionThenTcpRewritesSuccessfully()
    {
        // IPv6 Hop-by-Hop (next-header 0) followed by TCP (6): proves the shared transport-finder's
        // accept branch is reachable from the TCP entry point, not only via the UDP path.
        var frame = BuildIpv6TcpFrameWithHopByHop();
        var original = frame.ToArray();

        var ok = PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv6Source, 5555, s_newIpv6Dest, 6666);

        Assert.True(ok);
        AssertChangedOnly(Ipv6ExtensionExpectedMutableOffsets(), original, frame);

        Assert.Equal(s_newIpv6Source, new IPAddress(frame.AsSpan(22, 16).ToArray()));
        Assert.Equal(s_newIpv6Dest, new IPAddress(frame.AsSpan(38, 16).ToArray()));
        Assert.Equal((ushort)5555, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(62, 2)));
        Assert.Equal((ushort)6666, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(64, 2)));

        Assert.Equal(original.AsSpan(54, 8).ToArray(), frame.AsSpan(54, 8).ToArray());
        Assert.Equal(original.AsSpan(66, 12).ToArray(), frame.AsSpan(66, 12).ToArray());

        Assert.True(ValidateIpv6TcpChecksumWithHopByHop(frame));
    }

    [Fact]
    public void TcpChecksumNeverInvertsZeroToFFFF()
    {
        var frame = BuildIpv4TcpFrame();
        Assert.True(PacketChecksums.TryRewriteTcpEndpoints(frame, s_newIpv4Source, 1, s_newIpv4Dest, 2));

        var stored = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(50, 2));
        var recomputed = RecomputeIpv4TcpChecksumField(frame);
        Assert.Equal(recomputed, stored);
        Assert.NotEqual(ushort.MaxValue, (ushort)(stored ^ recomputed));
    }

    private static bool ValidateIpv4HeaderChecksum(byte[] frame)
    {
        var headerLength = (frame[14] & 0x0f) * 4;
        return PacketChecksums.InternetChecksum(frame.AsSpan(14, headerLength)) == 0;
    }

    private static bool ValidateIpv4TcpChecksum(byte[] frame)
        => TcpSegmentChecksumIncludingCksumField(frame) == 0;

    private static bool ValidateIpv6TcpChecksum(byte[] frame)
    {
        var tcpOffset = 54;
        var tcpLength = frame.Length - tcpOffset;
        var sum = Sum(frame.AsSpan(22, 16)) + Sum(frame.AsSpan(38, 16)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcpOffset, tcpLength));
        return Finish(sum) == 0;
    }

    private static bool ValidateIpv6TcpChecksumWithHopByHop(byte[] frame)
    {
        var tcpOffset = 62;
        var tcpLength = frame.Length - tcpOffset;
        var sum = Sum(frame.AsSpan(22, 16)) + Sum(frame.AsSpan(38, 16)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcpOffset, tcpLength));
        return Finish(sum) == 0;
    }

    private static ushort RecomputeIpv4TcpChecksumField(byte[] frame)
    {
        var headerLength = (frame[14] & 0x0f) * 4;
        var tcpOffset = 14 + headerLength;
        var tcpLength = frame.Length - tcpOffset;
        var withZeroCksum = Sum(frame.AsSpan(tcpOffset, 16)) + Sum(frame.AsSpan(tcpOffset + 18, tcpLength - 18));
        var sum = Sum(frame.AsSpan(26, 4)) + Sum(frame.AsSpan(30, 4)) + 6u + (uint)tcpLength + withZeroCksum;
        return Finish(sum);
    }

    private static ushort TcpSegmentChecksumIncludingCksumField(byte[] frame)
    {
        var headerLength = (frame[14] & 0x0f) * 4;
        var tcpOffset = 14 + headerLength;
        var tcpLength = frame.Length - tcpOffset;
        var sum = Sum(frame.AsSpan(26, 4)) + Sum(frame.AsSpan(30, 4)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcpOffset, tcpLength));
        return Finish(sum);
    }

    private static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var index = 0;
        for (; index + 1 < data.Length; index += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
        if (index < data.Length) sum += (uint)data[index] << 8;
        return sum;
    }

    private static ushort Finish(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }

    private static HashSet<int> Ipv4ExpectedMutableOffsets()
        => [24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 50, 51];

    private static HashSet<int> Ipv6ExpectedMutableOffsets()
    {
        var set = new HashSet<int>();
        for (var i = 22; i < 58; i++) set.Add(i);
        set.Add(70);
        set.Add(71);
        return set;
    }

    private static HashSet<int> Ipv6ExtensionExpectedMutableOffsets()
    {
        // Ethernet(14) + IPv6(40) + Hop-by-Hop(8) + TCP(20..). Mutable: IPv6 src(22..37),
        // IPv6 dst(38..53), TCP src port(62..63), TCP dst port(64..65), TCP checksum(78..79).
        // The Hop-by-Hop header (54..61) is immutable.
        var set = new HashSet<int>();
        for (var i = 22; i < 54; i++) set.Add(i);
        set.Add(62);
        set.Add(63);
        set.Add(64);
        set.Add(65);
        set.Add(78);
        set.Add(79);
        return set;
    }

    private static void AssertChangedOnly(HashSet<int> expectedMutable, byte[] original, byte[] actual)
    {
        Assert.Equal(original.Length, actual.Length);
        for (var i = 0; i < original.Length; i++)
        {
            if (original[i] == actual[i]) continue;
            Assert.True(expectedMutable.Contains(i), $"Byte at offset {i.ToString(CultureInfo.InvariantCulture)} changed but was not in the expected mutable set.");
        }
    }

    private static byte[] BuildIpv4TcpFrame(int tcpDataOffsetWords = 5, byte[]? options = null, byte[]? payload = null)
    {
        options ??= [];
        var tcpHeaderLength = tcpDataOffsetWords * 4;
        var optionPadding = new byte[tcpHeaderLength - 20 - options.Length];
        var payloadBytes = payload ?? [];
        var totalLength = 20 + tcpHeaderLength + payloadBytes.Length;
        var frame = new byte[14 + totalLength];

        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), (ushort)totalLength);
        frame[23] = 6;
        s_ipv4Source.TryWriteBytes(frame.AsSpan(26, 4), out _);
        s_ipv4Dest.TryWriteBytes(frame.AsSpan(30, 4), out _);

        const int tcp = 34;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 2, 2), 443);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 4, 4), 0x00000001);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 8, 4), 0x00000000);
        frame[tcp + 12] = (byte)(tcpDataOffsetWords << 4);
        frame[tcp + 13] = 0x18;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 14, 2), 0xffff);
        options.CopyTo(frame, tcp + 20);
        optionPadding.CopyTo(frame, tcp + 20 + options.Length);
        payloadBytes.CopyTo(frame, tcp + tcpHeaderLength);

        SetIpv4HeaderChecksum(frame);
        SetIpv4TcpChecksum(frame, tcp, totalLength - 20);
        return frame;
    }

    private static byte[] BuildIpv6TcpFrameWithHopByHop()
    {
        // Layout: Ethernet(14) + IPv6(40) + Hop-by-Hop(8) + TCP(20).
        // IPv6 next-header byte (offset 20) = 0 (Hop-by-Hop).
        // Hop-by-Hop next-header (offset 54) = 6 (TCP); length (offset 55) = 0 => (0+1)*8 = 8 bytes.
        const int tcpHeaderLength = 20;
        const int extensionLength = 8;
        const int ipv6PayloadLength = extensionLength + tcpHeaderLength;
        var frame = new byte[14 + 40 + ipv6PayloadLength];

        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), (ushort)ipv6PayloadLength);
        frame[20] = 0;
        s_ipv6Source.TryWriteBytes(frame.AsSpan(22, 16), out _);
        s_ipv6Dest.TryWriteBytes(frame.AsSpan(38, 16), out _);

        const int extension = 54;
        frame[extension] = 6;
        frame[extension + 1] = 0;

        const int tcp = 62;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 2, 2), 443);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 4, 4), 0x00000003);
        frame[tcp + 12] = 0x50;
        frame[tcp + 13] = 0x02;

        SetIpv6TcpChecksum(frame, tcp, tcpHeaderLength);
        return frame;
    }

    private static byte[] BuildIpv6TcpFrame(byte[]? payload = null)
    {
        var payloadBytes = payload ?? [];
        var tcpLength = 20 + payloadBytes.Length;
        var frame = new byte[14 + 40 + tcpLength];

        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), (ushort)tcpLength);
        frame[20] = 6;
        s_ipv6Source.TryWriteBytes(frame.AsSpan(22, 16), out _);
        s_ipv6Dest.TryWriteBytes(frame.AsSpan(38, 16), out _);

        const int tcp = 54;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 2, 2), 443);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 4, 4), 0x00000002);
        frame[tcp + 12] = 0x50;
        frame[tcp + 13] = 0x02;
        payloadBytes.CopyTo(frame, tcp + 20);

        SetIpv6TcpChecksum(frame, tcp, tcpLength);
        return frame;
    }

    private static byte[] BuildIpv4UdpFrame()
    {
        var frame = new byte[14 + 20 + 8];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 28);
        frame[23] = 17;
        s_ipv4Source.TryWriteBytes(frame.AsSpan(26, 4), out _);
        s_ipv4Dest.TryWriteBytes(frame.AsSpan(30, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38, 2), 8);
        SetIpv4HeaderChecksum(frame);
        return frame;
    }

    private static void SetIpv4HeaderChecksum(byte[] frame)
    {
        var headerLength = (frame[14] & 0x0f) * 4;
        frame[24] = 0;
        frame[25] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(24, 2), PacketChecksums.InternetChecksum(frame.AsSpan(14, headerLength)));
    }

    private static void SetIpv4TcpChecksum(byte[] frame, int tcp, int tcpLength)
    {
        frame[tcp + 16] = 0;
        frame[tcp + 17] = 0;
        var sum = Sum(frame.AsSpan(26, 4)) + Sum(frame.AsSpan(30, 4)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcp, tcpLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 16, 2), Finish(sum));
    }

    private static void SetIpv6TcpChecksum(byte[] frame, int tcp, int tcpLength)
    {
        frame[tcp + 16] = 0;
        frame[tcp + 17] = 0;
        var sum = Sum(frame.AsSpan(22, 16)) + Sum(frame.AsSpan(38, 16)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcp, tcpLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 16, 2), Finish(sum));
    }
}
