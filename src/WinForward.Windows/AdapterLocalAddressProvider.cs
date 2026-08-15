using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Core;

namespace WinForward.Windows;

/// <summary>
/// A hardware-independent projection of one network interface's unicast addresses, mirroring the
/// <see cref="IpAdapterInfo"/> projection in <see cref="AdapterIdentity.cs"/>. The mask is captured
/// only for IPv4 addresses; IPv6 subnet preference uses /64 prefix equality instead.
/// </summary>
public readonly record struct IpAdapterUnicastInfo(string Id, IReadOnlyList<IpAdapterUnicastAddress> Addresses);

public readonly record struct IpAdapterUnicastAddress(IPAddress Address, IPAddress? Ipv4Mask);

public interface IAdapterLocalAddressProvider
{
    /// <summary>
    /// Selects the local unicast address of <paramref name="adapterId"/> that a transparent-redirect
    /// destination should be rewritten to for a flow of <paramref name="family"/> arriving from
    /// <paramref name="clientAddress"/>. Returns null when the adapter cannot be correlated or owns
    /// no usable address of the family (no loopback, no IPv6 link-local), so the caller fails closed.
    /// </summary>
    IPAddress? SelectLocalAddress(string adapterId, AddressFamilyKind family, IPAddress clientAddress);
}

/// <summary>
/// Resolves adapter-local addresses from <see cref="NetworkInterface"/> snapshots. Correlation with
/// the NDISAPI-plane adapter id follows the GUID-primary rule of <see cref="WindowsAdapterInventory"/>
/// (brace/case-insensitive, raw-string equality as fallback). Among the adapter's addresses of the
/// requested family, an address sharing the client's subnet is preferred so the rewritten packet
/// looks like ordinary same-subnet traffic to the local stack.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAdapterLocalAddressProvider : IAdapterLocalAddressProvider
{
    private readonly Func<IReadOnlyList<IpAdapterUnicastInfo>> _adapters;

    public WindowsAdapterLocalAddressProvider(Func<IReadOnlyList<IpAdapterUnicastInfo>>? adapters = null)
    {
        _adapters = adapters ?? GetWindowsAdapterAddresses;
    }

    private static IReadOnlyList<IpAdapterUnicastInfo> GetWindowsAdapterAddresses()
    {
        var result = new List<IpAdapterUnicastInfo>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            var addresses = new List<IpAdapterUnicastAddress>();
            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                IPAddress? mask = null;
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    try
                    {
                        mask = unicast.IPv4Mask;
                    }
                    catch (NetworkInformationException)
                    {
                        // IPv4Mask is not available on every platform snapshot; treat as unknown.
                    }
                }
                addresses.Add(new IpAdapterUnicastAddress(unicast.Address, mask));
            }
            result.Add(new IpAdapterUnicastInfo(network.Id, addresses));
        }
        return result;
    }

    public IPAddress? SelectLocalAddress(string adapterId, AddressFamilyKind family, IPAddress clientAddress)
    {
        ArgumentNullException.ThrowIfNull(adapterId);
        ArgumentNullException.ThrowIfNull(clientAddress);
        foreach (var adapter in _adapters())
        {
            if (!MatchesAdapter(adapter.Id, adapterId)) continue;
            IPAddress? fallback = null;
            foreach (var candidate in adapter.Addresses)
            {
                if (!IsCandidate(candidate.Address, family)) continue;
                fallback ??= candidate.Address;
                if (SharesClientSubnet(candidate, clientAddress)) return candidate.Address;
            }
            return fallback;
        }
        return null;
    }

    private static bool MatchesAdapter(string candidateId, string wantedId)
    {
        if (WindowsAdapterInventory.TryExtractGuid(candidateId, out var candidateGuid) &&
            WindowsAdapterInventory.TryExtractGuid(wantedId, out var wantedGuid))
        {
            return string.Equals(candidateGuid, wantedGuid, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(candidateId, wantedId, StringComparison.Ordinal);
    }

    private static bool IsCandidate(IPAddress address, AddressFamilyKind family) => family switch
    {
        AddressFamilyKind.IPv4 => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address),
        AddressFamilyKind.IPv6 => address.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(address) && !IsIPv6LinkLocal(address),
        _ => false,
    };

    private static bool SharesClientSubnet(IpAdapterUnicastAddress candidate, IPAddress clientAddress)
    {
        var address = candidate.Address;
        if (address.AddressFamily != clientAddress.AddressFamily) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (candidate.Ipv4Mask is null) return false;
            var mask = candidate.Ipv4Mask.GetAddressBytes();
            if (mask.Length != 4 || mask.All(octet => octet == 0)) return false;
            var local = address.GetAddressBytes();
            var client = clientAddress.GetAddressBytes();
            for (var index = 0; index < 4; index++)
            {
                if ((local[index] & mask[index]) != (client[index] & mask[index])) return false;
            }
            return true;
        }

        var localBytes = address.GetAddressBytes();
        var clientBytes = clientAddress.GetAddressBytes();
        return localBytes.AsSpan(0, 8).SequenceEqual(clientBytes.AsSpan(0, 8));
    }

    private static bool IsIPv6LinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80;
    }
}
