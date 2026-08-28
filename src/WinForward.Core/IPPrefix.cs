using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinForward.Core;

/// <summary>
/// A CIDR prefix stored as a raw <see cref="IPAddressValue"/> network plus prefix length, so
/// policy matching against flow endpoints is a two-operation mask compare with no byte-array
/// round-trips. The <see cref="IPAddress"/>-based members are cold edges kept for configuration
/// parsing and tests.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct IPPrefix
{
    public IPPrefix(IPAddress network, int prefixLength)
        : this(IPAddressValue.From(network), prefixLength)
    {
    }

    public IPPrefix(IPAddressValue network, int prefixLength)
    {
        var maxLength = network.Family == AddressFamilyKind.IPv4 ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxLength) throw new ArgumentOutOfRangeException(nameof(prefixLength));
        Network = Normalize(network, prefixLength);
        PrefixLength = prefixLength;
    }

    public IPAddressValue Network { get; }
    public int PrefixLength { get; }

    public static bool TryParse(string? value, out IPPrefix prefix)
    {
        prefix = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1 ||
            !IPAddress.TryParse(value[..separator].Trim(), out var address) ||
            !int.TryParse(value[(separator + 1)..].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length) ||
            length < 0 || length > (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
        {
            return false;
        }

        prefix = new IPPrefix(IPAddressValue.From(address), length);
        return true;
    }

    public bool Contains(Endpoint endpoint) => Contains(endpoint.Address);

    public bool Contains(IPAddressValue address)
    {
        if (address.Family != Network.Family) return false;
        var mask = PrefixMask(PrefixLength, Network.Family);
        return (address.Bits & mask) == Network.Bits;
    }

    public bool Contains(IPAddress? address) => address is not null && Contains(IPAddressValue.From(address));

    /// <summary>The all-ones mask over the first <paramref name="prefixLength"/> bits, aligned to
    /// the family's width: IPv4 masks live in the low 32 bits (where IPv4 addresses are stored);
    /// IPv6 masks span the full 128 bits from the top.</summary>
    private static UInt128 PrefixMask(int prefixLength, AddressFamilyKind family)
    {
        if (prefixLength == 0) return 0;
        if (family == AddressFamilyKind.IPv4)
        {
            if (prefixLength >= 32) return 0xFFFFFFFFul;
            return 0xFFFFFFFFul << (32 - prefixLength);
        }

        return prefixLength == 128 ? ~UInt128.Zero : ~(~UInt128.Zero >> prefixLength);
    }

    private static IPAddressValue Normalize(IPAddressValue address, int prefixLength)
    {
        var mask = PrefixMask(prefixLength, address.Family);
        return new IPAddressValue(address.Bits & mask, address.Family, 0);
    }
}
