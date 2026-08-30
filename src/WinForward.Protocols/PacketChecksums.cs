using System.Buffers.Binary;
using System.Net;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;
using WinForward.Core;

namespace WinForward.Protocols;

public static class PacketChecksums
{
    // Vectorized one's-complement accumulation (P2b): big-endian word pairs are byte-swapped
    // via shuffle and widened into independent u32 lanes, folded into the scalar sum every
    // 4 096 blocks so the accumulation cannot wrap uint at any span length — bit-identical to
    // the scalar fold-while-adding (both compute the same mod-65535 class, and only an
    // all-zero span yields the exact-zero representative). Hosts without hardware vectors
    // keep the fold-while-adding scalar loop below.
    private static readonly Vector256<byte> SwapAdjacentBytes = Vector256.Create(
        (byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14,
        17, 16, 19, 18, 21, 20, 23, 22, 25, 24, 27, 26, 29, 28, 31, 30);

    public static ushort InternetChecksum(ReadOnlySpan<byte> data) => Finish(Sum(data));

    public static bool TryRewriteUdpEndpoints(Span<byte> ethernetFrame, IPAddress sourceAddress, ushort sourcePort, IPAddress destinationAddress, ushort destinationPort)
        => TryRewriteUdpEndpoints(ethernetFrame, IPAddressValue.From(sourceAddress), sourcePort, IPAddressValue.From(destinationAddress), destinationPort);

    public static bool TryRewriteUdpEndpoints(Span<byte> ethernetFrame, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        if (ethernetFrame.Length < 14) return false;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(ethernetFrame.Slice(12, 2));
        return etherType switch
        {
            0x0800 => TryRewriteIpv4(ethernetFrame, sourceAddress, sourcePort, destinationAddress, destinationPort),
            0x86dd => TryRewriteIpv6(ethernetFrame, sourceAddress, sourcePort, destinationAddress, destinationPort),
            _ => false
        };
    }

    public static bool TryRewriteTcpEndpoints(Span<byte> ethernetFrame, IPAddress sourceAddress, ushort sourcePort, IPAddress destinationAddress, ushort destinationPort)
        => TryRewriteTcpEndpoints(ethernetFrame, IPAddressValue.From(sourceAddress), sourcePort, IPAddressValue.From(destinationAddress), destinationPort);

    public static bool TryRewriteTcpEndpoints(Span<byte> ethernetFrame, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        if (ethernetFrame.Length < 14) return false;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(ethernetFrame.Slice(12, 2));
        return etherType switch
        {
            0x0800 => TryRewriteIpv4Tcp(ethernetFrame, sourceAddress, sourcePort, destinationAddress, destinationPort),
            0x86dd => TryRewriteIpv6Tcp(ethernetFrame, sourceAddress, sourcePort, destinationAddress, destinationPort),
            _ => false
        };
    }

    private static bool TryRewriteIpv4(Span<byte> frame, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        const int ipOffset = 14;
        if (sourceAddress.Family != AddressFamilyKind.IPv4 || destinationAddress.Family != sourceAddress.Family || frame.Length < ipOffset + 20) return false;
        var headerLength = (frame[ipOffset] & 0x0f) * 4;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 2, 2));
        if (frame[ipOffset] >> 4 != 4 || headerLength < 20 || frame[ipOffset + 9] != 17 || totalLength < headerLength + 8 || frame.Length < ipOffset + totalLength) return false;
        var fragment = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 6, 2));
        if ((fragment & 0xbfff) != 0) return false;
        var udpOffset = ipOffset + headerLength;
        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(udpOffset + 4, 2));
        if (udpLength < 8 || udpOffset + udpLength > ipOffset + totalLength) return false;

        sourceAddress.TryWrite(frame.Slice(ipOffset + 12, 4), out _);
        destinationAddress.TryWrite(frame.Slice(ipOffset + 16, 4), out _);
        WritePorts(frame, udpOffset, sourcePort, destinationPort);
        frame[ipOffset + 10] = 0;
        frame[ipOffset + 11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(ipOffset + 10, 2), InternetChecksum(frame.Slice(ipOffset, headerLength)));
        WriteUdpChecksum(frame, udpOffset, udpLength, frame.Slice(ipOffset + 12, 4), frame.Slice(ipOffset + 16, 4), isIpv6: false);
        return true;
    }

    private static bool TryRewriteIpv6(Span<byte> frame, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        const int ipOffset = 14;
        if (sourceAddress.Family != AddressFamilyKind.IPv6 || destinationAddress.Family != sourceAddress.Family || frame.Length < ipOffset + 40 || frame[ipOffset] >> 4 != 6) return false;
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 4, 2));
        if (frame.Length < ipOffset + 40 + payloadLength || !TryFindIpv6Transport(frame, ipOffset, payloadLength, 17, out var udpOffset)) return false;
        var availableLength = ipOffset + 40 + payloadLength - udpOffset;
        if (availableLength < 8 || frame.Length < udpOffset + 8) return false;
        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(udpOffset + 4, 2));
        if (udpLength < 8 || udpLength > availableLength) return false;

        sourceAddress.TryWrite(frame.Slice(ipOffset + 8, 16), out _);
        destinationAddress.TryWrite(frame.Slice(ipOffset + 24, 16), out _);
        WritePorts(frame, udpOffset, sourcePort, destinationPort);
        WriteUdpChecksum(frame, udpOffset, udpLength, frame.Slice(ipOffset + 8, 16), frame.Slice(ipOffset + 24, 16), isIpv6: true);
        return true;
    }

    // RFC 1624 (HC' = ~(~HC + ~m + m')) incremental checksum update over only the changed
    // 16-bit words. Folding the existing checksum in makes the result bit-identical to a full
    // recompute only when the incoming checksum is correct — frames captured from the wire, or
    // emitted by MSTCP in answer to an injected leg, always carry valid checksums, and the
    // capture pipeline never hands this rewriter an offload-zeroed segment. The full-recompute
    // oracle below pins that equivalence by property.
    private static bool TryRewriteIpv4Tcp(Span<byte> frame, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        const int ipOffset = 14;
        if (sourceAddress.Family != AddressFamilyKind.IPv4 || destinationAddress.Family != sourceAddress.Family || frame.Length < ipOffset + 20) return false;
        var headerLength = (frame[ipOffset] & 0x0f) * 4;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 2, 2));
        if (frame[ipOffset] >> 4 != 4 || headerLength < 20 || frame[ipOffset + 9] != 6 || totalLength < headerLength + 20 || frame.Length < ipOffset + totalLength) return false;
        var fragment = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 6, 2));
        if ((fragment & 0xbfff) != 0) return false;
        var tcpOffset = ipOffset + headerLength;
        var tcpLength = totalLength - headerLength;
        if (tcpLength < 20 || frame.Length < tcpOffset + tcpLength) return false;
        var dataOffset = (frame[tcpOffset + 12] >> 4) * 4;
        if (dataOffset < 20 || dataOffset > tcpLength) return false;

        var oldSource0 = ReadWord(frame, ipOffset + 12);
        var oldSource1 = ReadWord(frame, ipOffset + 14);
        var oldDestination0 = ReadWord(frame, ipOffset + 16);
        var oldDestination1 = ReadWord(frame, ipOffset + 18);
        var oldSourcePort = ReadWord(frame, tcpOffset);
        var oldDestinationPort = ReadWord(frame, tcpOffset + 2);
        var oldHeaderChecksum = ReadWord(frame, ipOffset + 10);
        var oldTcpChecksum = ReadWord(frame, tcpOffset + 16);

        sourceAddress.TryWrite(frame.Slice(ipOffset + 12, 4), out _);
        destinationAddress.TryWrite(frame.Slice(ipOffset + 16, 4), out _);
        WritePorts(frame, tcpOffset, sourcePort, destinationPort);

        // Both checksums share the address delta (IPv4 header sum and TCP pseudo-header carry
        // the same address words); new words are read back after the write so the encoding has
        // a single source.
        var addressDelta = WordDelta(oldSource0, ReadWord(frame, ipOffset + 12))
            + WordDelta(oldSource1, ReadWord(frame, ipOffset + 14))
            + WordDelta(oldDestination0, ReadWord(frame, ipOffset + 16))
            + WordDelta(oldDestination1, ReadWord(frame, ipOffset + 18));
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(ipOffset + 10, 2), IncrementalChecksum(oldHeaderChecksum, addressDelta));
        var tcpDelta = addressDelta + WordDelta(oldSourcePort, sourcePort) + WordDelta(oldDestinationPort, destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(tcpOffset + 16, 2), IncrementalChecksum(oldTcpChecksum, tcpDelta));
        return true;
    }

    private static bool TryRewriteIpv6Tcp(Span<byte> frame, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        const int ipOffset = 14;
        if (sourceAddress.Family != AddressFamilyKind.IPv6 || destinationAddress.Family != sourceAddress.Family || frame.Length < ipOffset + 40 || frame[ipOffset] >> 4 != 6) return false;
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 4, 2));
        if (frame.Length < ipOffset + 40 + payloadLength || !TryFindIpv6Transport(frame, ipOffset, payloadLength, 6, out var tcpOffset)) return false;
        var tcpLength = ipOffset + 40 + payloadLength - tcpOffset;
        if (tcpLength < 20 || frame.Length < tcpOffset + tcpLength) return false;
        var dataOffset = (frame[tcpOffset + 12] >> 4) * 4;
        if (dataOffset < 20 || dataOffset > tcpLength) return false;

        Span<ushort> oldAddressWords = stackalloc ushort[16];
        FillAddressWords(frame, ipOffset, oldAddressWords);
        var oldSourcePort = ReadWord(frame, tcpOffset);
        var oldDestinationPort = ReadWord(frame, tcpOffset + 2);
        var oldTcpChecksum = ReadWord(frame, tcpOffset + 16);

        sourceAddress.TryWrite(frame.Slice(ipOffset + 8, 16), out _);
        destinationAddress.TryWrite(frame.Slice(ipOffset + 24, 16), out _);
        WritePorts(frame, tcpOffset, sourcePort, destinationPort);

        // IPv6 has no header checksum: the addresses feed only the TCP pseudo-header.
        uint tcpDelta = WordDelta(oldSourcePort, sourcePort) + WordDelta(oldDestinationPort, destinationPort);
        var wordIndex = 0;
        for (var offset = ipOffset + 8; offset < ipOffset + 40; offset += 2)
        {
            tcpDelta += WordDelta(oldAddressWords[wordIndex++], ReadWord(frame, offset));
        }
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(tcpOffset + 16, 2), IncrementalChecksum(oldTcpChecksum, tcpDelta));
        return true;
    }

    /// <summary>Full-segment endpoint rewrite kept as the property-test oracle for the
    /// incremental TCP paths: identical validation and mutation, but every checksum is
    /// recomputed over the whole header/segment.</summary>
    internal static bool TryRewriteTcpEndpointsFullRecompute(Span<byte> ethernetFrame, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        if (ethernetFrame.Length < 14) return false;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(ethernetFrame.Slice(12, 2));
        return etherType switch
        {
            0x0800 => RewriteIpv4TcpFull(ethernetFrame, sourceAddress, sourcePort, destinationAddress, destinationPort),
            0x86dd => RewriteIpv6TcpFull(ethernetFrame, sourceAddress, sourcePort, destinationAddress, destinationPort),
            _ => false
        };
    }

    private static bool RewriteIpv4TcpFull(Span<byte> frame, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        const int ipOffset = 14;
        var headerLength = (frame[ipOffset] & 0x0f) * 4;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 2, 2));
        var tcpOffset = ipOffset + headerLength;
        var tcpLength = totalLength - headerLength;
        sourceAddress.TryWrite(frame.Slice(ipOffset + 12, 4), out _);
        destinationAddress.TryWrite(frame.Slice(ipOffset + 16, 4), out _);
        WritePorts(frame, tcpOffset, sourcePort, destinationPort);
        frame[ipOffset + 10] = 0;
        frame[ipOffset + 11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(ipOffset + 10, 2), InternetChecksum(frame.Slice(ipOffset, headerLength)));
        WriteTcpChecksum(frame, tcpOffset, tcpLength, frame.Slice(ipOffset + 12, 4), frame.Slice(ipOffset + 16, 4), isIpv6: false);
        return true;
    }

    private static bool RewriteIpv6TcpFull(Span<byte> frame, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        const int ipOffset = 14;
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 4, 2));
        if (!TryFindIpv6Transport(frame, ipOffset, payloadLength, 6, out var tcpOffset)) return false;
        var tcpLength = ipOffset + 40 + payloadLength - tcpOffset;
        sourceAddress.TryWrite(frame.Slice(ipOffset + 8, 16), out _);
        destinationAddress.TryWrite(frame.Slice(ipOffset + 24, 16), out _);
        WritePorts(frame, tcpOffset, sourcePort, destinationPort);
        WriteTcpChecksum(frame, tcpOffset, tcpLength, frame.Slice(ipOffset + 8, 16), frame.Slice(ipOffset + 24, 16), isIpv6: true);
        return true;
    }

    private static ushort ReadWord(ReadOnlySpan<byte> frame, int offset) => BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset, 2));

    private static void FillAddressWords(ReadOnlySpan<byte> frame, int ipOffset, Span<ushort> words)
    {
        var wordIndex = 0;
        for (var offset = ipOffset + 8; offset < ipOffset + 40; offset += 2)
        {
            words[wordIndex++] = ReadWord(frame, offset);
        }
    }

    /// <summary>One's-complement delta contribution of a changed 16-bit word: ~m + m'.</summary>
    private static uint WordDelta(ushort oldWord, ushort newWord) => (uint)((oldWord ^ 0xffff) + newWord);

    private static ushort IncrementalChecksum(ushort checksum, uint delta)
    {
        var sum = (uint)(checksum ^ 0xffff) + delta;
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }

    private static bool TryFindIpv6Transport(ReadOnlySpan<byte> frame, int ipOffset, int payloadLength, byte targetNextHeader, out int transportOffset)
    {
        var nextHeader = frame[ipOffset + 6];
        transportOffset = ipOffset + 40;
        var extensionBytes = 0;
        while (nextHeader is 0 or 43 or 44 or 60)
        {
            if (nextHeader == 44 || frame.Length < transportOffset + 2) return false;
            var extensionLength = (frame[transportOffset + 1] + 1) * 8;
            if (extensionBytes + extensionLength > 256 || transportOffset + extensionLength > ipOffset + 40 + payloadLength) return false;
            nextHeader = frame[transportOffset];
            transportOffset += extensionLength;
            extensionBytes += extensionLength;
        }
        return nextHeader == targetNextHeader;
    }

    internal static void WriteTcpChecksum(Span<byte> frame, int tcpOffset, int tcpLength, ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination, bool isIpv6)
    {
        frame[tcpOffset + 16] = 0;
        frame[tcpOffset + 17] = 0;
        uint sum = Sum(source) + Sum(destination) + 6u;
        sum += isIpv6 ? (uint)tcpLength : (ushort)tcpLength;
        sum += Sum(frame.Slice(tcpOffset, tcpLength));
        // TCP has no UDP-style optional-zero-checksum: the folded value is stored verbatim,
        // so the rare 0x0000 result is written directly rather than inverted to 0xFFFF.
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(tcpOffset + 16, 2), Finish(sum));
    }

    private static void WritePorts(Span<byte> frame, int transportOffset, ushort sourcePort, ushort destinationPort)
    {
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(transportOffset, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(transportOffset + 2, 2), destinationPort);
    }

    internal static void WriteUdpChecksum(Span<byte> frame, int udpOffset, int udpLength, ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination, bool isIpv6)
    {
        frame[udpOffset + 6] = 0;
        frame[udpOffset + 7] = 0;
        uint sum = Sum(source) + Sum(destination) + 17u;
        sum += isIpv6 ? (uint)udpLength : (ushort)udpLength;
        sum += Sum(frame.Slice(udpOffset, udpLength));
        var checksum = Finish(sum);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(udpOffset + 6, 2), checksum == 0 ? ushort.MaxValue : checksum);
    }

    private static uint Sum(ReadOnlySpan<byte> data)
    {
        // Every 4 096 blocks (128 KiB) the u32 lanes are folded into the scalar sum and the sum
        // is folded fully to 16 bits. A lane holds two words per block, so one interval's eight
        // lanes sum to at most 65 536 words of 0xFFFF = 0xFFFF_0000; with the residual kept at
        // or below 0xFFFF the accumulation can never reach 2^32 — and uint wrap would NOT be
        // one's-complement-neutral (2^32 ≡ 1 mod 65 535). Generic checksum consumers exceed
        // the 64 KiB IP maximum, hence the periodic fold; the scalar fallback folds per word
        // for the same reason on hosts without hardware vectors.
        const int FoldBlockInterval = 4_096;
        uint sum = 0;
        var index = 0;
        if (Vector256.IsHardwareAccelerated && data.Length >= Vector256<byte>.Count)
        {
            var accumulator = Vector256<uint>.Zero;
            ref byte start = ref MemoryMarshal.GetReference(data);
            for (var blocks = 0; index + Vector256<byte>.Count <= data.Length; index += Vector256<byte>.Count)
            {
                var block = Vector256.LoadUnsafe(ref start, (nuint)index);
                var swapped = Vector256.Shuffle(block, SwapAdjacentBytes);
                var (lo, hi) = Vector256.Widen(swapped.AsUInt16());
                accumulator += lo + hi;
                if (++blocks == FoldBlockInterval)
                {
                    sum += Vector256.Sum(accumulator);
                    while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
                    accumulator = Vector256<uint>.Zero;
                    blocks = 0;
                }
            }
            sum += Vector256.Sum(accumulator);
        }
        for (; index + 1 < data.Length; index += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
            sum = (sum & 0xffff) + (sum >> 16);
        }
        if (index < data.Length) sum += (uint)data[index] << 8;
        return sum;
    }

    private static ushort Finish(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }
}
