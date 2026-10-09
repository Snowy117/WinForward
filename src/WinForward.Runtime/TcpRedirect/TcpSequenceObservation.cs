using System.Buffers.Binary;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// Static pure-function cluster for observing TCP sequence numbers on redirect flows: recording
/// the client ISN and a bounded SYN-frame copy, recording the server SYN-ACK ISN, and tracking
/// forward/reverse sequence advancement. These functions have zero instance state and no I/O.
/// <para>
/// The layout-taking entries consume the parse's proofs (a parsed TCP layout, its flags byte,
/// header offsets and the IP-derived transport length) and keep only the frame bound a 4-byte
/// sequence read demands:
/// <c>frame.Length &gt;= layout.TransportOffset + 8</c> covers the whole read, so a short frame takes
/// the reject path instead of throwing out of <c>Slice</c>. The span-taking entries remain the
/// independent oracle and sit next to their layout twin.
/// </para>
/// </summary>
internal static class TcpSequenceObservation
{
    /// <summary>
    /// Records the client ISN and a bounded copy of the original SYN frame on the association.
    /// Together with the server ISN captured by <see cref="RecordServerSynAck(ReadOnlySpan{byte}, in PacketLayout, TcpRedirectAssociation)"/>
    /// this is what a relay setup failure needs to abort the client-visible connection with an
    /// in-window RST.
    /// </summary>
    public static void RecordClientSyn(ReadOnlySpan<byte> frame, in PacketLayout layout, TcpRedirectAssociation association, NativeBufferPool synCopyPool)
    {
        if (!layout.IsTcp) return;
        var sequenceOffset = layout.TransportOffset + 4;
        if (frame.Length < sequenceOffset + 4) return;
        association.ClientInitialSeq = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(sequenceOffset, 4));
        var templateLength = Math.Min(frame.Length, 128);
        var template = synCopyPool.Rent();
        frame[..templateLength].CopyTo(template.Span);
        association.ReleaseOriginalSynTemplate();
        association.SetOriginalSynTemplate(template, templateLength);
    }

    /// <summary>
    /// Records the client ISN and a bounded copy of the original SYN frame on the association.
    /// Together with the server ISN captured by <see cref="RecordServerSynAck(ReadOnlySpan{byte}, TcpRedirectAssociation)"/>
    /// this is what a relay setup failure needs to abort the client-visible connection with an
    /// in-window RST.
    /// </summary>
    public static void RecordClientSyn(ReadOnlySpan<byte> frame, TcpRedirectAssociation association, NativeBufferPool synCopyPool)
    {
        if (!IPTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return;
        var sequenceOffset = 14 + view.IPHeaderLength + 4;
        if (frame.Length < sequenceOffset + 4) return;
        association.ClientInitialSeq = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(sequenceOffset, 4));
        var templateLength = Math.Min(frame.Length, 128);
        var template = synCopyPool.Rent();
        frame[..templateLength].CopyTo(template.Span);
        association.ReleaseOriginalSynTemplate();
        association.SetOriginalSynTemplate(template, templateLength);
    }

    /// <summary>
    /// Captures the listener-side ISN when the reverse leg's SYN-ACK passes through, so a later
    /// relay setup failure can craft a reset the client's stack accepts as in-window.
    /// </summary>
    public static void RecordServerSynAck(ReadOnlySpan<byte> frame, in PacketLayout layout, TcpRedirectAssociation association)
    {
        if (!layout.IsTcp) return;
        const byte synAck = 0x12;
        if ((layout.TcpFlags & synAck) != synAck) return;
        var sequenceOffset = layout.TransportOffset + 4;
        if (frame.Length < sequenceOffset + 4) return;
        association.ServerInitialSeq = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(sequenceOffset, 4));
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
        association.ServerInitialSeq = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(sequenceOffset, 4));
    }

    /// <summary>
    /// Reads a TCP frame's sequence-space advancement — seq plus payload length, with SYN and FIN
    /// each consuming one sequence number — from the pre-rewrite frame. The transport length is the
    /// parse's IP-derived value, never the frame length, so Ethernet padding is not counted as
    /// payload. Returns false for non-TCP or short frames, which simply leaves the trackers
    /// untouched (the reset then degrades to the ISN-based values).
    /// </summary>
    private static bool TryReadTcpSequenceAdvance(ReadOnlySpan<byte> frame, in PacketLayout layout, out uint sequenceNext)
    {
        sequenceNext = 0;
        if (!layout.IsTcp) return false;
        if (layout.TransportLength < layout.TransportHeaderLength) return false;
        var tcpOffset = layout.TransportOffset;
        if (frame.Length < tcpOffset + 8) return false;
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(tcpOffset + 4, 4));
        var advance = (uint)(layout.TransportLength - layout.TransportHeaderLength);
        const byte syn = 0x02;
        const byte fin = 0x01;
        if ((layout.TcpFlags & syn) != 0) advance++;
        if ((layout.TcpFlags & fin) != 0) advance++;
        sequenceNext = sequence + advance;
        return true;
    }

    /// <summary>
    /// Reads a TCP frame's sequence-space advancement — seq plus payload length, with SYN and FIN
    /// each consuming one sequence number — from the pre-rewrite frame. The transport length comes
    /// from the IP header rather than the frame length so Ethernet padding is not counted as
    /// payload. Returns false for non-TCP or unparseable frames, which simply leaves the trackers
    /// untouched (the reset then degrades to the ISN-based values).
    /// </summary>
    private static bool TryReadTcpSequenceAdvance(ReadOnlySpan<byte> frame, out uint sequenceNext)
    {
        sequenceNext = 0;
        if (!IPTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return false;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        var transportLength = etherType == 0x0800
            ? BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(16, 2)) - view.IPHeaderLength
            : BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(18, 2)) - (view.IPHeaderLength - 40);
        if (transportLength < view.TransportHeaderLength) return false;
        var tcpOffset = 14 + view.IPHeaderLength;
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(tcpOffset + 4, 4));
        var flags = frame[tcpOffset + 13];
        var advance = (uint)(transportLength - view.TransportHeaderLength);
        const byte syn = 0x02;
        const byte fin = 0x01;
        if ((flags & syn) != 0) advance++;
        if ((flags & fin) != 0) advance++;
        sequenceNext = sequence + advance;
        return true;
    }

    /// <summary>
    /// Advances the client-side sequence tracker on the forward leg. Must run on the original
    /// (pre-rewrite) frame — the same read-then-write invariant as <see cref="RecordClientSyn(ReadOnlySpan{byte}, in PacketLayout, TcpRedirectAssociation, NativeBufferPool)"/>.
    /// </summary>
    public static void TrackClientSequence(ReadOnlySpan<byte> frame, in PacketLayout layout, TcpRedirectAssociation association)
    {
        if (TryReadTcpSequenceAdvance(frame, layout, out var next)) association.ObserveClientSequence(next);
    }

    /// <summary>
    /// Advances the client-side sequence tracker on the forward leg. Must run on the original
    /// (pre-rewrite) frame — the same read-then-write invariant as <see cref="RecordClientSyn(ReadOnlySpan{byte}, TcpRedirectAssociation, NativeBufferPool)"/>.
    /// </summary>
    public static void TrackClientSequence(ReadOnlySpan<byte> frame, TcpRedirectAssociation association)
    {
        if (TryReadTcpSequenceAdvance(frame, out var next)) association.ObserveClientSequence(next);
    }

    /// <summary>
    /// Advances the client-acknowledgement tracker on the forward leg by the frame's ACK field
    /// (transport offset +8). Must run on the original (pre-rewrite) frame, the same read-then-write
    /// invariant as <see cref="TrackClientSequence(ReadOnlySpan{byte}, in PacketLayout, TcpRedirectAssociation)"/>.
    /// A frame without the ACK control bit leaves the tracker untouched: its acknowledgement field
    /// carries no value (a SYN's is zero), and a tracked zero would look like it covers a drain
    /// target in the upper half of the sequence space — an arm that exits before the close was
    /// acknowledged.
    /// </summary>
    public static void TrackClientAck(ReadOnlySpan<byte> frame, in PacketLayout layout, TcpRedirectAssociation association)
    {
        if (TryReadTcpAcknowledgement(frame, layout, out var acknowledgement)) association.ObserveClientAck(acknowledgement);
    }

    /// <summary>
    /// The span-taking twin of
    /// <see cref="TrackClientAck(ReadOnlySpan{byte}, in PacketLayout, TcpRedirectAssociation)"/> and
    /// its independent oracle: it parses the frame itself, so an accepted packet's TCP header — the
    /// acknowledgement field included — is proven present and needs no separate bound check. Must run
    /// on the original (pre-rewrite) frame, the same read-then-write invariant as the layout entry.
    /// </summary>
    public static void TrackClientAck(ReadOnlySpan<byte> frame, TcpRedirectAssociation association)
    {
        if (TryReadTcpAcknowledgement(frame, out var acknowledgement)) association.ObserveClientAck(acknowledgement);
    }

    private static bool TryReadTcpAcknowledgement(ReadOnlySpan<byte> frame, in PacketLayout layout, out uint acknowledgement)
    {
        acknowledgement = 0;
        if (!layout.IsTcp) return false;
        const byte ack = 0x10;
        if ((layout.TcpFlags & ack) == 0) return false;
        var acknowledgementOffset = layout.TransportOffset + 8;
        if (frame.Length < acknowledgementOffset + 4) return false;
        acknowledgement = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(acknowledgementOffset, 4));
        return true;
    }

    private static bool TryReadTcpAcknowledgement(ReadOnlySpan<byte> frame, out uint acknowledgement)
    {
        acknowledgement = 0;
        if (!IPTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return false;
        const byte ack = 0x10;
        var tcpOffset = 14 + view.IPHeaderLength;
        if ((frame[tcpOffset + 13] & ack) == 0) return false;
        acknowledgement = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(tcpOffset + 8, 4));
        return true;
    }

    /// <summary>Advances the server-side sequence tracker on the reverse leg (pre-rewrite frame).</summary>
    public static void TrackServerSequence(ReadOnlySpan<byte> frame, in PacketLayout layout, TcpRedirectAssociation association)
    {
        if (TryReadTcpSequenceAdvance(frame, layout, out var next)) association.ObserveServerSequence(next);
    }

    /// <summary>Advances the server-side sequence tracker on the reverse leg (pre-rewrite frame).</summary>
    public static void TrackServerSequence(ReadOnlySpan<byte> frame, TcpRedirectAssociation association)
    {
        if (TryReadTcpSequenceAdvance(frame, out var next)) association.ObserveServerSequence(next);
    }
}
