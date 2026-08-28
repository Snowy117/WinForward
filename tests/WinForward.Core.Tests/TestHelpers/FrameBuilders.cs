using System.Buffers.Binary;
using System.Net;

namespace WinForward.Core.Tests;

/// <summary>
/// Ethernet II + IPv4/IPv6 + TCP/UDP test frame builders. Every builder produces a structurally
/// valid frame with correct IP/TCP checksums (IPv4 UDP keeps its optional checksum zero, matching
/// real offloaded traffic). Signatures accept addresses, ports, TCP flags, data-offset words,
/// options, payload, and sequence number so callers can shape any test frame.
/// </summary>
internal static class FrameBuilders
{
    public const byte TcpFlagSyn = 0x02;
    public const byte TcpFlagPshAck = 0x18;

    public static byte[] BuildIpv4TcpSyn(IPAddress source, IPAddress destination, ushort sourcePort, ushort destinationPort, byte[]? payload = null) =>
        BuildIpv4TcpFrame(source, destination, sourcePort, destinationPort, TcpFlagSyn, payload: payload);

    public static byte[] BuildIpv4TcpFrame(
        IPAddress source,
        IPAddress destination,
        ushort sourcePort,
        ushort destinationPort,
        byte tcpFlags = TcpFlagPshAck,
        int tcpDataOffsetWords = 5,
        byte[]? options = null,
        byte[]? payload = null,
        uint sequence = 0x00000001)
    {
        options ??= [];
        payload ??= [];
        var tcpHeaderLength = tcpDataOffsetWords * 4;
        var optionPadding = new byte[tcpHeaderLength - 20 - options.Length];
        var totalLength = 20 + tcpHeaderLength + payload.Length;
        var frame = new byte[14 + totalLength];

        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), (ushort)totalLength);
        frame[23] = 6;
        source.TryWriteBytes(frame.AsSpan(26, 4), out _);
        destination.TryWriteBytes(frame.AsSpan(30, 4), out _);

        const int tcp = 34;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 2, 2), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 4, 4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 8, 4), 0x00000000);
        frame[tcp + 12] = (byte)(tcpDataOffsetWords << 4);
        frame[tcp + 13] = tcpFlags;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 14, 2), 0xffff);
        options.CopyTo(frame, tcp + 20);
        optionPadding.CopyTo(frame, tcp + 20 + options.Length);
        payload.CopyTo(frame, tcp + tcpHeaderLength);

        ChecksumMath.SetIpv4HeaderChecksum(frame);
        ChecksumMath.SetIpv4TcpChecksum(frame, tcp, totalLength - 20);
        return frame;
    }

    public static byte[] BuildIpv6TcpSyn(IPAddress source, IPAddress destination, ushort sourcePort, ushort destinationPort) =>
        BuildIpv6TcpFrame(source, destination, sourcePort, destinationPort);

    public static byte[] BuildIpv6TcpFrame(
        IPAddress source,
        IPAddress destination,
        ushort sourcePort,
        ushort destinationPort,
        byte[]? payload = null,
        byte tcpFlags = TcpFlagSyn,
        uint sequence = 0x00000002)
    {
        payload ??= [];
        var tcpLength = 20 + payload.Length;
        var frame = new byte[14 + 40 + tcpLength];

        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), (ushort)tcpLength);
        frame[20] = 6;
        source.TryWriteBytes(frame.AsSpan(22, 16), out _);
        destination.TryWriteBytes(frame.AsSpan(38, 16), out _);

        const int tcp = 54;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 2, 2), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 4, 4), sequence);
        frame[tcp + 12] = 0x50;
        frame[tcp + 13] = tcpFlags;
        payload.CopyTo(frame, tcp + 20);

        ChecksumMath.SetIpv6TcpChecksum(frame, tcp, tcpLength);
        return frame;
    }

    /// <summary>
    /// Layout: Ethernet(14) + IPv6(40) + Hop-by-Hop(8) + TCP(20). IPv6 next-header byte
    /// (offset 20) = 0 (Hop-by-Hop); the extension's next-header (offset 54) = 6 (TCP) with
    /// length 0, i.e. (0+1)*8 = 8 bytes.
    /// </summary>
    public static byte[] BuildIpv6TcpFrameWithHopByHop(IPAddress source, IPAddress destination, ushort sourcePort, ushort destinationPort)
    {
        const int tcpHeaderLength = 20;
        const int extensionLength = 8;
        const int ipv6PayloadLength = extensionLength + tcpHeaderLength;
        var frame = new byte[14 + 40 + ipv6PayloadLength];

        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), (ushort)ipv6PayloadLength);
        frame[20] = 0;
        source.TryWriteBytes(frame.AsSpan(22, 16), out _);
        destination.TryWriteBytes(frame.AsSpan(38, 16), out _);

        const int extension = 54;
        frame[extension] = 6;
        frame[extension + 1] = 0;

        const int tcp = 62;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 2, 2), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 4, 4), 0x00000003);
        frame[tcp + 12] = 0x50;
        frame[tcp + 13] = TcpFlagSyn;

        ChecksumMath.SetIpv6TcpChecksum(frame, tcp, tcpHeaderLength);
        return frame;
    }

    /// <summary>IPv4 UDP datagram; the UDP checksum stays zero (legal for IPv4).</summary>
    public static byte[] BuildIpv4UdpFrame(IPAddress source, IPAddress destination, ushort sourcePort, ushort destinationPort, byte[]? payload = null)
    {
        payload ??= [];
        var udpLength = 8 + payload.Length;
        var totalLength = 20 + udpLength;
        var frame = new byte[14 + totalLength];

        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), (ushort)totalLength);
        frame[23] = 17;
        source.TryWriteBytes(frame.AsSpan(26, 4), out _);
        destination.TryWriteBytes(frame.AsSpan(30, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38, 2), (ushort)udpLength);
        payload.CopyTo(frame, 42);

        ChecksumMath.SetIpv4HeaderChecksum(frame);
        return frame;
    }

    /// <summary>IPv6 UDP datagram; the UDP checksum stays zero (parse-only tests do not validate it).</summary>
    public static byte[] BuildIpv6UdpFrame(IPAddress source, IPAddress destination, ushort sourcePort, ushort destinationPort, byte[]? payload = null)
    {
        payload ??= [];
        var udpLength = 8 + payload.Length;
        var frame = new byte[14 + 40 + udpLength];

        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), (ushort)udpLength);
        frame[20] = 17;
        frame[21] = 64;
        source.TryWriteBytes(frame.AsSpan(22, 16), out _);
        destination.TryWriteBytes(frame.AsSpan(38, 16), out _);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(54, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(56, 2), destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(58, 2), (ushort)udpLength);
        payload.CopyTo(frame, 62);

        return frame;
    }

    // ---- Capture-pipeline fixed-address wrappers ----

    internal static byte[] CreateIpv4TcpFrame() =>
        BuildIpv4TcpFrame(IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.53"), 53000, 443, tcpFlags: 0);

    internal static byte[] CreateIpv4UdpFrame() =>
        BuildIpv4UdpFrame(IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.53"), 53000, 53);

    internal static byte[] CreateIpv6TcpFrame() =>
        BuildIpv6TcpFrame(IPAddress.Parse("2001:db8::10"), IPAddress.Parse("2001:db8::53"), 53000, 443, tcpFlags: 0, sequence: 0);

    internal static byte[] CreateIpv6UdpFrame() =>
        BuildIpv6UdpFrame(IPAddress.Parse("2001:db8::10"), IPAddress.Parse("2001:db8::53"), 53000, 53);
}
