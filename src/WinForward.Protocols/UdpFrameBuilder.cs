using System.Buffers.Binary;
using System.Net;
using WinForward.Core;

namespace WinForward.Protocols;

/// <summary>
/// Builds a complete Ethernet II + IPv4/IPv6 + UDP frame from raw endpoints and payload, including
/// IPv4 header and UDP checksums. Pure and allocation-bounded: the only allocation is the returned
/// frame byte[]. Used to reinject SOCKS5 UDP relay responses back toward the original client.
/// </summary>
public static class UdpFrameBuilder
{
    /// <summary>The default pinned NDISAPI capture-frame ABI (Ethernet II plus max payload).</summary>
    public const int DefaultMaximumEthernetFrame = 1514;

    /// <summary>
    /// The pinned NDISAPI capture-frame ABI (Ethernet II plus max payload) used when no explicit cap
    /// is supplied. A jumbo-enabled (9014) ABI build is configured explicitly through
    /// <see cref="TryBuild"/>'s <paramref name="maximumEthernetFrame"/> parameter.
    /// </summary>
    public const int MaximumEthernetFrame = DefaultMaximumEthernetFrame;

    public static bool TryBuild(
        IPAddress sourceAddress,
        ushort sourcePort,
        IPAddress destinationAddress,
        ushort destinationPort,
        ReadOnlyMemory<byte> payload,
        ReadOnlySpan<byte> sourceMac,
        ReadOnlySpan<byte> destinationMac,
        out byte[] frame,
        int maximumEthernetFrame = DefaultMaximumEthernetFrame)
        => TryBuild(IPAddressValue.From(sourceAddress), sourcePort, IPAddressValue.From(destinationAddress), destinationPort, payload, sourceMac, destinationMac, out frame, maximumEthernetFrame);

    public static bool TryBuild(
        IPAddressValue sourceAddress,
        ushort sourcePort,
        IPAddressValue destinationAddress,
        ushort destinationPort,
        ReadOnlyMemory<byte> payload,
        ReadOnlySpan<byte> sourceMac,
        ReadOnlySpan<byte> destinationMac,
        out byte[] frame,
        int maximumEthernetFrame = DefaultMaximumEthernetFrame)
    {
        frame = [];
        if (!TryComputeFrameLength(sourceAddress, destinationAddress, payload, sourceMac, destinationMac, maximumEthernetFrame, out var totalLength)) return false;
        var result = new byte[totalLength];
        if (!TryBuildInto(sourceAddress, sourcePort, destinationAddress, destinationPort, payload, sourceMac, destinationMac, result, out _, maximumEthernetFrame)) return false;
        frame = result;
        return true;
    }

    /// <summary>
    /// Writes the complete Ethernet II + IPv4/IPv6 + UDP frame directly into
    /// <paramref name="destination"/> (for example a pooled native buffer's frame storage) so the
    /// response reinjection path allocates no managed frame per datagram. The allocating
    /// <see cref="TryBuild"/> delegates here, so both entry points share one header/checksum code
    /// path. Returns false for the same rejections as <see cref="TryBuild"/> or when the
    /// destination span is shorter than the computed frame.
    /// </summary>
    public static bool TryBuildInto(
        IPAddressValue sourceAddress,
        ushort sourcePort,
        IPAddressValue destinationAddress,
        ushort destinationPort,
        ReadOnlyMemory<byte> payload,
        ReadOnlySpan<byte> sourceMac,
        ReadOnlySpan<byte> destinationMac,
        Span<byte> destination,
        out int frameLength,
        int maximumEthernetFrame = DefaultMaximumEthernetFrame)
    {
        if (!TryComputeFrameLength(sourceAddress, destinationAddress, payload, sourceMac, destinationMac, maximumEthernetFrame, out frameLength)) return false;
        if (destination.Length < frameLength)
        {
            frameLength = 0;
            return false;
        }

        WriteFrame(destination, sourceAddress, sourcePort, destinationAddress, destinationPort, payload, sourceMac, destinationMac, frameLength);
        return true;
    }

    private static bool TryComputeFrameLength(
        IPAddressValue sourceAddress,
        IPAddressValue destinationAddress,
        ReadOnlyMemory<byte> payload,
        ReadOnlySpan<byte> sourceMac,
        ReadOnlySpan<byte> destinationMac,
        int maximumEthernetFrame,
        out int totalLength)
    {
        totalLength = 0;
        if (sourceMac.Length != 6 || destinationMac.Length != 6) return false;
        if (maximumEthernetFrame <= 0) return false;
        if (sourceAddress.Family != destinationAddress.Family) return false;
        var isIpv4 = sourceAddress.Family == AddressFamilyKind.IPv4;
        var isIpv6 = sourceAddress.Family == AddressFamilyKind.IPv6;
        if (!isIpv4 && !isIpv6) return false;

        if (payload.Length > ushort.MaxValue - 8) return false;
        var udpLength = 8 + payload.Length;
        if (isIpv4 && udpLength > ushort.MaxValue - 20) return false;
        var ipHeaderLength = isIpv4 ? 20 : 40;
        totalLength = 14 + ipHeaderLength + udpLength;
        if (totalLength > maximumEthernetFrame)
        {
            totalLength = 0;
            return false;
        }

        return true;
    }

    private static void WriteFrame(
        Span<byte> result,
        IPAddressValue sourceAddress,
        ushort sourcePort,
        IPAddressValue destinationAddress,
        ushort destinationPort,
        ReadOnlyMemory<byte> payload,
        ReadOnlySpan<byte> sourceMac,
        ReadOnlySpan<byte> destinationMac,
        int totalLength)
    {
        var isIpv4 = sourceAddress.Family == AddressFamilyKind.IPv4;

        // Ethernet II header: destination MAC, source MAC, ethertype.
        destinationMac.CopyTo(result[..6]);
        sourceMac.CopyTo(result.Slice(6, 6));
        BinaryPrimitives.WriteUInt16BigEndian(result.Slice(12, 2), isIpv4 ? (ushort)0x0800 : (ushort)0x86dd);

        const int ipOffset = 14;
        var ipHeaderLength = isIpv4 ? 20 : 40;
        var udpLength = totalLength - ipOffset - ipHeaderLength;
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
            PacketChecksums.WriteUdpChecksum(result, udpOffset, udpLength, result.Slice(ipOffset + 12, 4), result.Slice(ipOffset + 16, 4), isIpv6: false);
        }
        else
        {
            PacketChecksums.WriteUdpChecksum(result, udpOffset, udpLength, result.Slice(ipOffset + 8, 16), result.Slice(ipOffset + 24, 16), isIpv6: true);
        }
    }

    private static void WriteIpv4Header(Span<byte> frame, int ipOffset, IPAddressValue sourceAddress, IPAddressValue destinationAddress, int udpLength)
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
        sourceAddress.TryWrite(frame.Slice(ipOffset + 12, 4), out _);
        destinationAddress.TryWrite(frame.Slice(ipOffset + 16, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(ipOffset + 10, 2), PacketChecksums.InternetChecksum(frame.Slice(ipOffset, 20)));
    }

    private static void WriteIpv6Header(Span<byte> frame, int ipOffset, IPAddressValue sourceAddress, IPAddressValue destinationAddress, int udpLength)
    {
        frame[ipOffset] = 0x60; // version 6, traffic class 0
        frame[ipOffset + 1] = 0;
        frame[ipOffset + 2] = 0;
        frame[ipOffset + 3] = 0; // flow label 0
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(ipOffset + 4, 2), checked((ushort)udpLength));
        frame[ipOffset + 6] = 17; // next header: UDP
        frame[ipOffset + 7] = 64; // hop limit
        sourceAddress.TryWrite(frame.Slice(ipOffset + 8, 16), out _);
        destinationAddress.TryWrite(frame.Slice(ipOffset + 24, 16), out _);
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
