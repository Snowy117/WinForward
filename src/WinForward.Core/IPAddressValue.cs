using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinForward.Core;

/// <summary>
/// A fixed-size, allocation-free IP address value. IPv4 keeps its four big-endian bytes in the
/// low 32 bits (upper 96 bits zero); IPv6 keeps all sixteen big-endian bytes. This is the
/// hot-path representation used by packet parsing, flow keys, and prefix matching; convert to
/// <see cref="IPAddress"/> only on cold edges such as socket calls, configuration, and logging.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct IPAddressValue : IEquatable<IPAddressValue>
{
    /// <summary>The raw big-endian address bits; IPv4 occupies the low 32 bits only.</summary>
    public UInt128 Bits { get; }

    public AddressFamilyKind Family { get; }

    /// <summary>
    /// The IPv6 scope identifier carried over from a source <see cref="IPAddress"/>; always zero
    /// for IPv4 and for addresses parsed off the wire. Link-local IPv6 relays need it to route on
    /// the correct interface, so it participates in equality.
    /// </summary>
    public uint ScopeId { get; }

    public IPAddressValue(UInt128 bits, AddressFamilyKind family, uint scopeId = 0)
    {
        if (family == AddressFamilyKind.IPv4 && bits >> 32 != 0) throw new ArgumentException("An IPv4 address must keep its upper 96 bits zero.", nameof(bits));
        Bits = bits;
        Family = family;
        ScopeId = scopeId;
    }

    public static IPAddressValue FromIPv4(uint addressBigEndian) => new(addressBigEndian, AddressFamilyKind.IPv4, 0);

    public static IPAddressValue FromIPv4(ReadOnlySpan<byte> fourBytes)
    {
        if (fourBytes.Length != 4) throw new ArgumentException("An IPv4 address requires exactly four bytes.", nameof(fourBytes));
        return new(BinaryPrimitives.ReadUInt32BigEndian(fourBytes), AddressFamilyKind.IPv4, 0);
    }

    public static IPAddressValue FromIPv6(ReadOnlySpan<byte> sixteenBytes, uint scopeId = 0)
    {
        if (sixteenBytes.Length != 16) throw new ArgumentException("An IPv6 address requires exactly sixteen bytes.", nameof(sixteenBytes));
        return new(BinaryPrimitives.ReadUInt128BigEndian(sixteenBytes), AddressFamilyKind.IPv6, scopeId);
    }

    /// <summary>Converts a framework address without allocating (stack buffer only).</summary>
    public static IPAddressValue From(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        Span<byte> buffer = stackalloc byte[16];
        if (!address.TryWriteBytes(buffer, out var written)) throw new ArgumentException("The address has an unsupported binary size.", nameof(address));
        return address.AddressFamily == AddressFamily.InterNetwork
            ? FromIPv4(buffer[..written])
            : FromIPv6(buffer[..written], checked((uint)address.ScopeId));
    }

    /// <summary>Implicit conversion from a framework address: an input-direction convenience for
    /// configuration, tests, and other cold edges. Hot paths construct from wire bytes instead.</summary>
    public static implicit operator IPAddressValue(IPAddress address) => From(address);

    public bool IsIPv4Any => Family == AddressFamilyKind.IPv4 && Bits == 0;
    public bool IsIPv6Any => Family == AddressFamilyKind.IPv6 && Bits == 0 && ScopeId == 0;

    /// <summary>Writes the address in network byte order (4 or 16 bytes) into <paramref name="destination"/>.</summary>
    public bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        if (Family == AddressFamilyKind.IPv4)
        {
            if (destination.Length < 4)
            {
                bytesWritten = 0;
                return false;
            }

            BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)Bits);
            bytesWritten = 4;
            return true;
        }

        if (destination.Length < 16)
        {
            bytesWritten = 0;
            return false;
        }

        BinaryPrimitives.WriteUInt128BigEndian(destination, Bits);
        bytesWritten = 16;
        return true;
    }

    /// <summary>Allocates a framework address; cold edges only (sockets, logging, tests).</summary>
    public IPAddress ToIPAddress()
    {
        Span<byte> buffer = stackalloc byte[16];
        _ = TryWrite(buffer, out var written);
        var bytes = buffer[..written].ToArray();
        if (Family == AddressFamilyKind.IPv4) return new IPAddress(bytes);
        if (ScopeId == 0) return new IPAddress(bytes);
        return new IPAddress(bytes, ScopeId);
    }

    public bool Equals(IPAddressValue other) => Bits == other.Bits && Family == other.Family && ScopeId == other.ScopeId;
    public override bool Equals(object? obj) => obj is IPAddressValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine((ulong)Bits, (ulong)(Bits >> 64), (int)Family, (int)ScopeId);

    public static bool operator ==(IPAddressValue left, IPAddressValue right) => left.Equals(right);
    public static bool operator !=(IPAddressValue left, IPAddressValue right) => !left.Equals(right);

    /// <summary>Formats via the framework's address formatting; cold edges only because it allocates.</summary>
    public override string ToString() => ToIPAddress().ToString();
}
