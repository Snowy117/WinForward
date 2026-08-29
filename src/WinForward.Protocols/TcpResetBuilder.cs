using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using WinForward.Core;

namespace WinForward.Protocols;

/// <summary>
/// Builds a standalone TCP RST|ACK frame that aborts one connection on behalf of its original
/// server endpoint. Used to surface a failed upstream relay to the client as an immediate,
/// protocol-correct reset instead of a silent hang. The Ethernet header is mirrored from the
/// recorded client SYN (addresses swapped) so the frame is deliverable on the same L2 segment;
/// everything at L3/L4 is constructed fresh with its own checksums, so the result never depends
/// on parser or rewrite state.
/// </summary>
public static class TcpResetBuilder
{
    private const int EthernetHeaderLength = 14;
    private const int Ipv4HeaderLength = 20;
    private const int Ipv6HeaderLength = 40;
    private const int TcpHeaderLength = 20;
    private const byte TcpFlagsOffset = 13;
    private const byte TcpResetAck = 0x14;

    /// <summary>
    /// Builds the reset frame from framework addresses; convenience wrapper for callers on cold
    /// paths (relay failure handling, tests).
    /// </summary>
    public static byte[]? BuildReset(
        ReadOnlySpan<byte> originalSynFrame,
        IPAddress serverAddress,
        ushort serverPort,
        IPAddress clientAddress,
        ushort clientPort,
        uint serverSequenceNext,
        uint clientSequenceNext)
        => BuildReset(originalSynFrame, IPAddressValue.From(serverAddress), serverPort, IPAddressValue.From(clientAddress), clientPort, serverSequenceNext, clientSequenceNext);

    /// <summary>
    /// Builds the reset frame from fixed-size address values; the hot-path form.
    /// <paramref name="serverSequenceNext"/> must be the sequence the client expects next from
    /// the server (its in-window value, typically server-ISN + 1) and <paramref name="clientSequenceNext"/>
    /// the acknowledged client sequence (client-ISN + 1). Returns null when the template is not a
    /// usable Ethernet IPv4/IPv6 frame or the address families do not match it.
    /// </summary>
    public static byte[]? BuildReset(
        ReadOnlySpan<byte> originalSynFrame,
        IPAddressValue serverAddress,
        ushort serverPort,
        IPAddressValue clientAddress,
        ushort clientPort,
        uint serverSequenceNext,
        uint clientSequenceNext)
    {
        if (originalSynFrame.Length < EthernetHeaderLength) return null;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(originalSynFrame.Slice(12, 2));
        return etherType switch
        {
            0x0800 when serverAddress.Family == AddressFamilyKind.IPv4 && clientAddress.Family == AddressFamilyKind.IPv4
                => BuildIpv4(originalSynFrame, serverAddress, serverPort, clientAddress, clientPort, serverSequenceNext, clientSequenceNext),
            0x86dd when serverAddress.Family == AddressFamilyKind.IPv6 && clientAddress.Family == AddressFamilyKind.IPv6
                => BuildIpv6(originalSynFrame, serverAddress, serverPort, clientAddress, clientPort, serverSequenceNext, clientSequenceNext),
            _ => null
        };
    }

    /// <summary>
    /// Builds the abort for a SYN that was rejected before any redirect state existed (the
    /// capacity gate): source = the server tuple the client dialed, destination = the client,
    /// <c>seq = 0</c>, <c>ack = client-ISN + 1</c>, flags RST|ACK. A client in SYN_SENT accepts
    /// this reset — its ACK acknowledges the client's SYN — and fails the connect immediately
    /// with ECONNREFUSED instead of retransmitting for the full OS timeout. The client ISN is
    /// read from <paramref name="synFrame"/> itself; returns null when the frame is not a
    /// parseable IPv4/IPv6 TCP segment or the address families do not match it.
    /// </summary>
    public static byte[]? BuildResetFromSyn(
        ReadOnlySpan<byte> synFrame,
        IPAddressValue serverAddress,
        ushort serverPort,
        IPAddressValue clientAddress,
        ushort clientPort)
    {
        if (!IPTcpUdpPacket.TryParse(synFrame, out var view) || view.Transport != PacketTransport.Tcp) return null;
        var sequenceOffset = EthernetHeaderLength + view.IPHeaderLength + 4;
        if (synFrame.Length < sequenceOffset + 4) return null;
        var clientInitialSeq = BinaryPrimitives.ReadUInt32BigEndian(synFrame.Slice(sequenceOffset, 4));
        return BuildReset(synFrame, serverAddress, serverPort, clientAddress, clientPort, serverSequenceNext: 0, clientSequenceNext: clientInitialSeq + 1);
    }

    private static byte[] BuildIpv4(ReadOnlySpan<byte> synTemplate, IPAddressValue serverAddress, ushort serverPort, IPAddressValue clientAddress, ushort clientPort, uint serverSequenceNext, uint clientSequenceNext)
    {
        var frame = new byte[EthernetHeaderLength + Ipv4HeaderLength + TcpHeaderLength];
        WriteEthernetHeader(frame, synTemplate, 0x0800);

        var ip = frame.AsSpan(EthernetHeaderLength, Ipv4HeaderLength);
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(2, 2), Ipv4HeaderLength + TcpHeaderLength);
        BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(6, 2), 0x4000);
        ip[8] = 128;
        ip[9] = 6;
        serverAddress.TryWrite(ip.Slice(12, 4), out _);
        clientAddress.TryWrite(ip.Slice(16, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(10, 2), PacketChecksums.InternetChecksum(ip));

        var tcp = frame.AsSpan(EthernetHeaderLength + Ipv4HeaderLength, TcpHeaderLength);
        WriteTcpHeader(tcp, serverPort, clientPort, serverSequenceNext, clientSequenceNext);
        PacketChecksums.WriteTcpChecksum(frame, EthernetHeaderLength + Ipv4HeaderLength, TcpHeaderLength, ip.Slice(12, 4), ip.Slice(16, 4), isIpv6: false);
        return frame;
    }

    private static byte[] BuildIpv6(ReadOnlySpan<byte> synTemplate, IPAddressValue serverAddress, ushort serverPort, IPAddressValue clientAddress, ushort clientPort, uint serverSequenceNext, uint clientSequenceNext)
    {
        var frame = new byte[EthernetHeaderLength + Ipv6HeaderLength + TcpHeaderLength];
        WriteEthernetHeader(frame, synTemplate, 0x86dd);

        var ip = frame.AsSpan(EthernetHeaderLength, Ipv6HeaderLength);
        ip[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(4, 2), TcpHeaderLength);
        ip[6] = 6;
        ip[7] = 64;
        serverAddress.TryWrite(ip.Slice(8, 16), out _);
        clientAddress.TryWrite(ip.Slice(24, 16), out _);

        var tcp = frame.AsSpan(EthernetHeaderLength + Ipv6HeaderLength, TcpHeaderLength);
        WriteTcpHeader(tcp, serverPort, clientPort, serverSequenceNext, clientSequenceNext);
        PacketChecksums.WriteTcpChecksum(frame, EthernetHeaderLength + Ipv6HeaderLength, TcpHeaderLength, ip.Slice(8, 16), ip.Slice(24, 16), isIpv6: true);
        return frame;
    }

    private static void WriteEthernetHeader(Span<byte> frame, ReadOnlySpan<byte> synTemplate, ushort etherType)
    {
        // Mirror the recorded client SYN with the addresses swapped: the reset travels back toward
        // the side the SYN arrived from.
        synTemplate.Slice(6, 6).CopyTo(frame);
        synTemplate.Slice(0, 6).CopyTo(frame.Slice(6, 6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(12, 2), etherType);
    }

    private static void WriteTcpHeader(Span<byte> tcp, ushort serverPort, ushort clientPort, uint serverSequenceNext, uint clientSequenceNext)
    {
        BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(0, 2), serverPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(2, 2), clientPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.Slice(4, 4), serverSequenceNext);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.Slice(8, 4), clientSequenceNext);
        tcp[12] = 5 << 4;
        tcp[TcpFlagsOffset] = TcpResetAck;
    }
}
