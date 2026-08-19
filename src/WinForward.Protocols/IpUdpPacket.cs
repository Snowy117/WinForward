using System.Buffers.Binary;
using System.Net;

namespace WinForward.Protocols;

public readonly record struct UdpPacketView(IPAddress SourceAddress, IPAddress DestinationAddress, ushort SourcePort, ushort DestinationPort, ReadOnlyMemory<byte> Payload, int IpHeaderLength);

public static class IpUdpPacket
{
    public static bool TryParse(ReadOnlyMemory<byte> frame, out UdpPacketView packet)
    {
        packet = default;
        var bytes = frame.Span;
        if (bytes.Length < 14 + 20) return false;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(12, 2));
        return etherType switch
        {
            0x0800 => TryParseIpv4(frame, out packet),
            0x86dd => TryParseIpv6(frame, out packet),
            _ => false
        };
    }

    private static bool TryParseIpv4(ReadOnlyMemory<byte> frame, out UdpPacketView packet)
    {
        packet = default;
        var bytes = frame.Span;
        const int offset = 14;
        if (bytes.Length < offset + 20) return false;
        var versionAndHeader = bytes[offset];
        var headerLength = (versionAndHeader & 0x0f) * 4;
        if (versionAndHeader >> 4 != 4 || headerLength < 20 || bytes.Length < offset + headerLength || bytes[offset + 9] != 17) return false;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 2, 2));
        if (totalLength < headerLength + 8 || bytes.Length < offset + totalLength) return false;
        var fragment = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 6, 2));
        if ((fragment & 0xbfff) != 0) return false;
        var source = new IPAddress(bytes.Slice(offset + 12, 4));
        var destination = new IPAddress(bytes.Slice(offset + 16, 4));
        return TryParseUdp(frame, offset + headerLength, source, destination, headerLength, totalLength - headerLength, out packet);
    }

    private static bool TryParseIpv6(ReadOnlyMemory<byte> frame, out UdpPacketView packet)
    {
        packet = default;
        var bytes = frame.Span;
        const int offset = 14;
        if (bytes.Length < offset + 40) return false;
        if (bytes[offset] >> 4 != 6) return false;
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 4, 2));
        if (bytes.Length < offset + 40 + payloadLength) return false;
        var nextHeader = bytes[offset + 6];
        var transportOffset = offset + 40;
        var extensionBytes = 0;
        while (nextHeader is 0 or 43 or 44 or 60)
        {
            if (nextHeader == 44) return false;
            if (bytes.Length < transportOffset + 2) return false;
            var extensionLength = (bytes[transportOffset + 1] + 1) * 8;
            if (extensionBytes + extensionLength > 256 || transportOffset + extensionLength > offset + 40 + payloadLength) return false;
            nextHeader = bytes[transportOffset];
            transportOffset += extensionLength;
            extensionBytes += extensionLength;
        }
        if (nextHeader != 17) return false;
        var sourceV6 = new IPAddress(bytes.Slice(offset + 8, 16));
        var destinationV6 = new IPAddress(bytes.Slice(offset + 24, 16));
        return TryParseUdp(frame, transportOffset, sourceV6, destinationV6, transportOffset - offset, offset + 40 + payloadLength - transportOffset, out packet);
    }

    private static bool TryParseUdp(ReadOnlyMemory<byte> frame, int offset, IPAddress source, IPAddress destination, int ipHeaderLength, int udpLength, out UdpPacketView packet)
    {
        packet = default;
        var bytes = frame.Span;
        if (udpLength < 8 || bytes.Length < offset + udpLength) return false;
        var declaredLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 4, 2));
        if (declaredLength < 8 || declaredLength > udpLength) return false;
        packet = new UdpPacketView(source, destination, BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2)), BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 2, 2)), frame.Slice(offset + 8, declaredLength - 8), ipHeaderLength);
        return true;
    }
}
