using WinForward.Core;

namespace WinForward.Windows;

/// <summary>
/// Turns a driver-written iphlpapi owner-table image into one <see cref="OwnerTable"/> slot. The
/// row-count validation and the row decode live here, apart from the <c>iphlpapi</c> call itself,
/// so the decode is exercisable — and allocation-gated — on a host without the native tables.
/// <para>
/// Filling reuses the slot's row storage and constructs no framework address: a rescan of a table
/// that fits the storage it already has allocates nothing, which is what keeps a scan affordable at
/// one per new flow. The addresses are read as the raw network-order bytes the driver wrote, so no
/// endian conversion is involved.
/// </para>
/// </summary>
internal static class IPHelperOwnerTableParser
{
    internal static unsafe void FillUdp4(OwnerTable table, nint buffer, int rowCount, uint bytesWritten)
    {
        IPHelperTables.ValidateRowCount(rowCount, bytesWritten, sizeof(IPHelperAbi.MibUdpRowOwnerPid), "IPv4 UDP owner table");
        var rows = table.BeginUdpFill(rowCount);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = IPHelperTables.ReadRow<IPHelperAbi.MibUdpRowOwnerPid>(buffer, index);
            rows[index] = new UdpOwnerRow(DecodeIPv4Address(row.LocalAddress), IPHelperAbi.DecodeNetworkPort(row.LocalPort), row.ProcessId);
        }

        table.CompleteFill();
    }

    internal static unsafe void FillUdp6(OwnerTable table, nint buffer, int rowCount, uint bytesWritten)
    {
        IPHelperTables.ValidateRowCount(rowCount, bytesWritten, sizeof(IPHelperAbi.MibUdp6RowOwnerPid), "IPv6 UDP owner table");
        var rows = table.BeginUdpFill(rowCount);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = IPHelperTables.ReadRow<IPHelperAbi.MibUdp6RowOwnerPid>(buffer, index);
            rows[index] = new UdpOwnerRow(DecodeIPv6Value(new ReadOnlySpan<byte>(row.LocalAddress, 16), row.ScopeId), IPHelperAbi.DecodeNetworkPort(row.LocalPort), row.ProcessId);
        }

        table.CompleteFill();
    }

    internal static unsafe void FillTcp4(OwnerTable table, nint buffer, int rowCount, uint bytesWritten)
    {
        IPHelperTables.ValidateRowCount(rowCount, bytesWritten, sizeof(IPHelperAbi.MibTcpRowOwnerPid), "IPv4 TCP owner table");
        var rows = table.BeginTcpFill(rowCount);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = IPHelperTables.ReadRow<IPHelperAbi.MibTcpRowOwnerPid>(buffer, index);
            rows[index] = new TcpOwnerRow(
                Endpoint.From(DecodeIPv4Address(row.LocalAddress), IPHelperAbi.DecodeNetworkPort(row.LocalPort)),
                Endpoint.From(DecodeIPv4Address(row.RemoteAddress), IPHelperAbi.DecodeNetworkPort(row.RemotePort)),
                row.ProcessId);
        }

        table.CompleteFill();
    }

    internal static unsafe void FillTcp6(OwnerTable table, nint buffer, int rowCount, uint bytesWritten)
    {
        IPHelperTables.ValidateRowCount(rowCount, bytesWritten, sizeof(IPHelperAbi.MibTcp6RowOwnerPid), "IPv6 TCP owner table");
        var rows = table.BeginTcpFill(rowCount);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = IPHelperTables.ReadRow<IPHelperAbi.MibTcp6RowOwnerPid>(buffer, index);
            rows[index] = new TcpOwnerRow(
                Endpoint.From(DecodeIPv6Value(new ReadOnlySpan<byte>(row.LocalAddress, 16), row.LocalScopeId), IPHelperAbi.DecodeNetworkPort(row.LocalPort)),
                Endpoint.From(DecodeIPv6Value(new ReadOnlySpan<byte>(row.RemoteAddress, 16), row.RemoteScopeId), IPHelperAbi.DecodeNetworkPort(row.RemotePort)),
                row.ProcessId);
        }

        table.CompleteFill();
    }

    // The address member holds the driver-written network-order bytes as they sit in the row, so
    // reading its raw image reproduces the wire address without an endian conversion.
    private static unsafe IPAddressValue DecodeIPv4Address(uint address) =>
        IPAddressValue.FromIPv4(new ReadOnlySpan<byte>(&address, sizeof(uint)));

    private static IPAddressValue DecodeIPv6Value(ReadOnlySpan<byte> address, uint scopeId) =>
        IPAddressValue.FromIPv6(address, scopeId);
}
