using System.Net;

namespace WinForward.Core;

public readonly record struct IpPrefix(IPAddress Network, int PrefixLength)
{
    public static bool TryParse(string value, out IpPrefix prefix)
    {
        prefix = default;
        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1 ||
            !IPAddress.TryParse(value[..separator].Trim(), out var address) ||
            !int.TryParse(value[(separator + 1)..].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length) ||
            length < 0 || length > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
        {
            return false;
        }

        prefix = new IpPrefix(Normalize(address, length), length);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != Network.AddressFamily) return false;
        var networkBytes = Network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        var fullBytes = PrefixLength / 8;
        var remainingBits = PrefixLength % 8;
        for (var index = 0; index < fullBytes; index++)
        {
            if (networkBytes[index] != addressBytes[index]) return false;
        }

        return remainingBits == 0 || (networkBytes[fullBytes] & (byte)(0xff << (8 - remainingBits))) ==
            (addressBytes[fullBytes] & (byte)(0xff << (8 - remainingBits)));
    }

    private static IPAddress Normalize(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (remainingBits != 0) bytes[fullBytes] &= (byte)(0xff << (8 - remainingBits));
        for (var index = fullBytes + (remainingBits == 0 ? 0 : 1); index < bytes.Length; index++) bytes[index] = 0;
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? new IPAddress(bytes, address.ScopeId)
            : new IPAddress(bytes);
    }
}
