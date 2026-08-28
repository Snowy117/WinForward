using System.Buffers.Binary;
using System.Net;
using WinForward.Protocols;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class UdpPacketParsingTests
{
    [Fact]
    public void SocksUdpRoundTripPreservesDnsPayloadAndEndpoint()
    {
        var payload = new byte[] { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01 };
        var address = IPAddress.Parse("2001:db8::53");
        var encoded = Socks5UdpCodec.Encode(address, 53, payload);

        Assert.True(Socks5UdpCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(address, decoded.DestinationAddress);
        Assert.Equal((ushort)53, decoded.DestinationPort);
        Assert.Equal(payload, decoded.Payload.ToArray());
        encoded[^1] = 0xff;
        Assert.Equal(0xff, decoded.Payload.Span[^1]);
    }

    [Fact]
    public void SocksUdpRejectsFragmentedFrames()
    {
        var encoded = Socks5UdpCodec.Encode(IPAddress.Parse("192.0.2.53"), 53, [1, 2, 3]);
        encoded[2] = 1;

        Assert.False(Socks5UdpCodec.TryDecode(encoded, out _));
    }

    [Fact]
    public void SocksUdpDomainRoundTripPreservesPayload()
    {
        var encoded = Socks5UdpCodec.Encode("dns.example", 53, [0xab, 0xcd]);

        Assert.True(Socks5UdpCodec.TryDecode(encoded, out var decoded));
        Assert.Null(decoded.DestinationAddress);
        Assert.Equal("dns.example", decoded.DestinationDomain);
        Assert.Equal(new byte[] { 0xab, 0xcd }, decoded.Payload.ToArray());
    }

    [Fact]
    public void SocksUdpDomainDecodesAsAsciiAndReplacesNonAsciiBytes()
    {
        // R5: RFC 1928 domain names are ASCII. Non-ASCII bytes are replaced ('?') by the ASCII
        // decoder rather than throwing, preserving the codec's fail-closed no-throw style.
        var asciiFrame = new byte[] { 0, 0, 0, 3, 3, (byte)'d', (byte)'n', (byte)'s', 0, 53, 0xab };
        Assert.True(Socks5UdpCodec.TryDecode(asciiFrame, out var ascii));
        Assert.Equal("dns", ascii.DestinationDomain);
        Assert.Equal(53, ascii.DestinationPort);
        Assert.Equal(new byte[] { 0xab }, ascii.Payload.ToArray());

        var nonAsciiFrame = new byte[] { 0, 0, 0, 3, 3, (byte)'d', 0xE9, (byte)'s', 0, 53, 0xab };
        Assert.True(Socks5UdpCodec.TryDecode(nonAsciiFrame, out var replaced));
        Assert.Equal("d?s", replaced.DestinationDomain);
    }

    [Fact]
    public void Socks5UdpDecodeCarriesRelayScopeForIpv6Address()
    {
        // M2: the SOCKS5 UDP wire format carries no scope, so TryDecode accepts an explicit scope
        // and reconstructs an IPv6 destination with a non-zero ScopeId.
        var address = IPAddress.Parse("fe80::53");
        var encoded = Socks5UdpCodec.Encode(address, 53, [1, 2, 3]);
        Assert.True(Socks5UdpCodec.TryDecode(encoded, out var decoded, scopeId: 9));
        Assert.NotNull(decoded.DestinationAddress);
        Assert.Equal((uint)9, decoded.DestinationAddress!.ScopeId);
    }

    [Fact]
    public void IPv4UdpPacketParserRejectsFragmentsAndReadsPayload()
    {
        var frame = new byte[14 + 20 + 8 + 3];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 31);
        frame[23] = 17;
        IPAddress.Parse("192.0.2.10").GetAddressBytes().CopyTo(frame, 26);
        IPAddress.Parse("192.0.2.53").GetAddressBytes().CopyTo(frame, 30);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38, 2), 11);
        frame[42] = 1;
        frame[43] = 2;
        frame[44] = 3;

        Assert.True(IpUdpPacket.TryParse(frame, out var packet));
        Assert.Equal((ushort)53000, packet.SourcePort);
        Assert.Equal(new byte[] { 1, 2, 3 }, packet.Payload.ToArray());
        frame[44] = 4;
        Assert.Equal(4, packet.Payload.Span[^1]);
        frame[20] = 0x20;
        Assert.False(IpUdpPacket.TryParse(frame, out _));
    }

    [Fact]
    public void IPv4UdpRewriteUpdatesEndpointsAndChecksums()
    {
        var frame = CreateIpv4UdpFrame();

        Assert.True(PacketChecksums.TryRewriteUdpEndpoints(frame, IPAddress.Parse("198.51.100.1"), 40000, IPAddress.Parse("203.0.113.2"), 5353));
        Assert.True(IpUdpPacket.TryParse(frame, out var packet));
        Assert.Equal(IPAddress.Parse("198.51.100.1"), packet.SourceAddress);
        Assert.Equal((ushort)40000, packet.SourcePort);
        Assert.Equal(IPAddress.Parse("203.0.113.2"), packet.DestinationAddress);
        Assert.Equal((ushort)5353, packet.DestinationPort);
        Assert.Equal((ushort)0, PacketChecksums.InternetChecksum(frame.AsSpan(14, 20)));
    }

    [Fact]
    public void UdpChecksumIsValidAfterEndpointRewrite()
    {
        var frame = CreateIpv4UdpFrame();
        Assert.True(PacketChecksums.TryRewriteUdpEndpoints(frame, IPAddress.Parse("198.51.100.1"), 40000, IPAddress.Parse("203.0.113.2"), 5353));
        var udpOffset = 14 + 20;
        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(udpOffset + 4, 2));
        var checksum = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(udpOffset + 6, 2));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 6, 2), 0);
        var source = frame.AsSpan(14 + 12, 4);
        var destination = frame.AsSpan(14 + 16, 4);
        var pseudo = new byte[12 + udpLength];
        source.CopyTo(pseudo);
        destination.CopyTo(pseudo.AsSpan(4));
        pseudo[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(pseudo.AsSpan(10, 2), udpLength);
        frame.AsSpan(udpOffset, udpLength).CopyTo(pseudo.AsSpan(12));
        Assert.Equal(checksum, PacketChecksums.InternetChecksum(pseudo));
    }

    [Fact]
    public void IPv6UdpPacketParserReadsAddressesAndPayload()
    {
        var frame = CreateIpv6UdpFrame();

        Assert.True(IpUdpPacket.TryParse(frame, out var packet));
        Assert.Equal(IPAddress.Parse("2001:db8::10"), packet.SourceAddress);
        Assert.Equal(IPAddress.Parse("2001:db8::53"), packet.DestinationAddress);
        Assert.Equal((ushort)53000, packet.SourcePort);
        Assert.Equal((ushort)53, packet.DestinationPort);
        Assert.Equal(new byte[] { 1, 2, 3 }, packet.Payload.ToArray());
    }

    [Fact]
    public void IPv6UdpRewriteUpdatesEndpointsAndChecksum()
    {
        var frame = CreateIpv6UdpFrame();

        Assert.True(PacketChecksums.TryRewriteUdpEndpoints(frame, IPAddress.Parse("2001:db8::99"), 40000, IPAddress.Parse("2001:db8::1"), 5353));
        Assert.True(IpUdpPacket.TryParse(frame, out var packet));
        Assert.Equal(IPAddress.Parse("2001:db8::99"), packet.SourceAddress);
        Assert.Equal((ushort)40000, packet.SourcePort);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), packet.DestinationAddress);
        Assert.Equal((ushort)5353, packet.DestinationPort);

        var udpOffset = 14 + 40;
        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(udpOffset + 4, 2));
        var checksum = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(udpOffset + 6, 2));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 6, 2), 0);
        var pseudo = new byte[40 + udpLength];
        frame.AsSpan(14 + 8, 16).CopyTo(pseudo);
        frame.AsSpan(14 + 24, 16).CopyTo(pseudo.AsSpan(16));
        BinaryPrimitives.WriteUInt32BigEndian(pseudo.AsSpan(32, 4), udpLength);
        pseudo[39] = 17;
        frame.AsSpan(udpOffset, udpLength).CopyTo(pseudo.AsSpan(40));
        Assert.NotEqual((ushort)0, checksum);
        Assert.Equal(checksum, PacketChecksums.InternetChecksum(pseudo));
    }

    [Fact]
    public void IPv6UdpParserRejectsFragmentHeader()
    {
        var frame = CreateIpv6UdpFrame();
        frame[20] = 44; // next header = fragment

        Assert.False(IpUdpPacket.TryParse(frame, out _));
    }

    private static byte[] CreateIpv6UdpFrame()
    {
        var frame = new byte[14 + 40 + 8 + 3];
        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        frame[18] = 0;
        frame[19] = 11; // payload length = UDP(8) + payload(3)
        frame[20] = 17; // next header = UDP
        frame[21] = 64; // hop limit
        IPAddress.Parse("2001:db8::10").GetAddressBytes().CopyTo(frame, 22);
        IPAddress.Parse("2001:db8::53").GetAddressBytes().CopyTo(frame, 38);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(54, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(56, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(58, 2), 11);
        frame[62] = 1;
        frame[63] = 2;
        frame[64] = 3;
        return frame;
    }

    private static byte[] CreateIpv4UdpFrame()
    {
        var frame = new byte[14 + 20 + 8 + 3];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 31);
        frame[23] = 17;
        IPAddress.Parse("192.0.2.10").GetAddressBytes().CopyTo(frame, 26);
        IPAddress.Parse("192.0.2.53").GetAddressBytes().CopyTo(frame, 30);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38, 2), 11);
        frame[42] = 1;
        frame[43] = 2;
        frame[44] = 3;
        return frame;
    }
}
