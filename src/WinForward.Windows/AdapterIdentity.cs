using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace WinForward.Windows;

public interface IWindowsAdapterInventory
{
    IReadOnlyList<WindowsAdapter> GetCurrentAdapters();
}

/// <summary>
/// A Windows IP Helper adapter snapshot projected onto the identity fields the
/// inventory needs to correlate NDISAPI adapters with user-facing names.
/// </summary>
public readonly record struct IpAdapterInfo(string Id, string Name, byte[] Mac)
{
    public static IpAdapterInfo From(NetworkInterface network) =>
        new(network.Id, network.Name, network.GetPhysicalAddress().GetAddressBytes());
}

[SupportedOSPlatform("windows")]
public sealed class WindowsAdapterInventory : IWindowsAdapterInventory
{
    private readonly Func<IReadOnlyList<(string InternalName, nint Handle, byte[] Mac, ushort Mtu)>> _ndisAdapters;
    private readonly Func<IReadOnlyList<IpAdapterInfo>> _ipAdapters;
    private long _generation;

    public WindowsAdapterInventory(
        Func<IReadOnlyList<(string InternalName, nint Handle, byte[] Mac, ushort Mtu)>> ndisAdapters,
        Func<IReadOnlyList<IpAdapterInfo>>? ipAdapters = null)
    {
        ArgumentNullException.ThrowIfNull(ndisAdapters);
        _ndisAdapters = ndisAdapters;
        _ipAdapters = ipAdapters ?? GetWindowsIpAdapters;
    }

    private static IReadOnlyList<IpAdapterInfo> GetWindowsIpAdapters()
    {
        var result = new List<IpAdapterInfo>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            result.Add(IpAdapterInfo.From(network));
        }
        return result;
    }

    public IReadOnlyList<WindowsAdapter> GetCurrentAdapters()
    {
        var ipAdapters = _ipAdapters();
        var result = new List<WindowsAdapter>();
        var generation = ++_generation;
        foreach (var adapter in _ndisAdapters())
        {
            var matched = MatchAdapter(adapter, ipAdapters);
            var stableId = matched?.Id ?? adapter.InternalName;
            var friendlyName = matched?.Name ?? adapter.InternalName;
            result.Add(new WindowsAdapter(stableId, friendlyName, adapter.InternalName, adapter.Handle, generation));
        }
        return result;
    }

    /// <summary>
    /// Correlates an NDISAPI adapter with a Windows IP Helper identity. The
    /// NDISAPI internal name is typically the adapter GUID (often as
    /// \\DEVICE\{GUID}); Windows IP Helper exposes the same GUID in
    /// <see cref="NetworkInterface.Id"/> plus the human-readable friendly name
    /// in <see cref="NetworkInterface.Name"/>. GUID correlation is primary
    /// because virtual/hidden adapters can share a MAC (or report none). MAC is
    /// kept as a sanity-check fallback when the internal name is not a GUID.
    /// </summary>
    private static IpAdapterInfo? MatchAdapter(
        (string InternalName, nint Handle, byte[] Mac, ushort Mtu) adapter,
        IReadOnlyList<IpAdapterInfo> ipAdapters)
    {
        if (TryExtractGuid(adapter.InternalName, out var internalGuid))
        {
            var guidMatches = ipAdapters.Where(ip => TryExtractGuid(ip.Id, out var idGuid) && string.Equals(idGuid, internalGuid, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (guidMatches.Length == 1) return guidMatches[0];
        }

        var macMatches = ipAdapters.Where(ip => ip.Mac.AsSpan().SequenceEqual(adapter.Mac)).ToArray();
        return macMatches.Length == 1 ? macMatches[0] : null;
    }

    /// <summary>
    /// Normalizes a \\DEVICE\{GUID} adapter identity (or bare GUID) into its
    /// canonical GUID string. Returns false when the value is not a GUID.
    /// </summary>
    internal static bool TryExtractGuid(string value, out string guid)
    {
        guid = string.Empty;
        var trimmed = value.Trim();
        var separator = trimmed.LastIndexOf('\\');
        if (separator >= 0) trimmed = trimmed[(separator + 1)..];
        trimmed = trimmed.Trim('{', '}');
        if (!Guid.TryParse(trimmed, out var parsed)) return false;
        guid = parsed.ToString("D");
        return true;
    }
}
