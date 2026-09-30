using System.Runtime.InteropServices;

namespace WinForward.Protocols;

/// <summary>
/// The address-free layout a successful <see cref="IPTcpUdpPacket.TryParse"/> proved: the transport,
/// its flags byte, the address family and the three header lengths. It is derived once from a
/// <see cref="PacketView"/> and carried on the packet, so the SYN test, both sequence observations
/// and the checksum rewriter consume the parse's proofs instead of re-walking the frame. The
/// addresses are deliberately absent: they already travel with the flow key, packed, and carrying
/// the 80-byte view would grow a struct the dispatcher copies per packet.
/// <para>
/// <see cref="Family"/> is not optional. The endpoint rewriter writes IPv4 or IPv6 address fields at
/// different offsets, and the addresses it writes come from an association, not from the frame — so
/// without the family a layout-driven rewrite could write 16 bytes into an IPv4 frame and report
/// success. The family byte fits the record's existing padding, so the layout stays 16 bytes.
/// </para>
/// <para>
/// Only <see cref="From"/> produces a valid layout, and it stamps the value it returns. The stamp is
/// load-bearing rather than decorative: <see cref="PacketTransport.Tcp"/> is <c>0</c>, so an all-zero
/// layout would otherwise be indistinguishable from a parsed TCP layout whose transport header sits
/// where the IP header starts and whose family is IPv4 — and a packet that never parsed carries
/// exactly that value. Consumers therefore gate on <see cref="IsTcp"/> (or <see cref="IsValid"/>),
/// never on the transport byte alone, and <c>default(PacketLayout)</c> is refused rather than applied.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct PacketLayout
{
    /// <summary>The marker <see cref="From"/> writes; a defaulted layout carries zero instead.</summary>
    private const byte ParsedStamp = 0x5a;

    private readonly byte _stamp;

    /// <summary>
    /// Deliberately private: a layout asserts what a parse proved, so the only value that may claim
    /// it is one <see cref="From"/> produced. The get-only properties are the parse's proofs and none
    /// has a setter, so no hand-built value can overwrite one.
    /// </summary>
    private PacketLayout(byte transport, byte tcpFlags, byte family, int ipHeaderLength, int transportHeaderLength, int transportLength)
    {
        Transport = transport;
        TcpFlags = tcpFlags;
        Family = family;
        _stamp = ParsedStamp;
        IPHeaderLength = ipHeaderLength;
        TransportHeaderLength = transportHeaderLength;
        TransportLength = transportLength;
    }

    /// <summary>Derives the layout from a successful parse; the only producer of a valid layout.</summary>
    public static PacketLayout From(in PacketView view) => new(
        (byte)view.Transport,
        view.TcpFlags,
        (byte)view.SourceAddress.Family,
        view.IPHeaderLength,
        view.TransportHeaderLength,
        view.TransportLength);

    /// <summary>
    /// Whether a successful parse produced this layout. False for <see langword="default"/> — the
    /// layout of a packet that never parsed — whose zeroed fields are not geometry and must never be
    /// read as any.
    /// </summary>
    public bool IsValid => _stamp == ParsedStamp;

    /// <summary>
    /// The gate every TCP consumer uses: this is a parsed layout and its transport is TCP. A
    /// defaulted layout fails it, which is what keeps <see cref="TransportOffset"/> and
    /// <see cref="Family"/> from being applied to a frame the parse never saw.
    /// </summary>
    public bool IsTcp => _stamp == ParsedStamp && Transport == (byte)PacketTransport.Tcp;

    // ReSharper disable once MemberCanBePrivate.Global // AGENTS.md false-positive class: this is a public cross-assembly API member (WinForward.Runtime and the benchmarks consume PacketLayout's geometry), so the parse-proof surface is not narrowed to today's call sites.
    public byte Transport { get; }

    public byte TcpFlags { get; }

    public byte Family { get; }

    // ReSharper disable once MemberCanBePrivate.Global // AGENTS.md false-positive class: this is a public cross-assembly API member (WinForward.Runtime and the benchmarks consume PacketLayout's geometry), so the parse-proof surface is not narrowed to today's call sites.
    public int IPHeaderLength { get; }

    public int TransportHeaderLength { get; }

    public int TransportLength { get; }

    /// <summary>The transport header's offset in the Ethernet II frame; meaningful only for a valid layout.</summary>
    public int TransportOffset => 14 + IPHeaderLength;

    /// <summary>The first byte past the IP datagram, Ethernet-relative; meaningful only for a valid layout.</summary>
    public int TransportEnd => TransportOffset + TransportLength;
}
