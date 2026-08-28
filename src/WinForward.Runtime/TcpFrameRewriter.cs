using System.Runtime.InteropServices;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime;

/// <summary>
/// Static pure-function cluster for TCP frame rewriting: classifying SYN packets, obtaining a
/// writable frame view, rewriting the forward-leg (SYN or mid-flow data) endpoints toward the
/// redirect listener, and swapping Ethernet MACs for the host-shape local-redirect pattern.
/// These functions have zero instance state and no I/O, making them OS-independent and unit-testable.
/// </summary>
internal static class TcpFrameRewriter
{
    /// <summary>
    /// Rewrites the forward leg (SYN or mid-flow data) toward the redirect listener. The forwarded
    /// DNAT shape preserves the client's source tuple and moves only the destination to the
    /// adapter-local listener address; the host shape follows the WinpkFilter local-redirect pattern
    /// (swap IPs, rewrite dst port, swap MACs) — see <see cref="SwapEthernetMacs"/>.
    /// </summary>
    public static bool TryRewriteForwardLeg(Span<byte> frame, Endpoint originalClient, Endpoint originalServer, TcpRedirectAssociation association, ushort listenerPort)
    {
        if (association.ForwardLocalAddress is { } forwardLocalAddress)
        {
            return PacketChecksums.TryRewriteTcpEndpoints(frame, originalClient.Address, originalClient.Port, forwardLocalAddress, listenerPort);
        }
        if (!PacketChecksums.TryRewriteTcpEndpoints(frame, originalServer.Address, originalClient.Port, originalClient.Address, listenerPort)) return false;
        SwapEthernetMacs(frame);
        return true;
    }

    /// <summary>
    /// Obtains a writable view of a captured frame for in-place rewriting. The capture path
    /// always wraps a pooled <see cref="byte"/>[] in the lease, but a lease over non-array
    /// memory cannot be rewritten in place; callers fail closed rather than fall back to a copy.
    /// </summary>
    public static bool TryGetWritableFrame(ReadOnlyMemory<byte> frame, out Span<byte> writable)
    {
        writable = default;
        if (!MemoryMarshal.TryGetArray(frame, out var segment)) return false;
        writable = segment.Array.AsSpan(segment.Offset, segment.Count);
        return true;
    }

    /// <summary>
    /// Swaps the Ethernet source and destination MAC addresses of a frame. The official WinpkFilter
    /// local-redirect pattern swaps MACs alongside IPs and ports so the redirected frame is accepted
    /// by the local stack as if it arrived from the original server.
    /// </summary>
    public static void SwapEthernetMacs(Span<byte> frame)
    {
        if (frame.Length < 12) return;
        Span<byte> destination = frame.Slice(0, 6);
        Span<byte> source = frame.Slice(6, 6);
        var temp = new byte[6];
        destination.CopyTo(temp);
        source.CopyTo(destination);
        temp.CopyTo(source);
    }

    /// <summary>
    /// Detects a TCP SYN (SYN set, ACK clear) from the raw Ethernet frame. The flags byte is at
    /// the TCP header offset + 13; SYN = 0x02, ACK = 0x10. Returns <see cref="TcpSynKind.Empty"/>
    /// for a bare SYN, <see cref="TcpSynKind.WithPayload"/> for a SYN with data (which is blocked),
    /// and <see cref="TcpSynKind.None"/> for a non-SYN or unparseable frame.
    /// </summary>
    public static TcpSynKind ClassifyTcpSyn(ReadOnlySpan<byte> frame)
    {
        if (!IpTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return TcpSynKind.None;
        var tcpFlagsOffset = 14 + view.IpHeaderLength + 13;
        if (frame.Length <= tcpFlagsOffset) return TcpSynKind.None;
        var flags = frame[tcpFlagsOffset];
        const byte Syn = 0x02;
        const byte Ack = 0x10;
        if ((flags & Syn) == 0 || (flags & Ack) != 0) return TcpSynKind.None;

        var etherType = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        var transportLength = etherType == 0x0800
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(16, 2)) - view.IpHeaderLength
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(18, 2)) - (view.IpHeaderLength - 40);
        return transportLength == view.TransportHeaderLength ? TcpSynKind.Empty : TcpSynKind.WithPayload;
    }
}

/// <summary>Classification of a captured TCP frame's SYN status.</summary>
internal enum TcpSynKind
{
    None,
    Empty,
    WithPayload,
}
