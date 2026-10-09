using System.Buffers.Binary;
using WinForward.Core;

namespace WinForward.Protocols;

/// <summary>
/// Builds the standalone close frames one connection's original server endpoint sends when
/// WinForward ends it itself: a TCP RST|ACK that aborts, and a TCP FIN|ACK that closes cleanly.
/// Used to surface a failed upstream relay to the client as an immediate, protocol-correct close
/// instead of a silent hang. The Ethernet header is mirrored from the recorded client SYN
/// (addresses swapped) so the frame is deliverable on the same L2 segment; everything at L3/L4 is
/// constructed fresh with its own checksums, so the result never depends on parser or rewrite state.
/// </summary>
public static class TcpResetBuilder
{
    private const int EthernetHeaderLength = 14;
    private const int IPv4HeaderLength = 20;
    private const int IPv6HeaderLength = 40;
    private const int TcpHeaderLength = 20;
    private const byte TcpFlagsOffset = 13;
    private const byte TcpResetAck = 0x14;
    private const byte TcpFinAck = 0x11;

    /// <summary>The largest reset frame the builder can produce (IPv6: 14 + 40 + 20 = 74).</summary>
    public const int MaxResetFrameLength = EthernetHeaderLength + IPv6HeaderLength + TcpHeaderLength;

    /// <summary>
    /// Writes the reset frame into <paramref name="destination"/> (at least
    /// <see cref="MaxResetFrameLength"/> bytes) and reports the exact frame length.
    /// <paramref name="serverSequenceNext"/> must be the sequence the client expects next from
    /// the server (its in-window value, typically server-ISN + 1) and <paramref name="clientSequenceNext"/>
    /// the acknowledged client sequence (client-ISN + 1). Returns false without writing when the
    /// template is not a usable Ethernet IPv4/IPv6 frame, the address families do not match it,
    /// or <paramref name="destination"/> is too small.
    /// </summary>
    public static bool TryBuildReset(
        ReadOnlySpan<byte> originalSynFrame,
        IPAddressValue serverAddress,
        ushort serverPort,
        IPAddressValue clientAddress,
        ushort clientPort,
        uint serverSequenceNext,
        uint clientSequenceNext,
        Span<byte> destination,
        out int written)
        => TryBuild(originalSynFrame, serverAddress, serverPort, clientAddress, clientPort, serverSequenceNext, clientSequenceNext, TcpResetAck, destination, out written);

    public static bool TryBuildFin(
        ReadOnlySpan<byte> originalSynFrame,
        IPAddressValue serverAddress,
        ushort serverPort,
        IPAddressValue clientAddress,
        ushort clientPort,
        uint serverSequenceNext,
        uint clientSequenceNext,
        Span<byte> destination,
        out int written)
        => TryBuild(originalSynFrame, serverAddress, serverPort, clientAddress, clientPort, serverSequenceNext, clientSequenceNext, TcpFinAck, destination, out written);

    private static bool TryBuild(
        ReadOnlySpan<byte> originalSynFrame,
        IPAddressValue serverAddress,
        ushort serverPort,
        IPAddressValue clientAddress,
        ushort clientPort,
        uint serverSequenceNext,
        uint clientSequenceNext,
        byte tcpFlags,
        Span<byte> destination,
        out int written)
    {
        written = 0;
        if (originalSynFrame.Length < EthernetHeaderLength) return false;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(originalSynFrame.Slice(12, 2));
        // ReSharper disable once ConvertIfStatementToSwitchStatement // Each arm guards on the etherType AND both address families; `switch` + `when` clauses would bury those guards in a hot-path frame builder.
        if (etherType == 0x0800 && serverAddress.Family == AddressFamilyKind.IPv4 && clientAddress.Family == AddressFamilyKind.IPv4)
        {
            const int required = EthernetHeaderLength + IPv4HeaderLength + TcpHeaderLength;
            if (destination.Length < required) return false;
            BuildIPv4(originalSynFrame, serverAddress, serverPort, clientAddress, clientPort, serverSequenceNext, clientSequenceNext, tcpFlags, destination);
            written = required;
            return true;
        }
        if (etherType == 0x86dd && serverAddress.Family == AddressFamilyKind.IPv6 && clientAddress.Family == AddressFamilyKind.IPv6)
        {
            const int required = EthernetHeaderLength + IPv6HeaderLength + TcpHeaderLength;
            if (destination.Length < required) return false;
            BuildIPv6(originalSynFrame, serverAddress, serverPort, clientAddress, clientPort, serverSequenceNext, clientSequenceNext, tcpFlags, destination);
            written = required;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the abort for a SYN that was rejected before any redirect state existed (the
    /// capacity gate): source = the server tuple the client dialed, destination = the client,
    /// <c>seq = 0</c>, <c>ack = client-ISN + 1</c>, flags RST|ACK. A client in SYN_SENT accepts
    /// this reset — its ACK acknowledges the client's SYN — and fails the connect immediately
    /// with ECONNREFUSED instead of retransmitting for the full OS timeout. The client ISN is
    /// read from <paramref name="synFrame"/> itself; writes the abort frame into
    /// <paramref name="destination"/> (at least <see cref="MaxResetFrameLength"/> bytes) and
    /// reports the exact frame length. Returns false without writing when the frame is not a
    /// parseable IPv4/IPv6 TCP segment, the address families do not match it, or
    /// <paramref name="destination"/> is too small.
    /// </summary>
    public static bool TryBuildResetFromSyn(
        ReadOnlySpan<byte> synFrame,
        IPAddressValue serverAddress,
        ushort serverPort,
        IPAddressValue clientAddress,
        ushort clientPort,
        Span<byte> destination,
        out int written)
    {
        written = 0;
        if (!IPTcpUdpPacket.TryParse(synFrame, out var view) || view.Transport != PacketTransport.Tcp) return false;
        var sequenceOffset = EthernetHeaderLength + view.IPHeaderLength + 4;
        if (synFrame.Length < sequenceOffset + 4) return false;
        var clientInitialSeq = BinaryPrimitives.ReadUInt32BigEndian(synFrame.Slice(sequenceOffset, 4));
        return TryBuildReset(synFrame, serverAddress, serverPort, clientAddress, clientPort, serverSequenceNext: 0, clientSequenceNext: clientInitialSeq + 1, destination, out written);
    }

    private static void BuildIPv4(ReadOnlySpan<byte> synTemplate, IPAddressValue serverAddress, ushort serverPort, IPAddressValue clientAddress, ushort clientPort, uint serverSequenceNext, uint clientSequenceNext, byte tcpFlags, Span<byte> frame)
    {
        WriteEthernetHeader(frame, synTemplate, 0x0800);

        var ip = frame.Slice(EthernetHeaderLength, IPv4HeaderLength);
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(2, 2), IPv4HeaderLength + TcpHeaderLength);
        BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(6, 2), 0x4000);
        ip[8] = 128;
        ip[9] = 6;
        serverAddress.TryWrite(ip.Slice(12, 4), out _);
        clientAddress.TryWrite(ip.Slice(16, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(10, 2), PacketChecksums.InternetChecksum(ip));

        var tcp = frame.Slice(EthernetHeaderLength + IPv4HeaderLength, TcpHeaderLength);
        WriteTcpHeader(tcp, serverPort, clientPort, serverSequenceNext, clientSequenceNext, tcpFlags);
        PacketChecksums.WriteTcpChecksum(frame, EthernetHeaderLength + IPv4HeaderLength, TcpHeaderLength, ip.Slice(12, 4), ip.Slice(16, 4), isIPv6: false);
    }

    private static void BuildIPv6(ReadOnlySpan<byte> synTemplate, IPAddressValue serverAddress, ushort serverPort, IPAddressValue clientAddress, ushort clientPort, uint serverSequenceNext, uint clientSequenceNext, byte tcpFlags, Span<byte> frame)
    {
        WriteEthernetHeader(frame, synTemplate, 0x86dd);

        var ip = frame.Slice(EthernetHeaderLength, IPv6HeaderLength);
        ip[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(4, 2), TcpHeaderLength);
        ip[6] = 6;
        ip[7] = 64;
        serverAddress.TryWrite(ip.Slice(8, 16), out _);
        clientAddress.TryWrite(ip.Slice(24, 16), out _);

        var tcp = frame.Slice(EthernetHeaderLength + IPv6HeaderLength, TcpHeaderLength);
        WriteTcpHeader(tcp, serverPort, clientPort, serverSequenceNext, clientSequenceNext, tcpFlags);
        PacketChecksums.WriteTcpChecksum(frame, EthernetHeaderLength + IPv6HeaderLength, TcpHeaderLength, ip.Slice(8, 16), ip.Slice(24, 16), isIPv6: true);
    }

    private static void WriteEthernetHeader(Span<byte> frame, ReadOnlySpan<byte> synTemplate, ushort etherType)
    {
        // Mirror the recorded client SYN with the addresses swapped: the reset travels back toward
        // the side the SYN arrived from.
        synTemplate.Slice(6, 6).CopyTo(frame);
        synTemplate[..6].CopyTo(frame.Slice(6, 6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(12, 2), etherType);
    }

    private static void WriteTcpHeader(Span<byte> tcp, ushort serverPort, ushort clientPort, uint serverSequenceNext, uint clientSequenceNext, byte tcpFlags)
    {
        BinaryPrimitives.WriteUInt16BigEndian(tcp[..2], serverPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(2, 2), clientPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.Slice(4, 4), serverSequenceNext);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.Slice(8, 4), clientSequenceNext);
        tcp[12] = 5 << 4;
        tcp[TcpFlagsOffset] = tcpFlags;
    }
}
