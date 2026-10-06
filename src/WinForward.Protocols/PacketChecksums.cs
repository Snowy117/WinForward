using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using WinForward.Core;

namespace WinForward.Protocols;

public static class PacketChecksums
{
    public static ushort InternetChecksum(ReadOnlySpan<byte> data) => Finish(Sum(data));

    /// <summary>
    /// Rewrites only the IP addresses, UDP ports, and IPv4/UDP checksums of an Ethernet II
    /// IPv4/IPv6 UDP frame. There is no production caller: UDP flow translation does not rewrite
    /// endpoint headers, so this overload is kept as the independent protocol oracle for the UDP
    /// parse/checksum tests (<c>UdpPacketParsingTests</c>, <c>ProtocolAuditTests</c>).
    /// </summary>
    public static bool TryRewriteUdpEndpoints(Span<byte> ethernetFrame, IPAddress sourceAddress, ushort sourcePort, IPAddress destinationAddress, ushort destinationPort) => TryRewriteUdpEndpoints(ethernetFrame, IPAddressValue.From(sourceAddress), sourcePort, IPAddressValue.From(destinationAddress), destinationPort);

    private static bool TryRewriteUdpEndpoints(Span<byte> ethernetFrame, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        if (ethernetFrame.Length < 14) return false;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(ethernetFrame.Slice(12, 2));
        return etherType switch
        {
            0x0800 => TryRewriteIpv4(ethernetFrame, sourceAddress, sourcePort, destinationAddress, destinationPort),
            0x86dd => TryRewriteIpv6(ethernetFrame, sourceAddress, sourcePort, destinationAddress, destinationPort),
            _ => false,
        };
    }

    public static bool TryRewriteTcpEndpoints(Span<byte> ethernetFrame, IPAddress sourceAddress, ushort sourcePort, IPAddress destinationAddress, ushort destinationPort)
        => TryRewriteTcpEndpoints(ethernetFrame, IPAddressValue.From(sourceAddress), sourcePort, IPAddressValue.From(destinationAddress), destinationPort);

    public static bool TryRewriteTcpEndpoints(Span<byte> ethernetFrame, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        PacketPathProbe.RevalidateWalk?.Invoke();
        if (ethernetFrame.Length < 14) return false;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(ethernetFrame.Slice(12, 2));
        return etherType switch
        {
            0x0800 => TryRewriteIpv4Tcp(ethernetFrame, sourceAddress, sourcePort, destinationAddress, destinationPort),
            0x86dd => TryRewriteIpv6Tcp(ethernetFrame, sourceAddress, sourcePort, destinationAddress, destinationPort),
            _ => false,
        };
    }

    /// <summary>
    /// The layout-driven TCP endpoint rewrite for a frame a successful
    /// <see cref="IPTcpUdpPacket.TryParse"/> already proved: it consumes the parse's proofs
    /// (Ethernet II framing, IP version, header length in range, the transport protocol, no IPv4
    /// fragmentation, the total/payload length inside the frame, the IPv6 extension chain, the TCP
    /// header's presence and the <c>dataOffset</c> bound) and keeps only what the layout cannot
    /// prove — the layout is a parsed TCP layout (never the defaulted one a packet without a parse
    /// carries), the frame still covers the IP datagram the layout describes, and the argument
    /// addresses' family is the frame's family, because the write geometry depends on it and the
    /// addresses come from an association rather than the frame. It rejects without mutating,
    /// exactly like the span entry point that remains the independent oracle.
    /// </summary>
    public static bool TryRewriteTcpEndpoints(Span<byte> ethernetFrame, in PacketLayout layout, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        PacketPathProbe.ViewRewrite?.Invoke();
        if (!layout.IsTcp) return false;
        if (ethernetFrame.Length < layout.TransportEnd) return false;
        if (sourceAddress.Family != destinationAddress.Family || (byte)sourceAddress.Family != layout.Family) return false;
        return layout.Family == (byte)AddressFamilyKind.IPv6
            ? RewriteIpv6Tcp(ethernetFrame, layout.TransportOffset, sourceAddress, sourcePort, destinationAddress, destinationPort)
            : RewriteIpv4Tcp(ethernetFrame, layout.TransportOffset, sourceAddress, sourcePort, destinationAddress, destinationPort);
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

        return RewriteIpv4Tcp(frame, tcpOffset, sourceAddress, sourcePort, destinationAddress, destinationPort);
    }

    private static bool RewriteIpv4Tcp(Span<byte> frame, int tcpOffset, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        const int ipOffset = 14;
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

        return RewriteIpv6Tcp(frame, tcpOffset, sourceAddress, sourcePort, destinationAddress, destinationPort);
    }

    private static bool RewriteIpv6Tcp(Span<byte> frame, int tcpOffset, IPAddressValue sourceAddress, ushort sourcePort, IPAddressValue destinationAddress, ushort destinationPort)
    {
        const int ipOffset = 14;
        Span<ushort> oldAddressWords = stackalloc ushort[16];
        FillAddressWords(frame, ipOffset, oldAddressWords);
        var oldSourcePort = ReadWord(frame, tcpOffset);
        var oldDestinationPort = ReadWord(frame, tcpOffset + 2);
        var oldTcpChecksum = ReadWord(frame, tcpOffset + 16);

        sourceAddress.TryWrite(frame.Slice(ipOffset + 8, 16), out _);
        destinationAddress.TryWrite(frame.Slice(ipOffset + 24, 16), out _);
        WritePorts(frame, tcpOffset, sourcePort, destinationPort);

        // IPv6 has no header checksum: the addresses feed only the TCP pseudo-header.
        var tcpDelta = WordDelta(oldSourcePort, sourcePort) + WordDelta(oldDestinationPort, destinationPort);
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
            _ => false,
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
        var sum = Sum(source) + Sum(destination) + 6u;
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
        var sum = Sum(source) + Sum(destination) + 17u;
        sum += isIpv6 ? (uint)udpLength : (ushort)udpLength;
        sum += Sum(frame.Slice(udpOffset, udpLength));
        var checksum = Finish(sum);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(udpOffset + 6, 2), checksum == 0 ? ushort.MaxValue : checksum);
    }

    // One's-complement accumulation invariants (P2b): uint wrap is NOT one's-complement neutral
    // (2^32 == 1 mod 65 535) and this method is size-public, so no tier may accumulate a span
    // without folding. A vector block adds two words per lane, so 2 048 blocks put at most
    // 4 096 words = 0x0FFF_F000 in a lane; the widest tier's sixteen lanes reduce to at most
    // 65 536 words = 0xFFFF_0000, and with the scalar sum folded to at most 0xFFFF first the
    // accumulator peaks at exactly 0xFFFF_FFFF. The tail folds on the same 4 096-word cadence.
    //
    // The Vector<T> tier must stay gated on span length alone: Native AOT at the base instruction
    // set folds Vector256.IsHardwareAccelerated to false, which leaves such a build with no vector
    // path at all. Its byte swap must stay two shifts: Vector256.Shuffle reintroduces exactly that
    // AVX2 gate, and Vector<T> offers no shuffle whose width stays build-chosen.
    //
    // The fold interval has no headroom: at 2 048 blocks a lane's 0x0FFF_F000 and the sixteen-lane
    // reduction's 0xFFFF_0000 are what make the peak exactly 0xFFFF_FFFF. Raising the interval or
    // widening Vector<T> past 512 bits breaks the checksum on generic input without failing to
    // compile, so both need the bound re-derived rather than assumed.
    private static uint Sum(ReadOnlySpan<byte> data)
    {
        const int foldBlockInterval = 2_048;
        const int foldWordInterval = 4_096;
        var words = MemoryMarshal.Cast<byte, ushort>(data);
        ref var start = ref MemoryMarshal.GetReference(words);
        uint sum = 0;
        var wordOffset = 0;
        if (Vector512.IsHardwareAccelerated && words.Length >= Vector512<ushort>.Count)
        {
            var accumulator = Vector512<uint>.Zero;
            for (var blocks = 0; wordOffset + Vector512<ushort>.Count <= words.Length; wordOffset += Vector512<ushort>.Count)
            {
                var loaded = Vector512.LoadUnsafe(ref start, (nuint)wordOffset);
                var (lo, hi) = Vector512.Widen((loaded >> 8) | (loaded << 8));
                accumulator += lo + hi;
                if (++blocks == foldBlockInterval)
                {
                    sum = Fold(sum + Vector512.Sum(accumulator));
                    accumulator = Vector512<uint>.Zero;
                    blocks = 0;
                }
            }

            sum = Fold(sum + Vector512.Sum(accumulator));
        }
        else if (words.Length >= Vector<ushort>.Count)
        {
            var accumulator = Vector<uint>.Zero;
            for (var blocks = 0; wordOffset + Vector<ushort>.Count <= words.Length; wordOffset += Vector<ushort>.Count)
            {
                var loaded = Vector.LoadUnsafe(ref start, (nuint)wordOffset);
                Vector.Widen((loaded >> 8) | (loaded << 8), out var lo, out var hi);
                accumulator += lo + hi;
                if (++blocks == foldBlockInterval)
                {
                    sum = Fold(sum + Vector.Sum(accumulator));
                    accumulator = Vector<uint>.Zero;
                    blocks = 0;
                }
            }

            sum = Fold(sum + Vector.Sum(accumulator));
        }

        var index = wordOffset * 2;
        var wordsSinceFold = 0;
        for (; index + 1 < data.Length; index += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
            if (++wordsSinceFold != foldWordInterval) continue;
            sum = Fold(sum);
            wordsSinceFold = 0;
        }

        if (index < data.Length) sum += (uint)data[index] << 8;
        return sum;
    }

    private static uint Fold(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return sum;
    }

    private static ushort Finish(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }
}
