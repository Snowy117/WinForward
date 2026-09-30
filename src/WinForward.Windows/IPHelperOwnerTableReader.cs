using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinForward.Core;

namespace WinForward.Windows;

/// <summary>
/// The single <c>iphlpapi</c> boundary behind <see cref="IProcessOwnerTableReader"/>: the size
/// probe, the table call, the fail-closed row-count validation and the
/// <c>Marshal.FreeHGlobal</c> release all live here, so nothing above this type touches native
/// memory. Every read is a system-wide enumeration (thousands of rows on a busy desktop) and is
/// therefore cold-path work.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class IPHelperOwnerTableReader : IProcessOwnerTableReader
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int ErrorInsufficientBuffer = 122;
    private const int TcpTableOwnerPidAll = 5;

    public OwnerTable Read(OwnerTableKind kind) => kind switch
    {
        OwnerTableKind.Tcp4 => new OwnerTable(kind, [], ReadTcp4()),
        OwnerTableKind.Tcp6 => new OwnerTable(kind, [], ReadTcp6()),
        OwnerTableKind.Udp4 => new OwnerTable(kind, ReadUdp4(), []),
        OwnerTableKind.Udp6 => new OwnerTable(kind, ReadUdp6(), []),
        _ => OwnerTable.Unavailable,
    };

    private static unsafe UdpOwnerRow[] ReadUdp4()
    {
        var buffer = ReadTable(AfInet, IPHelperAbi.UdpTableOwnerPid, out var rowCount, out var bytesWritten);
        try
        {
            IPHelperTables.ValidateRowCount(rowCount, bytesWritten, sizeof(IPHelperAbi.MibUdpRowOwnerPid), "IPv4 UDP owner table");
            var rows = new UdpOwnerRow[rowCount];
            for (var index = 0; index < rows.Length; index++)
            {
                var row = IPHelperTables.ReadRow<IPHelperAbi.MibUdpRowOwnerPid>(buffer, index);
                rows[index] = new UdpOwnerRow(new IPAddress(row.LocalAddress), IPHelperAbi.DecodeNetworkPort(row.LocalPort), row.ProcessId);
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static unsafe UdpOwnerRow[] ReadUdp6()
    {
        var buffer = ReadTable(AfInet6, IPHelperAbi.UdpTableOwnerPid, out var rowCount, out var bytesWritten);
        try
        {
            IPHelperTables.ValidateRowCount(rowCount, bytesWritten, sizeof(IPHelperAbi.MibUdp6RowOwnerPid), "IPv6 UDP owner table");
            var rows = new UdpOwnerRow[rowCount];
            for (var index = 0; index < rows.Length; index++)
            {
                var row = IPHelperTables.ReadRow<IPHelperAbi.MibUdp6RowOwnerPid>(buffer, index);
                rows[index] = new UdpOwnerRow(IPHelperAbi.DecodeIpv6Address(new ReadOnlySpan<byte>(row.LocalAddress, 16), row.ScopeId), IPHelperAbi.DecodeNetworkPort(row.LocalPort), row.ProcessId);
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static unsafe TcpOwnerRow[] ReadTcp4()
    {
        var buffer = ReadTable(AfInet, TcpTableOwnerPidAll, out var rowCount, out var bytesWritten);
        try
        {
            IPHelperTables.ValidateRowCount(rowCount, bytesWritten, sizeof(IPHelperAbi.MibTcpRowOwnerPid), "IPv4 TCP owner table");
            var rows = new TcpOwnerRow[rowCount];
            for (var index = 0; index < rows.Length; index++)
            {
                var row = IPHelperTables.ReadRow<IPHelperAbi.MibTcpRowOwnerPid>(buffer, index);
                rows[index] = new TcpOwnerRow(new Endpoint(AddressFamilyKind.IPv4, new IPAddress(row.LocalAddress), IPHelperAbi.DecodeNetworkPort(row.LocalPort)), new Endpoint(AddressFamilyKind.IPv4, new IPAddress(row.RemoteAddress), IPHelperAbi.DecodeNetworkPort(row.RemotePort)), row.ProcessId);
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static unsafe TcpOwnerRow[] ReadTcp6()
    {
        var buffer = ReadTable(AfInet6, TcpTableOwnerPidAll, out var rowCount, out var bytesWritten);
        try
        {
            IPHelperTables.ValidateRowCount(rowCount, bytesWritten, sizeof(IPHelperAbi.MibTcp6RowOwnerPid), "IPv6 TCP owner table");
            var rows = new TcpOwnerRow[rowCount];
            for (var index = 0; index < rows.Length; index++)
            {
                var row = IPHelperTables.ReadRow<IPHelperAbi.MibTcp6RowOwnerPid>(buffer, index);
                rows[index] = new TcpOwnerRow(new Endpoint(AddressFamilyKind.IPv6, IPHelperAbi.DecodeIpv6Address(new ReadOnlySpan<byte>(row.LocalAddress, 16), row.LocalScopeId), IPHelperAbi.DecodeNetworkPort(row.LocalPort)), new Endpoint(AddressFamilyKind.IPv6, IPHelperAbi.DecodeIpv6Address(new ReadOnlySpan<byte>(row.RemoteAddress, 16), row.RemoteScopeId), IPHelperAbi.DecodeNetworkPort(row.RemotePort)), row.ProcessId);
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static nint ReadTable(int addressFamily, int tableClass, out int rowCount, out uint bytesWritten)
    {
        uint size = 0;
        var result = tableClass == IPHelperAbi.UdpTableOwnerPid
            ? Native.GetExtendedUdpTable(nint.Zero, ref size, order: false, addressFamily, tableClass, 0)
            : Native.GetExtendedTcpTable(nint.Zero, ref size, order: false, addressFamily, tableClass, 0);
        if (result != ErrorInsufficientBuffer || size < 4) throw new Win32Exception(result);
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        result = tableClass == IPHelperAbi.UdpTableOwnerPid
            ? Native.GetExtendedUdpTable(buffer, ref size, order: false, addressFamily, tableClass, 0)
            : Native.GetExtendedTcpTable(buffer, ref size, order: false, addressFamily, tableClass, 0);
        if (result != 0)
        {
            Marshal.FreeHGlobal(buffer);
            throw new Win32Exception(result);
        }
        rowCount = Marshal.ReadInt32(buffer);
        // The in/out size parameter carries the driver-written byte count on success; callers
        // cross-check it against the announced row count before dereferencing any row.
        bytesWritten = size;
        return buffer;
    }

    private static partial class Native
    {
        [LibraryImport("iphlpapi.dll", EntryPoint = "GetExtendedTcpTable")]
        internal static partial int GetExtendedTcpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

        [LibraryImport("iphlpapi.dll", EntryPoint = "GetExtendedUdpTable")]
        internal static partial int GetExtendedUdpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
    }
}
