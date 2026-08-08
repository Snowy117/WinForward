using System.Buffers.Binary;
using System.Net;

namespace WinForward.Protocols;

public static class PacketChecksums
{
    public static ushort InternetChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var index = 0;
        for (; index + 1 < data.Length; index += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
        if (index < data.Length) sum += (uint)data[index] << 8;
        return Finish(sum);
    }

    public static bool TryRewriteUdpEndpoints(Span<byte> ethernetFrame, IPAddress sourceAddress, ushort sourcePort, IPAddress destinationAddress, ushort destinationPort)
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

    private static bool TryRewriteIpv4(Span<byte> frame, IPAddress sourceAddress, ushort sourcePort, IPAddress destinationAddress, ushort destinationPort)
    {
        const int ipOffset = 14;
        if (sourceAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || destinationAddress.AddressFamily != sourceAddress.AddressFamily || frame.Length < ipOffset + 20) return false;
        var headerLength = (frame[ipOffset] & 0x0f) * 4;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 2, 2));
        if (frame[ipOffset] >> 4 != 4 || headerLength < 20 || frame[ipOffset + 9] != 17 || totalLength < headerLength + 8 || frame.Length < ipOffset + totalLength) return false;
        var fragment = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 6, 2));
        if ((fragment & 0x3fff) != 0) return false;
        var udpOffset = ipOffset + headerLength;
        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(udpOffset + 4, 2));
        if (udpLength < 8 || udpOffset + udpLength > ipOffset + totalLength) return false;

        sourceAddress.TryWriteBytes(frame.Slice(ipOffset + 12, 4), out _);
        destinationAddress.TryWriteBytes(frame.Slice(ipOffset + 16, 4), out _);
        WritePorts(frame, udpOffset, sourcePort, destinationPort);
        frame[ipOffset + 10] = 0;
        frame[ipOffset + 11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(ipOffset + 10, 2), InternetChecksum(frame.Slice(ipOffset, headerLength)));
        WriteUdpChecksum(frame, udpOffset, udpLength, frame.Slice(ipOffset + 12, 4), frame.Slice(ipOffset + 16, 4), isIpv6: false);
        return true;
    }

    private static bool TryRewriteIpv6(Span<byte> frame, IPAddress sourceAddress, ushort sourcePort, IPAddress destinationAddress, ushort destinationPort)
    {
        const int ipOffset = 14;
        if (sourceAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 || destinationAddress.AddressFamily != sourceAddress.AddressFamily || frame.Length < ipOffset + 40 || frame[ipOffset] >> 4 != 6) return false;
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 4, 2));
        if (frame.Length < ipOffset + 40 + payloadLength || !TryFindIpv6Transport(frame, ipOffset, payloadLength, 17, out var udpOffset)) return false;
        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(udpOffset + 4, 2));
        if (udpLength < 8 || udpOffset + udpLength > ipOffset + 40 + payloadLength) return false;

        sourceAddress.TryWriteBytes(frame.Slice(ipOffset + 8, 16), out _);
        destinationAddress.TryWriteBytes(frame.Slice(ipOffset + 24, 16), out _);
        WritePorts(frame, udpOffset, sourcePort, destinationPort);
        WriteUdpChecksum(frame, udpOffset, udpLength, frame.Slice(ipOffset + 8, 16), frame.Slice(ipOffset + 24, 16), isIpv6: true);
        return true;
    }

    private static bool TryRewriteIpv4Tcp(Span<byte> frame, IPAddress sourceAddress, ushort sourcePort, IPAddress destinationAddress, ushort destinationPort)
    {
        const int ipOffset = 14;
        if (sourceAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || destinationAddress.AddressFamily != sourceAddress.AddressFamily || frame.Length < ipOffset + 20) return false;
        var headerLength = (frame[ipOffset] & 0x0f) * 4;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 2, 2));
        if (frame[ipOffset] >> 4 != 4 || headerLength < 20 || frame[ipOffset + 9] != 6 || totalLength < headerLength + 20 || frame.Length < ipOffset + totalLength) return false;
        var fragment = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 6, 2));
        if ((fragment & 0x3fff) != 0) return false;
        var tcpOffset = ipOffset + headerLength;
        var tcpLength = totalLength - headerLength;
        if (tcpLength < 20 || frame.Length < tcpOffset + tcpLength) return false;
        var dataOffset = (frame[tcpOffset + 12] >> 4) * 4;
        if (dataOffset < 20 || dataOffset > tcpLength) return false;

        sourceAddress.TryWriteBytes(frame.Slice(ipOffset + 12, 4), out _);
        destinationAddress.TryWriteBytes(frame.Slice(ipOffset + 16, 4), out _);
        WritePorts(frame, tcpOffset, sourcePort, destinationPort);
        frame[ipOffset + 10] = 0;
        frame[ipOffset + 11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(ipOffset + 10, 2), InternetChecksum(frame.Slice(ipOffset, headerLength)));
        WriteTcpChecksum(frame, tcpOffset, tcpLength, frame.Slice(ipOffset + 12, 4), frame.Slice(ipOffset + 16, 4), isIpv6: false);
        return true;
    }

    private static bool TryRewriteIpv6Tcp(Span<byte> frame, IPAddress sourceAddress, ushort sourcePort, IPAddress destinationAddress, ushort destinationPort)
    {
        const int ipOffset = 14;
        if (sourceAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 || destinationAddress.AddressFamily != sourceAddress.AddressFamily || frame.Length < ipOffset + 40 || frame[ipOffset] >> 4 != 6) return false;
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 4, 2));
        if (frame.Length < ipOffset + 40 + payloadLength || !TryFindIpv6Transport(frame, ipOffset, payloadLength, 6, out var tcpOffset)) return false;
        var tcpLength = ipOffset + 40 + payloadLength - tcpOffset;
        if (tcpLength < 20 || frame.Length < tcpOffset + tcpLength) return false;
        var dataOffset = (frame[tcpOffset + 12] >> 4) * 4;
        if (dataOffset < 20 || dataOffset > tcpLength) return false;

        sourceAddress.TryWriteBytes(frame.Slice(ipOffset + 8, 16), out _);
        destinationAddress.TryWriteBytes(frame.Slice(ipOffset + 24, 16), out _);
        WritePorts(frame, tcpOffset, sourcePort, destinationPort);
        WriteTcpChecksum(frame, tcpOffset, tcpLength, frame.Slice(ipOffset + 8, 16), frame.Slice(ipOffset + 24, 16), isIpv6: true);
        return true;
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

    private static void WriteTcpChecksum(Span<byte> frame, int tcpOffset, int tcpLength, ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination, bool isIpv6)
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

    private static void WriteUdpChecksum(Span<byte> frame, int udpOffset, int udpLength, ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination, bool isIpv6)
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
}
