using System.Buffers.Binary;
using WinForward.Core;

namespace WinForward.Protocols;

/// <summary>
/// A decoded SOCKS5 UDP datagram. <see cref="DestinationAddress"/> is the raw value-type
/// representation (zero-allocation decode; scope propagated by the caller), and null marks a
/// domain-typed datagram.
/// </summary>
public readonly record struct Socks5UdpDatagram(IPAddressValue? DestinationAddress, string? DestinationDomain, ushort DestinationPort, ReadOnlyMemory<byte> Payload);

public static class Socks5UdpCodec
{
    /// <summary>
    /// Writes an address-typed SOCKS5 UDP datagram header plus payload into
    /// <paramref name="destination"/> without allocating; the relay send path uses this with a
    /// reusable buffer. Returns false when the destination is too small.
    /// </summary>
    public static bool TryEncode(IPAddressValue destinationAddress, ushort destinationPort, ReadOnlySpan<byte> payload, Span<byte> destination, out int written)
    {
        var isIpv4 = destinationAddress.Family == AddressFamilyKind.IPv4;
        var addressLength = isIpv4 ? 4 : 16;
        var totalLength = 6 + addressLength + payload.Length;
        if (destination.Length < totalLength)
        {
            written = 0;
            return false;
        }

        destination[0] = 0;
        destination[1] = 0;
        destination[2] = 0;
        destination[3] = isIpv4 ? (byte)1 : (byte)4;
        _ = destinationAddress.TryWrite(destination.Slice(4, addressLength), out _);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4 + addressLength, 2), destinationPort);
        payload.CopyTo(destination[(6 + addressLength)..]);
        written = totalLength;
        return true;
    }

    public static bool TryDecode(ReadOnlyMemory<byte> frame, out Socks5UdpDatagram datagram, long scopeId = 0)
    {
        datagram = default;
        var bytes = frame.Span;
        if (bytes.Length < 4 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 0 || bytes[3] is not (1 or 3 or 4)) return false;
        var offset = 4;
        IPAddressValue? address = null;
        string? domain = null;
        if (bytes[3] is 1 or 4)
        {
            var addressLength = bytes[3] == 1 ? 4 : 16;
            if (bytes.Length < offset + addressLength + 2 || !TryReadAddress(bytes.Slice(offset, addressLength), bytes[3], scopeId, out address)) return false;
            offset += addressLength;
        }
        else
        {
            if (bytes.Length < offset + 1) return false;
            var domainLength = bytes[offset++];
            if (domainLength == 0 || bytes.Length < offset + domainLength + 2) return false;
            // RFC 1928 domain names are ASCII; non-ASCII bytes decode as '?' rather than throwing.
            domain = System.Text.Encoding.ASCII.GetString(bytes.Slice(offset, domainLength));
            offset += domainLength;
        }
        if (bytes.Length < offset + 2) return false;
        datagram = new Socks5UdpDatagram(address, domain, BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2)), frame[(offset + 2)..]);
        return true;
    }

    /// <summary>
    /// Reads an IPv4 or IPv6 address from SOCKS5 UDP frame bytes into a raw
    /// <see cref="IPAddressValue"/> without allocating. <paramref name="scopeId"/> is the
    /// interface scope to apply to a decoded IPv6 address (M2): the SOCKS5 UDP wire format does not
    /// carry a scope, so the caller propagates one from the known relay/control endpoint so a
    /// link-local address reconstructed from raw bytes keeps a non-zero
    /// <see cref="IPAddressValue.ScopeId"/> and can route on the correct interface.
    /// </summary>
    private static bool TryReadAddress(ReadOnlySpan<byte> bytes, byte type, long scopeId, out IPAddressValue? address)
    {
        // The raw constructors cannot fail on the length-checked slices: a 4-byte FromIPv4
        // cannot violate the IPv4 upper-bits invariant, so no framework-address exception
        // path remains on the decode.
        address = type == 1 ? IPAddressValue.FromIPv4(bytes) : IPAddressValue.FromIPv6(bytes, checked((uint)scopeId));
        return (type == 1 && bytes.Length == 4) || (type == 4 && bytes.Length == 16);
    }
}
