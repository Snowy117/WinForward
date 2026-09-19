using System.Buffers.Binary;
using System.Runtime.InteropServices;
using WinForward.Core;

namespace WinForward.Protocols;

/// <summary>
/// A parsed IPv4/IPv6 UDP header whose payload is an offset/length pair into the parsed frame
/// rather than a <see cref="ReadOnlyMemory{T}"/> slice, so a consumer holding only a span (a
/// native capture buffer viewed through <c>CapturedFlowPacket.InspectionSpan</c>) can parse and
/// slice without any managed backing. Use <see cref="Payload"/> to recover the payload slice from
/// the same span.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct UdpPacketSpanView(IPAddressValue SourceAddress, IPAddressValue DestinationAddress, ushort SourcePort, ushort DestinationPort, int PayloadOffset, int PayloadLength, int IPHeaderLength)
{
    /// <summary>The payload slice of the frame this view was parsed from.</summary>
    public ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> frame) => frame.Slice(PayloadOffset, PayloadLength);
}

public static class IPUdpPacket
{
    /// <summary>
    /// Span-based parse for synchronous hot-path consumers: the payload is reported as an
    /// offset/length pair into <paramref name="frame"/>, so parsing and slicing never touch the
    /// managed heap.
    /// </summary>
    public static bool TryParseSpan(ReadOnlySpan<byte> frame, out UdpPacketSpanView packet)
    {
        packet = default;
        if (frame.Length < 14 + 20) return false;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        return etherType switch
        {
            0x0800 => TryParseIpv4(frame, out packet),
            0x86dd => TryParseIpv6(frame, out packet),
            _ => false
        };
    }

    private static bool TryParseIpv4(ReadOnlySpan<byte> bytes, out UdpPacketSpanView packet)
    {
        packet = default;
        const int offset = 14;
        if (bytes.Length < offset + 20) return false;
        var versionAndHeader = bytes[offset];
        var headerLength = (versionAndHeader & 0x0f) * 4;
        if (versionAndHeader >> 4 != 4 || headerLength < 20 || bytes.Length < offset + headerLength || bytes[offset + 9] != 17) return false;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 2, 2));
        if (totalLength < headerLength + 8 || bytes.Length < offset + totalLength) return false;
        var fragment = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 6, 2));
        if ((fragment & 0xbfff) != 0) return false;
        var source = IPAddressValue.FromIPv4(bytes.Slice(offset + 12, 4));
        var destination = IPAddressValue.FromIPv4(bytes.Slice(offset + 16, 4));
        return TryParseUdp(bytes, offset + headerLength, source, destination, headerLength, totalLength - headerLength, out packet);
    }

    private static bool TryParseIpv6(ReadOnlySpan<byte> bytes, out UdpPacketSpanView packet)
    {
        packet = default;
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
        var sourceV6 = IPAddressValue.FromIPv6(bytes.Slice(offset + 8, 16));
        var destinationV6 = IPAddressValue.FromIPv6(bytes.Slice(offset + 24, 16));
        return TryParseUdp(bytes, transportOffset, sourceV6, destinationV6, transportOffset - offset, offset + 40 + payloadLength - transportOffset, out packet);
    }

    private static bool TryParseUdp(ReadOnlySpan<byte> bytes, int offset, IPAddressValue source, IPAddressValue destination, int ipHeaderLength, int udpLength, out UdpPacketSpanView packet)
    {
        packet = default;
        if (udpLength < 8 || bytes.Length < offset + udpLength) return false;
        var declaredLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 4, 2));
        if (declaredLength < 8 || declaredLength > udpLength) return false;
        packet = new UdpPacketSpanView(source, destination, BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2)), BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 2, 2)), offset + 8, declaredLength - 8, ipHeaderLength);
        return true;
    }
}
