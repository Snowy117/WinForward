using System.Buffers.Binary;
using System.Net;
using WinForward.Benchmarks;
using WinForward.Protocols;
using WinForward.Runtime.TcpRedirect;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The benchmark harness's frame builders feed every redirect and parser row, so their validity is part
/// of those rows' meaning: a frame with a wrong pseudo-header or a bad length would make the rewriter
/// take its rejection path, and the row would report a plausible number for the wrong question. Each
/// builder is checked structurally and against the test project's own checksum implementation, which is
/// independent of the harness's.
/// </summary>
public sealed class BenchmarkFrameBuilderTests
{
    private static readonly IPAddress s_client = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress s_server = IPAddress.Parse("2001:db8::80");

    [Theory]
    [InlineData(128, true)]
    [InlineData(128, false)]
    [InlineData(1400, false)]
    public void Ipv4TcpFrameCarriesAValidTcpChecksum(int frameSize, bool bareSyn)
    {
        var frame = BenchmarkShared.CreateIpv4TcpFrame(frameSize, bareSyn);

        Assert.Equal(0x0800, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2)));
        Assert.Equal(4, frame[14] >> 4);
        Assert.Equal(6, frame[23]);
        var headerLength = (frame[14] & 0x0f) * 4;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(16, 2));
        var tcpOffset = 14 + headerLength;
        var tcpLength = totalLength - headerLength;
        Assert.True(frame.Length >= tcpOffset + tcpLength);

        var sum = ChecksumMath.Sum(frame.AsSpan(26, 4))
            + ChecksumMath.Sum(frame.AsSpan(30, 4))
            + 6u
            + (uint)tcpLength
            + ChecksumMath.Sum(frame.AsSpan(tcpOffset, tcpLength));

        Assert.Equal(0, ChecksumMath.Finish(sum));
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(128, false)]
    [InlineData(1400, false)]
    public void Ipv6TcpFrameCarriesAValidTcpChecksumAndATcpNextHeader(int frameSize, bool bareSyn)
    {
        var frame = BenchmarkShared.CreateIpv6TcpFrame(frameSize, bareSyn);

        Assert.Equal(0x86dd, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2)));
        Assert.Equal(6, frame[14] >> 4);
        Assert.Equal(6, frame[20]);
        Assert.Equal(53_000, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(54, 2)));
        Assert.Equal(443, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(56, 2)));
        Assert.Equal(bareSyn ? 0x02 : 0x10, frame[67]);
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(18, 2));
        Assert.Equal(bareSyn ? 20 : frameSize - 54, payloadLength);
        Assert.True(frame.Length >= 54 + payloadLength);

        var sum = ChecksumMath.Sum(frame.AsSpan(22, 16))
            + ChecksumMath.Sum(frame.AsSpan(38, 16))
            + 6u
            + payloadLength
            + ChecksumMath.Sum(frame.AsSpan(54, payloadLength));

        Assert.Equal(0, ChecksumMath.Finish(sum));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(512)]
    [InlineData(1514)]
    public void Ipv6UdpFrameCarriesTheIpv6UdpStructure(int frameSize)
    {
        var frame = BenchmarkShared.CreateIpv6UdpFrame(frameSize);

        Assert.Equal(frameSize, frame.Length);
        Assert.Equal(0x86dd, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2)));
        Assert.Equal(6, frame[14] >> 4);
        Assert.Equal(17, frame[20]);
        Assert.Equal(53_000, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(54, 2)));
        Assert.Equal(53, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(56, 2)));
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(18, 2));
        Assert.Equal(frameSize - 54, payloadLength);
        Assert.Equal(payloadLength, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(58, 2)));
        // Parse-only shape, matching the IPv4 UDP builder: no production parser reads this field.
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(60, 2)));
    }

    /// <summary>
    /// The property the IPv6 parser rows depend on: both production parsers classify the built frame
    /// instead of rejecting it. A rejected frame would make a row throw rather than report a number,
    /// and a frame one parser rejects would time the rejection path for that row.
    /// </summary>
    [Theory]
    [InlineData(64)]
    [InlineData(512)]
    [InlineData(1514)]
    public void BothUdpParsersAcceptTheIpv6UdpFrame(int frameSize)
    {
        var frame = BenchmarkShared.CreateIpv6UdpFrame(frameSize);

        Assert.True(IPTcpUdpPacket.TryParse(frame, out var classified));
        Assert.Equal(PacketTransport.Udp, classified.Transport);
        Assert.Equal(IPAddress.Parse("2001:db8::10"), classified.SourceAddress);
        Assert.Equal(IPAddress.Parse("2001:db8::53"), classified.DestinationAddress);
        Assert.Equal((ushort)53_000, classified.SourcePort);
        Assert.Equal((ushort)53, classified.DestinationPort);
        Assert.Equal(40, classified.IPHeaderLength);

        Assert.True(IPUdpPacket.TryParseSpan(frame, out var datagram));
        Assert.Equal(IPAddress.Parse("2001:db8::10"), datagram.SourceAddress);
        Assert.Equal(IPAddress.Parse("2001:db8::53"), datagram.DestinationAddress);
        Assert.Equal((ushort)53_000, datagram.SourcePort);
        Assert.Equal((ushort)53, datagram.DestinationPort);
        Assert.Equal(frameSize - 54 - 8, datagram.PayloadLength);
        Assert.Equal(62, datagram.PayloadOffset);
    }

    /// <summary>
    /// The property the data-path rows actually depend on: both rewrite legs accept the built frame in
    /// both association shapes and both families. A rejecting frame would leave a row measuring the
    /// rejection path — which is faster than the real one, so it would look like an improvement.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothLegsAcceptTheBuiltFrameInBothShapes(bool ipv6)
    {
        var frame = ipv6
            ? BenchmarkShared.CreateIpv6TcpFrame(1400, bareSyn: false)
            : BenchmarkShared.CreateIpv4TcpFrame(1400, bareSyn: false);
        var client = ipv6 ? Endpoint.From(s_client, 53_000) : Endpoint.From(IPAddress.Parse("192.0.2.10"), 53_000);
        var server = ipv6 ? Endpoint.From(s_server, 443) : Endpoint.From(IPAddress.Parse("192.0.2.80"), 443);
        var translated = Endpoint.From(ipv6 ? IPAddress.Parse("2001:db8::1080") : IPAddress.Parse("192.168.77.2"), 1080);
        var key = FlowKey.Create(client, server, TransportProtocol.Tcp, FlowOriginKind.Host, BenchmarkShared.SlotOf("adapter-0"), 0);
        var forwardedKey = FlowKey.Create(client, server, TransportProtocol.Tcp, FlowOriginKind.Forwarded, BenchmarkShared.SlotOf("adapter-0"), 0);
        var host = new TcpRedirectAssociation(key, server, 0, translated, forwardLocalAddress: null, 1, DateTimeOffset.UnixEpoch);
        var forwardLocal = ipv6 ? IPAddress.Parse("2001:db8::1") : IPAddress.Parse("192.168.77.1");
        var forwarded = new TcpRedirectAssociation(forwardedKey, server, 0, translated, IPAddressValue.From(forwardLocal), 2, DateTimeOffset.UnixEpoch);

        Assert.True(TcpFrameRewriter.TryRewriteForwardLeg(frame, client, server, host, 1080));
        Assert.True(TcpFrameRewriter.TryRewriteForwardLeg(frame, client, server, forwarded, 1080));
        Assert.True(PacketChecksums.TryRewriteTcpEndpoints(frame, server.Address, server.Port, client.Address, 1080));
    }
}
