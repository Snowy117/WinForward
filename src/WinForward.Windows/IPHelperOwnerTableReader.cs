using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinForward.Windows;

/// <summary>
/// The single <c>iphlpapi</c> boundary behind <see cref="IProcessOwnerTableReader"/>: the size
/// probe, the table call and the <c>NativeMemory</c> release live here, so nothing above this type
/// touches native memory. One reusable <see cref="OwnerTable"/> slot per kind is refilled in
/// place by <see cref="IPHelperOwnerTableParser"/>, so a rescan costs no managed allocation even
/// though a read is a system-wide enumeration (thousands of rows on a busy desktop).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class IPHelperOwnerTableReader : IProcessOwnerTableReader
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int ErrorInsufficientBuffer = 122;
    private const int TcpTableOwnerPidAll = 5;

    private readonly OwnerTable[] _tables =
    [
        new(OwnerTableKind.Tcp4),
        new(OwnerTableKind.Tcp6),
        new(OwnerTableKind.Udp4),
        new(OwnerTableKind.Udp6),
    ];

    public OwnerTable Read(OwnerTableKind kind) => kind switch
    {
        OwnerTableKind.Tcp4 => ReadTcp4(_tables[(int)OwnerTableKind.Tcp4]),
        OwnerTableKind.Tcp6 => ReadTcp6(_tables[(int)OwnerTableKind.Tcp6]),
        OwnerTableKind.Udp4 => ReadUdp4(_tables[(int)OwnerTableKind.Udp4]),
        OwnerTableKind.Udp6 => ReadUdp6(_tables[(int)OwnerTableKind.Udp6]),
        _ => OwnerTable.Unavailable,
    };

    private static unsafe OwnerTable ReadUdp4(OwnerTable table)
    {
        var buffer = ReadTable(AfInet, IPHelperAbi.UdpTableOwnerPid, out var rowCount, out var bytesWritten);
        try
        {
            IPHelperOwnerTableParser.FillUdp4(table, buffer, rowCount, bytesWritten);
            return table;
        }
        finally { NativeMemory.Free(buffer); }
    }

    private static unsafe OwnerTable ReadUdp6(OwnerTable table)
    {
        var buffer = ReadTable(AfInet6, IPHelperAbi.UdpTableOwnerPid, out var rowCount, out var bytesWritten);
        try
        {
            IPHelperOwnerTableParser.FillUdp6(table, buffer, rowCount, bytesWritten);
            return table;
        }
        finally { NativeMemory.Free(buffer); }
    }

    private static unsafe OwnerTable ReadTcp4(OwnerTable table)
    {
        var buffer = ReadTable(AfInet, TcpTableOwnerPidAll, out var rowCount, out var bytesWritten);
        try
        {
            IPHelperOwnerTableParser.FillTcp4(table, buffer, rowCount, bytesWritten);
            return table;
        }
        finally { NativeMemory.Free(buffer); }
    }

    private static unsafe OwnerTable ReadTcp6(OwnerTable table)
    {
        var buffer = ReadTable(AfInet6, TcpTableOwnerPidAll, out var rowCount, out var bytesWritten);
        try
        {
            IPHelperOwnerTableParser.FillTcp6(table, buffer, rowCount, bytesWritten);
            return table;
        }
        finally { NativeMemory.Free(buffer); }
    }

    private static unsafe void* ReadTable(int addressFamily, int tableClass, out int rowCount, out uint bytesWritten)
    {
        uint size = 0;
        var result = tableClass == IPHelperAbi.UdpTableOwnerPid
            ? Native.GetExtendedUdpTable(table: null, ref size, order: false, addressFamily, tableClass, 0)
            : Native.GetExtendedTcpTable(table: null, ref size, order: false, addressFamily, tableClass, 0);
        if (result != ErrorInsufficientBuffer || size < 4) throw new Win32Exception(result);
        var buffer = NativeMemory.AllocZeroed(size);
        if (buffer is null) throw new InvalidOperationException("Unable to allocate the owner-table buffer.");
        result = tableClass == IPHelperAbi.UdpTableOwnerPid
            ? Native.GetExtendedUdpTable(buffer, ref size, order: false, addressFamily, tableClass, 0)
            : Native.GetExtendedTcpTable(buffer, ref size, order: false, addressFamily, tableClass, 0);
        if (result != 0)
        {
            NativeMemory.Free(buffer);
            throw new Win32Exception(result);
        }
        rowCount = Unsafe.ReadUnaligned<int>(buffer);
        // The in/out size parameter carries the driver-written byte count on success; the parser
        // cross-checks it against the announced row count before dereferencing any row.
        bytesWritten = size;
        return buffer;
    }

    private static partial class Native
    {
        [LibraryImport("iphlpapi.dll", EntryPoint = "GetExtendedTcpTable")]
        internal static unsafe partial int GetExtendedTcpTable(void* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

        [LibraryImport("iphlpapi.dll", EntryPoint = "GetExtendedUdpTable")]
        internal static unsafe partial int GetExtendedUdpTable(void* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
    }
}
