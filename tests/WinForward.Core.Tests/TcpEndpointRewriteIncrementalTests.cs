using System.Buffers.Binary;
using System.Net;
using WinForward.Core;
using WinForward.Protocols;
using Xunit;
using static WinForward.Core.Tests.ChecksumMath;

namespace WinForward.Core.Tests;

/// <summary>
/// Property coverage for the RFC 1624 incremental endpoint rewrite (P2a): for randomized
/// frames the incremental path must be byte-identical to the retained full-recompute oracle
/// and must produce checksums that validate through the independently reimplemented test-side
/// math — including odd payload lengths, carry-heavy deltas, and the 0x0000/0xFFFF sum
/// boundary (TCP never inverts a computed zero).
/// </summary>
public sealed class TcpEndpointRewriteIncrementalTests
{
    private const int RandomIterations = 512;

    [Fact]
    public void Ipv4IncrementalMatchesFullRecomputeAndValidatesAcrossRandomFrames()
    {
        var random = new Random(0xC0FFEE);
        for (var iteration = 0; iteration < RandomIterations; iteration++)
        {
            var dataOffsetWords = 5 + random.Next(0, 3);
            var maxOptionBytes = (dataOffsetWords - 5) * 4;
            var frame = FrameBuilders.BuildIpv4TcpFrame(
                RandomIpv4(random), RandomIpv4(random),
                RandomPort(random), RandomPort(random),
                tcpDataOffsetWords: dataOffsetWords,
                options: new byte[random.Next(0, maxOptionBytes + 1)],
                payload: RandomPayload(random, random.Next(0, 97)));

            var (incremental, full) = RewriteBothWays(frame, RandomIpv4(random), RandomIpv4(random), RandomPort(random), RandomPort(random));

            Assert.Equal(full, incremental);
            Assert.True(ValidateIpv4HeaderChecksum(incremental), $"header checksum invalid at iteration {iteration}");
            Assert.True(ValidateIpv4TcpChecksum(incremental), $"tcp checksum invalid at iteration {iteration}");
        }
    }

    [Fact]
    public void Ipv6IncrementalMatchesFullRecomputeAndValidatesAcrossRandomFrames()
    {
        var random = new Random(0x0DB8);
        for (var iteration = 0; iteration < RandomIterations; iteration++)
        {
            var frame = FrameBuilders.BuildIpv6TcpFrame(
                RandomIpv6(random), RandomIpv6(random),
                RandomPort(random), RandomPort(random),
                payload: RandomPayload(random, random.Next(0, 97)));

            var (incremental, full) = RewriteBothWays(frame, RandomIpv6(random), RandomIpv6(random), RandomPort(random), RandomPort(random));

            Assert.Equal(full, incremental);
            Assert.True(ValidateIpv6TcpChecksum(incremental), $"tcp checksum invalid at iteration {iteration}");
        }
    }

    [Fact]
    public void Ipv6ExtensionHeaderIncrementalMatchesFullRecompute()
    {
        var random = new Random(0x60);
        var frame = FrameBuilders.BuildIpv6TcpFrameWithHopByHop(RandomIpv6(random), RandomIpv6(random), RandomPort(random), RandomPort(random));

        var (incremental, full) = RewriteBothWays(frame, RandomIpv6(random), RandomIpv6(random), RandomPort(random), RandomPort(random));

        Assert.Equal(full, incremental);
        Assert.True(ValidateIpv6TcpChecksumAt(incremental, tcpOffset: 62));
    }

    [Fact]
    public void BoundarySumsMatchFullRecomputeIncludingComputedZero()
    {
        // Sweep the new source port over its full range: the pre-complement sum walks 65 536
        // consecutive values, guaranteeing coverage of both the all-ones fold (stored checksum
        // 0x0000 — TCP stores it verbatim, never inverting to 0xFFFF) and the carry-folded
        // mid-range. Every candidate must equal the oracle byte-for-byte.
        var random = new Random(0xFFFF);
        var frame = FrameBuilders.BuildIpv4TcpFrame(
            RandomIpv4(random), RandomIpv4(random), RandomPort(random), RandomPort(random),
            payload: RandomPayload(random, 5));
        var sawStoredZero = false;

        for (uint sourcePort = 0; sourcePort <= ushort.MaxValue; sourcePort++)
        {
            var (incremental, full) = RewriteBothWays(frame, IPAddress.Parse("203.0.113.7"), IPAddress.Parse("198.51.100.42"), (ushort)sourcePort, 443);

            Assert.Equal(full, incremental);
            var stored = BinaryPrimitives.ReadUInt16BigEndian(incremental.AsSpan(50, 2));
            if (stored == 0) sawStoredZero = true;
        }

        Assert.True(sawStoredZero, "the sweep must reach the computed-zero boundary (TCP non-inversion)");
    }

    [Fact]
    public void AddressOnlyAndPortOnlyDeltasMatchFullRecompute()
    {
        var random = new Random(0xA11CE);
        var frame = FrameBuilders.BuildIpv4TcpFrame(
            RandomIpv4(random), RandomIpv4(random), RandomPort(random), RandomPort(random),
            payload: RandomPayload(random, 33));

        var (incrementalAddresses, fullAddresses) = RewriteBothWays(frame, RandomIpv4(random), RandomIpv4(random), ReadPort(frame, isSource: true), ReadPort(frame, isSource: false));
        Assert.Equal(fullAddresses, incrementalAddresses);

        var (incrementalPorts, fullPorts) = RewriteBothWays(frame, new IPAddress(frame.AsSpan(26, 4).ToArray()), new IPAddress(frame.AsSpan(30, 4).ToArray()), RandomPort(random), RandomPort(random));
        Assert.Equal(fullPorts, incrementalPorts);
    }

    [Fact]
    public void InternetChecksumMatchesIndependentScalarAcrossSizesAndBoundaries()
    {
        // P2b: the vectorized production checksum must stay bit-identical to the independently
        // reimplemented scalar fold across vector-threshold boundaries, odd tails, all-zero and
        // all-ones inputs, and the 64 KiB IP-maximum span.
        var random = new Random(0xBEEF);
        int[] sizes = [0, 1, 2, 31, 32, 33, 63, 64, 65, 512, 1514, 65535];
        foreach (var size in sizes)
        {
            var data = RandomPayload(random, size);
            Assert.Equal(IndependentScalarChecksum(data), PacketChecksums.InternetChecksum(data));

            var zeros = new byte[size];
            Assert.Equal(IndependentScalarChecksum(zeros), PacketChecksums.InternetChecksum(zeros));

            var ones = new byte[size];
            Array.Fill(ones, (byte)0xff);
            Assert.Equal(IndependentScalarChecksum(ones), PacketChecksums.InternetChecksum(ones));
        }
    }

    private static ushort IndependentScalarChecksum(byte[] data)
    {
        uint sum = 0;
        var index = 0;
        for (; index + 1 < data.Length; index += 2)
        {
            sum += (uint)((data[index] << 8) | data[index + 1]);
            sum = (sum & 0xffff) + (sum >> 16);
        }
        if (index < data.Length)
        {
            sum += (uint)data[index] << 8;
            sum = (sum & 0xffff) + (sum >> 16);
        }
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }

    private static (byte[] Incremental, byte[] Full) RewriteBothWays(byte[] frame, IPAddress newSource, IPAddress newDestination, ushort newSourcePort, ushort newDestinationPort)
    {
        var incremental = frame.ToArray();
        var full = frame.ToArray();
        Assert.True(PacketChecksums.TryRewriteTcpEndpoints(incremental, newSource, newSourcePort, newDestination, newDestinationPort));
        Assert.True(PacketChecksums.TryRewriteTcpEndpointsFullRecompute(full, IPAddressValue.From(newSource), newSourcePort, IPAddressValue.From(newDestination), newDestinationPort));
        return (incremental, full);
    }

    private static ushort ReadPort(byte[] frame, bool isSource)
    {
        var headerLength = (frame[14] & 0x0f) * 4;
        return BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + headerLength + (isSource ? 0 : 2), 2));
    }

    private static bool ValidateIpv4HeaderChecksum(byte[] frame)
    {
        var headerLength = (frame[14] & 0x0f) * 4;
        return Finish(Sum(frame.AsSpan(14, headerLength))) == 0;
    }

    private static bool ValidateIpv4TcpChecksum(byte[] frame)
    {
        var headerLength = (frame[14] & 0x0f) * 4;
        var tcpOffset = 14 + headerLength;
        var tcpLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(16, 2)) - headerLength;
        var sum = Sum(frame.AsSpan(26, 4)) + Sum(frame.AsSpan(30, 4)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcpOffset, tcpLength));
        return Finish(sum) == 0;
    }

    private static bool ValidateIpv6TcpChecksum(byte[] frame) => ValidateIpv6TcpChecksumAt(frame, tcpOffset: 54);

    private static bool ValidateIpv6TcpChecksumAt(byte[] frame, int tcpOffset)
    {
        var tcpLength = frame.Length - tcpOffset;
        var sum = Sum(frame.AsSpan(22, 16)) + Sum(frame.AsSpan(38, 16)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcpOffset, tcpLength));
        return Finish(sum) == 0;
    }

    private static IPAddress RandomIpv4(Random random)
    {
        var bytes = new byte[4];
        random.NextBytes(bytes);
        return new IPAddress(bytes);
    }

    private static IPAddress RandomIpv6(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new IPAddress(bytes);
    }

    private static ushort RandomPort(Random random) => (ushort)random.Next(1, ushort.MaxValue + 1);

    private static byte[] RandomPayload(Random random, int length)
    {
        var payload = new byte[length];
        random.NextBytes(payload);
        return payload;
    }
}
