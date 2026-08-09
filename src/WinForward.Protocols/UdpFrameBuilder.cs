using System.Buffers.Binary;
using System.Net;

namespace WinForward.Protocols;

/// <summary>
/// Builds a complete Ethernet II + IPv4/IPv6 + UDP frame from raw endpoints and payload, including
/// IPv4 header and UDP checksums. Pure and allocation-bounded: the only allocation is the returned
/// frame byte[]. Used to reinject SOCKS5 UDP relay responses back toward the original client.
/// </summary>
public static class UdpFrameBuilder
{
    /// <summary>The pinned NDISAPI capture-frame ABI (Ethernet II plus max payload).</summary>
    public const int MaximumEthernetFrame = 1514;

    public static bool TryBuild(
        IPAddress sourceAddress,
        ushort sourcePort,
        IPAddress destinationAddress,
        ushort destinationPort,
        ReadOnlyMemory<byte> payload,
        ReadOnlySpan<byte> sourceMac,
        ReadOnlySpan<byte> destinationMac,
        out byte[] frame)
    {
        frame = [];
        if (sourceMac.Length != 6 || destinationMac.Length != 6) return false;
        if (sourceAddress.AddressFamily != destinationAddress.AddressFamily) return false;
        var isIpv4 = sourceAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        var isIpv6 = sourceAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        if (!isIpv4 && !isIpv6) return false;

        var ipHeaderLength = isIpv4 ? 20 : 40;
        var udpLength = checked(8 + payload.Length);
        var totalLength = checked(14 + ipHeaderLength + udpLength);
        if (totalLength > MaximumEthernetFrame) return false;

        var result = new byte[totalLength];

        // Ethernet II header: destination MAC, source MAC, ethertype.
        destinationMac.CopyTo(result.AsSpan(0, 6));
        sourceMac.CopyTo(result.AsSpan(6, 6));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(12, 2), isIpv4 ? (ushort)0x0800 : (ushort)0x86dd);

        const int ipOffset = 14;
        if (isIpv4)
        {
            WriteIpv4Header(result, ipOffset, sourceAddress, destinationAddress, udpLength);
        }
        else
        {
            WriteIpv6Header(result, ipOffset, sourceAddress, destinationAddress, udpLength);
        }

        var udpOffset = ipOffset + ipHeaderLength;
        WriteUdpHeader(result, udpOffset, sourcePort, destinationPort, udpLength, payload.Span);

        if (isIpv4)
        {
            PacketChecksums.WriteUdpChecksum(result, udpOffset, udpLength, result.AsSpan(ipOffset + 12, 4), result.AsSpan(ipOffset + 16, 4), isIpv6: false);
        }
        else
        {
            PacketChecksums.WriteUdpChecksum(result, udpOffset, udpLength, result.AsSpan(ipOffset + 8, 16), result.AsSpan(ipOffset + 24, 16), isIpv6: true);
        }

        frame = result;
        return true;
    }

    private static void WriteIpv4Header(Span<byte> frame, int ipOffset, IPAddress sourceAddress, IPAddress destinationAddress, int udpLength)
    {
        frame[ipOffset] = 0x45; // version 4, IHL 5 (no options)
        frame[ipOffset + 1] = 0; // DSCP/ECN
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(ipOffset + 2, 2), checked((ushort)(20 + udpLength)));
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(ipOffset + 4, 2), 0); // identification
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(ipOffset + 6, 2), 0x4000); // DF, no fragment offset
        frame[ipOffset + 8] = 64; // TTL
        frame[ipOffset + 9] = 17; // UDP
        // Header checksum is computed after the address fields are written.
        frame[ipOffset + 10] = 0;
        frame[ipOffset + 11] = 0;
        sourceAddress.TryWriteBytes(frame.Slice(ipOffset + 12, 4), out _);
        destinationAddress.TryWriteBytes(frame.Slice(ipOffset + 16, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(ipOffset + 10, 2), PacketChecksums.InternetChecksum(frame.Slice(ipOffset, 20)));
    }

    private static void WriteIpv6Header(Span<byte> frame, int ipOffset, IPAddress sourceAddress, IPAddress destinationAddress, int udpLength)
    {
        frame[ipOffset] = 0x60; // version 6, traffic class 0
        frame[ipOffset + 1] = 0;
        frame[ipOffset + 2] = 0;
        frame[ipOffset + 3] = 0; // flow label 0
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(ipOffset + 4, 2), checked((ushort)udpLength));
        frame[ipOffset + 6] = 17; // next header: UDP
        frame[ipOffset + 7] = 64; // hop limit
        sourceAddress.TryWriteBytes(frame.Slice(ipOffset + 8, 16), out _);
        destinationAddress.TryWriteBytes(frame.Slice(ipOffset + 24, 16), out _);
    }

    private static void WriteUdpHeader(Span<byte> frame, int udpOffset, ushort sourcePort, ushort destinationPort, int udpLength, ReadOnlySpan<byte> payload)
    {
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(udpOffset, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(udpOffset + 2, 2), destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(udpOffset + 4, 2), checked((ushort)udpLength));
        // UDP checksum field is zeroed and filled by PacketChecksums.WriteUdpChecksum (0 -> 0xFFFF
        // per RFC 768 handling in the shared helper).
        payload.CopyTo(frame[(udpOffset + 8)..]);
    }
}