using WinForward.Protocols;

namespace WinForward.Runtime;

/// <summary>
/// Static pure-function cluster for observing TCP sequence numbers on redirect flows: recording
/// the client ISN and a bounded SYN-frame copy, recording the server SYN-ACK ISN, and tracking
/// forward/reverse sequence advancement. These functions have zero instance state and no I/O.
/// </summary>
internal static class TcpSequenceObservation
{
    /// <summary>
    /// Records the client ISN and a bounded copy of the original SYN frame on the association.
    /// Together with the server ISN captured by <see cref="RecordServerSynAck"/> this is everything
    /// a relay setup failure needs to abort the client-visible connection with an in-window RST.
    /// </summary>
    public static void RecordClientSyn(ReadOnlySpan<byte> frame, TcpRedirectAssociation association)
    {
        if (!IPTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return;
        var sequenceOffset = 14 + view.IPHeaderLength + 4;
        if (frame.Length < sequenceOffset + 4) return;
        association.ClientInitialSeq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(sequenceOffset, 4));
        association.OriginalSynFrameCopy = frame.Slice(0, Math.Min(frame.Length, 128)).ToArray();
    }

    /// <summary>
    /// Captures the listener-side ISN when the reverse leg's SYN-ACK passes through, so a later
    /// relay setup failure can craft a reset the client's stack accepts as in-window.
    /// </summary>
    public static void RecordServerSynAck(ReadOnlySpan<byte> frame, TcpRedirectAssociation association)
    {
        if (!IPTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return;
        var flagsOffset = 14 + view.IPHeaderLength + 13;
        if (frame.Length <= flagsOffset || (frame[flagsOffset] & 0x12) != 0x12) return;
        var sequenceOffset = 14 + view.IPHeaderLength + 4;
        if (frame.Length < sequenceOffset + 4) return;
        association.ServerInitialSeq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(sequenceOffset, 4));
    }

    /// <summary>
    /// Reads a TCP frame's sequence-space advancement — seq plus payload length, with SYN and FIN
    /// each consuming one sequence number — from the pre-rewrite frame. The transport length comes
    /// from the IP header rather than the frame length so Ethernet padding is not counted as
    /// payload. Returns false for non-TCP or unparseable frames, which simply leaves the trackers
    /// untouched (the reset then degrades to the ISN-based values).
    /// </summary>
    public static bool TryReadTcpSequenceAdvance(ReadOnlySpan<byte> frame, out uint sequenceNext)
    {
        sequenceNext = 0;
        if (!IPTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return false;
        var etherType = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        var transportLength = etherType == 0x0800
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(16, 2)) - view.IPHeaderLength
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(18, 2)) - (view.IPHeaderLength - 40);
        if (transportLength < view.TransportHeaderLength) return false;
        var tcpOffset = 14 + view.IPHeaderLength;
        var sequence = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(tcpOffset + 4, 4));
        var flags = frame[tcpOffset + 13];
        var advance = (uint)(transportLength - view.TransportHeaderLength);
        const byte Syn = 0x02;
        const byte Fin = 0x01;
        if ((flags & Syn) != 0) advance++;
        if ((flags & Fin) != 0) advance++;
        sequenceNext = sequence + advance;
        return true;
    }

    /// <summary>
    /// Advances the client-side sequence tracker on the forward leg. Must run on the original
    /// (pre-rewrite) frame — the same read-then-write invariant as <see cref="RecordClientSyn"/>.
    /// </summary>
    public static void TrackClientSequence(ReadOnlySpan<byte> frame, TcpRedirectAssociation association)
    {
        if (TryReadTcpSequenceAdvance(frame, out var next)) association.ObserveClientSequence(next);
    }

    /// <summary>Advances the server-side sequence tracker on the reverse leg (pre-rewrite frame).</summary>
    public static void TrackServerSequence(ReadOnlySpan<byte> frame, TcpRedirectAssociation association)
    {
        if (TryReadTcpSequenceAdvance(frame, out var next)) association.ObserveServerSequence(next);
    }
}
