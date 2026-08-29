using System.Buffers.Binary;
using WinForward.Core;

namespace WinForward.Protocols;

/// <summary>
/// Raw IP-level views of Ethernet II frames carrying IP fragments — frames the flow parsers
/// reject because they cannot be classified or rewritten safely. Detection is a pure bit test
/// over the frame header (IPv4 flags/fragment-offset mask 0xbfff, or an IPv6 fragment header
/// anywhere in the extension-header chain, mirroring <see cref="IPTcpUdpPacket"/>); the address
/// pair is read from the base IP header, which every fragment carries. Non-first fragments
/// carry no transport header, so the address pair is the finest key a fragment can be
/// attributed by.
/// </summary>
public static class IPFragment
{
    private const ushort EtherTypeIpv4 = 0x0800;
    private const ushort EtherTypeIpv6 = 0x86dd;
    private const int EthernetHeaderLength = 14;
    private const int Ipv4HeaderLength = 20;
    private const int Ipv6HeaderLength = 40;
    private const byte Ipv6FragmentHeader = 44;

    /// <summary>
    /// Detects an IP fragment: an IPv4 frame with any of the reserved/MF bits or a non-zero
    /// fragment offset set (mask <c>0xbfff</c>, DF allowed — the canonical parser's semantics),
    /// or an IPv6 frame whose extension-header chain contains a fragment header. Returns false
    /// for non-IP frames, non-fragment frames, and malformed headers.
    /// </summary>
    public static bool IsFragment(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < EthernetHeaderLength) return false;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        return etherType switch
        {
            EtherTypeIpv4 => IsIpv4Fragment(frame),
            EtherTypeIpv6 => IsIpv6Fragment(frame),
            _ => false
        };
    }

    /// <summary>
    /// Reads the IP source and destination addresses of a fragment frame (either family) from
    /// its base IP header. Fails for non-IP frames, truncated headers, or a version mismatch.
    /// </summary>
    public static bool TryReadAddressPair(ReadOnlySpan<byte> frame, out IPAddressValue source, out IPAddressValue destination)
    {
        source = default;
        destination = default;
        if (frame.Length < EthernetHeaderLength) return false;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        switch (etherType)
        {
            case EtherTypeIpv4:
                if (frame.Length < EthernetHeaderLength + Ipv4HeaderLength || frame[EthernetHeaderLength] >> 4 != 4) return false;
                source = IPAddressValue.FromIPv4(frame.Slice(EthernetHeaderLength + 12, 4));
                destination = IPAddressValue.FromIPv4(frame.Slice(EthernetHeaderLength + 16, 4));
                return true;
            case EtherTypeIpv6:
                if (frame.Length < EthernetHeaderLength + Ipv6HeaderLength || frame[EthernetHeaderLength] >> 4 != 6) return false;
                source = IPAddressValue.FromIPv6(frame.Slice(EthernetHeaderLength + 8, 16));
                destination = IPAddressValue.FromIPv6(frame.Slice(EthernetHeaderLength + 24, 16));
                return true;
            default:
                return false;
        }
    }

    private static bool IsIpv4Fragment(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < EthernetHeaderLength + Ipv4HeaderLength || frame[EthernetHeaderLength] >> 4 != 4) return false;
        var fragment = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(EthernetHeaderLength + 6, 2));
        return (fragment & 0xbfff) != 0;
    }

    private static bool IsIpv6Fragment(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < EthernetHeaderLength + Ipv6HeaderLength || frame[EthernetHeaderLength] >> 4 != 6) return false;
        // Walk the extension-header chain with the canonical parser's rules and bounds; a
        // fragment header reached anywhere in the chain makes the frame a fragment, and a
        // malformed chain is not a fragment (it keeps the non-flow pass behavior).
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(EthernetHeaderLength + 4, 2));
        var nextHeader = frame[EthernetHeaderLength + 6];
        var offset = EthernetHeaderLength + Ipv6HeaderLength;
        var extensionBytes = 0;
        while (nextHeader is 0 or 43 or 60)
        {
            if (offset + 2 > frame.Length) return false;
            var extensionLength = (frame[offset + 1] + 1) * 8;
            if (extensionBytes + extensionLength > 256 || offset + extensionLength > EthernetHeaderLength + Ipv6HeaderLength + payloadLength) return false;
            nextHeader = frame[offset];
            offset += extensionLength;
            extensionBytes += extensionLength;
        }
        return nextHeader == Ipv6FragmentHeader;
    }
}
