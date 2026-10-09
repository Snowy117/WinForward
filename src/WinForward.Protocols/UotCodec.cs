using System.Buffers.Binary;
using WinForward.Core;

namespace WinForward.Protocols;

/// <summary>
/// The sing-box "UDP over TCP" (UoT) v2 wire codec, connect mode only: the request header a client
/// writes after its SOCKS5 <c>CONNECT</c> to <see cref="MagicAddress"/>, and the <c>u16be</c>
/// length prefix that frames each datagram on the stream. The request header's destination is an
/// ordinary <em>SOCKS</em> address — the intercepting server reads it with its SOCKS address
/// serializer — so its address types are <see cref="Socks5Messages.AddressTypeIPv4"/> and
/// <see cref="Socks5Messages.AddressTypeIPv6"/>, shared with the SOCKS5 encoder instead of restated
/// here.
/// <para>
/// The protocol's <em>other</em> address encoding — the per-datagram <c>0x00</c> IPv4 /
/// <c>0x01</c> IPv6 / <c>0x02</c> domain header of protocol version 1's stream format, which
/// version 2 keeps only for <c>isConnect = 0</c> flows — belongs to a format this codec deliberately
/// does not implement and therefore does not name. A caller that needs the non-connect stream format
/// must define that format's address types itself rather than borrow anything from this type.
/// </para>
/// </summary>
public static class UotCodec
{
    /// <summary>
    /// The UoT protocol version this codec speaks: v2, the default since sing-box v1.2-beta9. The
    /// version is not an on-wire field of the header; it is carried by
    /// <see cref="MagicAddress"/>'s <c>v2</c> segment.
    /// </summary>
    public const int Version = 2;

    /// <summary>
    /// The magic address a client sends as the SOCKS5 <c>CONNECT</c> destination to request UoT:
    /// the intercepting server routes such a connection to its UoT router instead of dialing the
    /// name. In connect mode a <c>CONNECT</c> to this address carries the one destination the
    /// stream's datagrams are bound to.
    /// </summary>
    public const string MagicAddress = "sp.v2.udp-over-tcp.arpa";

    /// <summary>
    /// The longest request header this codec writes: the IPv6 form, whose address field is 16 bytes.
    /// Callers that do not know the family size their scratch span from this constant and use
    /// <see cref="RequestHeaderLength"/> when the family is known.
    /// </summary>
    public const int MaximumRequestHeaderLength = 20;

    /// <summary>The byte length of the UoT request header for a destination of <paramref name="family"/>.</summary>
    public static int RequestHeaderLength(AddressFamilyKind family) => 2 + (family == AddressFamilyKind.IPv4 ? 4 : 16) + 2;

    /// <summary>
    /// Writes the UoT v2 connect-mode request header — <paramref name="isConnect"/>, the <c>ATYP</c>
    /// byte, the address, and the port as <c>u16be</c> — into <paramref name="destination"/> without
    /// allocating. Returns false when the destination is too small. The destination is an ordinary
    /// SOCKS address
    /// (<see cref="Socks5Messages.AddressTypeIPv4"/> / <see cref="Socks5Messages.AddressTypeIPv6"/>),
    /// and only the two IP forms are emitted: the product's destinations are captured IPs, and the
    /// domain form is only meaningful on the magic <c>CONNECT</c> that <see cref="Socks5Messages"/>
    /// writes.
    /// <para>
    /// <paramref name="isConnect"/> is the header's mode field and this codec implements connect mode
    /// only — the framing that follows (<c>u16be length | payload</c>) is connect mode's. A
    /// non-connect request would ask the server for protocol version 1's per-datagram stream format,
    /// whose address types this codec does not carry, so <see langword="false"/> is rejected instead
    /// of written.
    /// </para>
    /// </summary>
    public static bool TryWriteRequestHeader(bool isConnect, IPAddressValue destinationAddress, ushort destinationPort, Span<byte> destination, out int written)
    {
        if (!isConnect)
        {
            written = 0;
            return false;
        }

        var isIPv4 = destinationAddress.Family == AddressFamilyKind.IPv4;
        var addressLength = isIPv4 ? 4 : 16;
        var totalLength = 2 + addressLength + 2;
        if (destination.Length < totalLength)
        {
            written = 0;
            return false;
        }

        destination[0] = 1;
        destination[1] = isIPv4 ? Socks5Messages.AddressTypeIPv4 : Socks5Messages.AddressTypeIPv6;
        _ = destinationAddress.TryWrite(destination.Slice(2, addressLength), out _);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2 + addressLength, 2), destinationPort);
        written = totalLength;
        return true;
    }

    /// <summary>The byte length of the per-datagram stream frame prefix: <c>u16be length</c>.</summary>
    public const int FrameHeaderSize = 2;

    /// <summary>
    /// Writes the <c>u16be</c> frame prefix for a datagram of <paramref name="payloadLength"/>
    /// bytes into <paramref name="destination"/>; the caller guards the <see cref="ushort"/> ceiling
    /// before the length reaches this parameter, and a zero length is a legal empty datagram.
    /// Returns false when the destination is too small for the two prefix bytes.
    /// </summary>
    public static bool TryWriteFrameHeader(ushort payloadLength, Span<byte> destination, out int written)
    {
        if (destination.Length < FrameHeaderSize)
        {
            written = 0;
            return false;
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination, payloadLength);
        written = FrameHeaderSize;
        return true;
    }
}
