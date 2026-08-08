using System.Buffers.Binary;
using System.Net;

namespace WinForward.Protocols;

public enum PacketTransport : byte
{
    Tcp,
    Udp
}

/// <summary>
/// A bounds-checked view of an Ethernet II frame carrying an IPv4 or IPv6 TCP/UDP packet
/// that is safe for flow classification. The transport is either TCP or UDP; the source and
/// destination endpoints are exposed for flow-key construction. IP fragments and unsupported
/// IPv6 extension-header chains are rejected because they cannot be classified safely.
/// </summary>
public readonly record struct PacketView(
    PacketTransport Transport,
    IPAddress SourceAddress,
    IPAddress DestinationAddress,
    ushort SourcePort,
    ushort DestinationPort,
    int IpHeaderLength,
    int TransportHeaderLength)
{
    public ushort RemotePort => DestinationPort;
}

public static class IpTcpUdpPacket
{
    private const ushort EtherTypeIpv4 = 0x0800;
    private const ushort EtherTypeIpv6 = 0x86dd;
    private const byte ProtocolTcp = 6;
    private const byte ProtocolUdp = 17;
    private const int EthernetHeaderLength = 14;

    /// <summary>
    /// Parses a captured Ethernet frame into a flow-classifiable TCP/UDP view. Returns false for
    /// non-Ethernet-II frames, non-IP frames, fragmented IP traffic, malformed or truncated headers,
    /// and unsupported IPv6 extension-header chains. No allocation occurs on the parse path.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> frame, out PacketView view)
    {
        view = default;
        if (frame.Length < EthernetHeaderLength + 20) return false;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        return etherType switch
        {
            EtherTypeIpv4 => TryParseIpv4(frame, out view),
            EtherTypeIpv6 => TryParseIpv6(frame, out view),
            _ => false
        };
    }

    private static bool TryParseIpv4(ReadOnlySpan<byte> frame, out PacketView view)
    {
        view = default;
        const int ipOffset = EthernetHeaderLength;
        var versionAndHeader = frame[ipOffset];
        var headerLength = (versionAndHeader & 0x0f) * 4;
        if (versionAndHeader >> 4 != 4 || headerLength < 20 || frame.Length < ipOffset + headerLength) return false;
        var protocol = frame[ipOffset + 9];
        if (protocol is not ProtocolTcp and not ProtocolUdp) return false;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 2, 2));
        if (totalLength < headerLength + 8 || frame.Length < ipOffset + totalLength) return false;
        var fragment = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 6, 2));
        if ((fragment & 0x3fff) != 0) return false;

        var source = new IPAddress(frame.Slice(ipOffset + 12, 4));
        var destination = new IPAddress(frame.Slice(ipOffset + 16, 4));
        return TryParseTransport(frame, ipOffset + headerLength, totalLength - headerLength, protocol, source, destination, headerLength, out view);
    }

    private static bool TryParseIpv6(ReadOnlySpan<byte> frame, out PacketView view)
    {
        view = default;
        const int ipOffset = EthernetHeaderLength;
        if (frame.Length < ipOffset + 40 || frame[ipOffset] >> 4 != 6) return false;
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(ipOffset + 4, 2));
        if (frame.Length < ipOffset + 40 + payloadLength) return false;

        var nextHeader = frame[ipOffset + 6];
        var transportOffset = ipOffset + 40;
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
        if (nextHeader is not ProtocolTcp and not ProtocolUdp) return false;

        var source = new IPAddress(frame.Slice(ipOffset + 8, 16));
        var destination = new IPAddress(frame.Slice(ipOffset + 24, 16));
        return TryParseTransport(frame, transportOffset, ipOffset + 40 + payloadLength - transportOffset, nextHeader, source, destination, transportOffset - ipOffset, out view);
    }

    private static bool TryParseTransport(
        ReadOnlySpan<byte> frame,
        int transportOffset,
        int availableLength,
        byte protocol,
        IPAddress source,
        IPAddress destination,
        int ipHeaderLength,
        out PacketView view)
    {
        view = default;
        if (availableLength < 8 || frame.Length < transportOffset + 8) return false;
        var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(transportOffset, 2));
        var destinationPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(transportOffset + 2, 2));

        if (protocol == ProtocolUdp)
        {
            var declaredLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(transportOffset + 4, 2));
            if (declaredLength < 8 || declaredLength > availableLength) return false;
            view = new PacketView(PacketTransport.Udp, source, destination, sourcePort, destinationPort, ipHeaderLength, 8);
            return true;
        }

        // TCP data-offset field is the top four bits of the 12th byte (offset 12 within the header).
        var dataOffset = (frame[transportOffset + 12] >> 4) * 4;
        if (dataOffset < 20 || dataOffset > availableLength) return false;
        view = new PacketView(PacketTransport.Tcp, source, destination, sourcePort, destinationPort, ipHeaderLength, dataOffset);
        return true;
    }
}