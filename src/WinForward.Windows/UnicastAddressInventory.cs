using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinForward.Windows;

/// <summary>
/// One unicast address row projected off the native table: the owning interface LUID plus the
/// parsed address (scope preserved for IPv6).
/// </summary>
internal readonly record struct UnicastAddressObservation(ulong InterfaceLuid, IPAddress Address);

/// <summary>
/// Reads the system unicast-address table (iphlpapi <c>GetUnicastIpAddressTable</c>) and folds it
/// into per-interface address fingerprints (task 09-17 R1-A). Host link-state changes that never
/// rebuild the NDISRD bound-adapter list — IPv6 temporary-address rotation above all — are
/// invisible to the NDISAPI enumeration, so the capture runner additionally diffs each adapter's
/// unicast addresses. The query is a cold-path control operation invoked once per enumeration;
/// a failure must be tolerated by callers as empty fingerprints, never allowed to break
/// enumeration.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class UnicastAddressInventory
{
    /// <summary>
    /// No real host carries thousands of unicast addresses; the system-allocated table offers no
    /// byte count to cross-check against, so a count beyond this bound is treated as corrupt.
    /// </summary>
    internal const int MaxUnicastAddressRows = 4096;

    internal const ushort AfUnspec = 0;
    internal const ushort AfInet = 2;
    internal const ushort AfInet6 = 23;

    /// <summary>
    /// Returns the normalized address fingerprint per interface, keyed by the interface GUID in
    /// canonical <c>D</c> form — the same normalization the adapter identity contract correlates
    /// on. Throws on native/parse failure; callers tolerate the throw as "no fingerprints".
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadAddressFingerprints()
    {
        var rows = ReadRows();
        return GroupFingerprints(rows, ResolveInterfaceGuid);
    }

    /// <summary>
    /// Resolves an adapter's fingerprint from a fingerprint map: the adapter stable ID is
    /// normalized through the same GUID extraction the identity correlation uses, so a
    /// <c>{GUID}</c>-shaped stable ID finds its interface's entry. Returns an empty string for
    /// non-GUID identities and unknown interfaces.
    /// </summary>
    public static string ResolveFingerprint(string stableId, IReadOnlyDictionary<string, string> fingerprints)
    {
        ArgumentNullException.ThrowIfNull(stableId);
        ArgumentNullException.ThrowIfNull(fingerprints);
        return WindowsAdapterInventory.TryExtractGuid(stableId, out var interfaceGuid)
            && fingerprints.TryGetValue(interfaceGuid, out var fingerprint)
            ? fingerprint
            : string.Empty;
    }

    internal static unsafe IReadOnlyList<UnicastAddressObservation> ReadRows()
    {
        nint table;
        var result = Native.GetUnicastIpAddressTable(AfUnspec, &table);
        if (result != 0) throw new Win32Exception(result);
        try
        {
            return ParseRows(table);
        }
        finally
        {
            Native.FreeMibTable(table);
        }
    }

    /// <summary>Parses a MIB_UNICASTIPADDRESS_TABLE-shaped buffer: 4-byte entry count, then rows.</summary>
    internal static unsafe IReadOnlyList<UnicastAddressObservation> ParseRows(nint tableBuffer)
    {
        if (tableBuffer == nint.Zero) throw new ArgumentNullException(nameof(tableBuffer));
        var rowCount = Marshal.ReadInt32(tableBuffer);
        ValidateEntryCount(rowCount);
        var rows = new UnicastAddressObservation[rowCount];
        for (var index = 0; index < rows.Length; index++)
        {
            var row = IPHelperTables.ReadRow<IPHelperAbi.MibUnicastIpAddressRow>(
                tableBuffer, index, IPHelperAbi.UnicastTableFirstRowOffset);
            rows[index] = ReadObservation(row, index);
        }
        return rows;
    }

    /// <summary>
    /// Fails closed when the entry count cannot belong to a real host table: the buffer is
    /// allocated by the system itself, so unlike the owner tables there is no byte count to
    /// cross-check against — instead the count is bounded by <see cref="MaxUnicastAddressRows"/>,
    /// refusing to walk rows past the allocation on a corrupt count. Callers tolerate the throw
    /// as "no fingerprints", never a partially-read table.
    /// </summary>
    internal static void ValidateEntryCount(int rowCount)
    {
        if (rowCount is < 0 or > MaxUnicastAddressRows)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The unicast address table announced {rowCount} rows; refusing to read a count that cannot belong to a real host table."));
    }

    /// <summary>
    /// Groups rows into one fingerprint per interface, keyed by normalized interface GUID:
    /// sorted unicast addresses joined with <c>;</c>, invariant lower-case, IPv6 link-local
    /// fe80::/10 excluded (per-interface, never identity-bearing). Interfaces the resolver
    /// cannot name contribute nothing.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> GroupFingerprints(
        IReadOnlyList<UnicastAddressObservation> rows,
        Func<ulong, Guid?> interfaceGuidResolver)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(interfaceGuidResolver);

        var addressesByLuid = new Dictionary<ulong, List<IPAddress>>();
        foreach (var row in rows)
        {
            if (row.Address.IsIPv6LinkLocal) continue;
            if (!addressesByLuid.TryGetValue(row.InterfaceLuid, out var addresses))
            {
                addresses = [];
                addressesByLuid.Add(row.InterfaceLuid, addresses);
            }
            addresses.Add(row.Address);
        }

        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (interfaceLuid, addresses) in addressesByLuid)
        {
            if (interfaceGuidResolver(interfaceLuid) is not { } interfaceGuid) continue;
            fingerprints[interfaceGuid.ToString("D")] = BuildFingerprint(addresses);
        }
        return fingerprints;
    }

    internal static string BuildFingerprint(IEnumerable<IPAddress> addresses) =>
        string.Join(';', addresses
            .Select(address => address.ToString().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

    /// <summary>
    /// Converts an interface LUID to the interface GUID (the NetCfgInstanceId the adapter
    /// identity contract correlates on); null when the system refuses the LUID.
    /// </summary>
    internal static Guid? ResolveInterfaceGuid(ulong interfaceLuid) =>
        Native.ConvertInterfaceLuidToGuid(in interfaceLuid, out var interfaceGuid) == 0 ? interfaceGuid : null;

    private static unsafe UnicastAddressObservation ReadObservation(IPHelperAbi.MibUnicastIpAddressRow row, int index)
    {
        var family = *(ushort*)row.Address;
        if (family == AfInet)
        {
            return new UnicastAddressObservation(
                row.InterfaceLuid,
                new IPAddress(new ReadOnlySpan<byte>(row.Address + IPHelperAbi.Ipv4AddressOffset, 4)));
        }

        if (family == AfInet6)
        {
            return new UnicastAddressObservation(
                row.InterfaceLuid,
                new IPAddress(
                    new ReadOnlySpan<byte>(row.Address + IPHelperAbi.Ipv6AddressOffset, 16),
                    *(uint*)(row.Address + IPHelperAbi.Ipv6ScopeIdOffset)));
        }

        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The unicast address table row {index} carries unsupported address family {family}."));
    }

    private static partial class Native
    {
        [LibraryImport("iphlpapi.dll", EntryPoint = "GetUnicastIpAddressTable", SetLastError = true)]
        internal static unsafe partial int GetUnicastIpAddressTable(ushort family, nint* table);

        [LibraryImport("iphlpapi.dll", EntryPoint = "FreeMibTable", SetLastError = true)]
        internal static partial void FreeMibTable(nint buffer);

        [LibraryImport("iphlpapi.dll", EntryPoint = "ConvertInterfaceLuidToGuid", SetLastError = true)]
        internal static partial int ConvertInterfaceLuidToGuid(in ulong interfaceLuid, out Guid interfaceGuid);
    }
}
