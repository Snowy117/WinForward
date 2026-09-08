using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using WinForward.Core;

namespace WinForward.Windows;

public sealed partial class WindowsProcessAttributor : IProcessAttributor
{
    private const int ErrorInsufficientBuffer = 122;
    private readonly TimeSpan _retryDelay;
    private readonly int _cacheCapacity;
    private readonly Dictionary<ProcessCacheKey, ProcessIdentity> _identityCache = [];
    private readonly Queue<ProcessCacheKey> _cacheOrder = [];
    private readonly Lock _cacheGate = new();

    public WindowsProcessAttributor(TimeSpan? retryDelay = null, int cacheCapacity = 1024)
    {
        if (cacheCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(2);
        _cacheCapacity = cacheCapacity;
    }

    public async ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var result = FindOwnerSafely(key);
        if (result is null && _retryDelay > TimeSpan.Zero)
        {
            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            result = FindOwnerSafely(key);
        }

        return result is null ? null : ReadProcessIdentity(result.Value);
    }

    private static uint? FindOwner(FlowKey key)
    {
        return key.Protocol switch
        {
            TransportProtocol.Udp => IPHelperTables.FindUdpOwner(key.Local),
            TransportProtocol.Tcp => IPHelperTables.FindTcpOwner(key.Local, key.Remote),
            _ => null
        };
    }

    private static uint? FindOwnerSafely(FlowKey key)
    {
        try { return FindOwner(key); }
        catch (Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private ProcessIdentity? ReadProcessIdentity(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var creationTime = process.StartTime.ToUniversalTime();
            var cacheKey = new ProcessCacheKey(processId, creationTime);
            lock (_cacheGate)
            {
                if (_identityCache.TryGetValue(cacheKey, out var cached)) return cached;
            }

            var name = process.ProcessName;
            var path = TryGetFullProcessImagePath(processId);

            var identity = new ProcessIdentity(processId, creationTime, name, path);
            lock (_cacheGate)
            {
                if (_identityCache.ContainsKey(cacheKey)) return _identityCache[cacheKey];
                while (_identityCache.Count >= _cacheCapacity && _cacheOrder.TryDequeue(out var evicted)) _identityCache.Remove(evicted);
                _identityCache[cacheKey] = identity;
                _cacheOrder.Enqueue(cacheKey);
            }

            return identity;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string? TryGetFullProcessImagePath(uint processId)
    {
        using var processHandle = SafeProcessHandle.Open(processId);
        if (processHandle.IsInvalid) return null;

        const int MaximumPathLength = 32_768;
        var capacity = 260;
        while (true)
        {
            var buffer = new char[capacity];
            var length = (uint)buffer.Length;
            bool succeeded;
            fixed (char* path = buffer)
            {
                succeeded = Native.QueryFullProcessImageName(processHandle, 0, path, ref length);
            }

            if (succeeded)
            {
                var pathLength = checked((int)Math.Min(length, (uint)buffer.Length));
                if (pathLength > 0 && buffer[pathLength - 1] == '\0') pathLength--;
                return new string(buffer, 0, pathLength);
            }

            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || capacity >= MaximumPathLength) return null;
            capacity = Math.Min(capacity * 2, MaximumPathLength);
        }
    }

    private readonly record struct ProcessCacheKey(uint ProcessId, DateTime CreationTimeUtc);

    private sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;

        private SafeProcessHandle() : base(ownsHandle: true) { }

        public static SafeProcessHandle Open(uint processId)
        {
            var result = new SafeProcessHandle();
            result.SetHandle(Native.OpenProcess(ProcessQueryLimitedInformation, false, processId));
            return result;
        }

        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }

    private static partial class Native
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(nint handle);

        [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* executablePath, ref uint size);
    }
}

internal static partial class IPHelperTables
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int ErrorInsufficientBuffer = 122;
    private const int TcpTableOwnerPidAll = 5;

    public static uint? FindUdpOwner(Endpoint local)
    {
        var owners = local.AddressFamily == AddressFamilyKind.IPv4 ? ReadUdp4() : ReadUdp6();
        var wildcard = local.AddressFamily == AddressFamilyKind.IPv4 ? IPAddress.Any : IPAddress.IPv6Any;
        var matches = owners.Where(row => row.Port == local.Port && (row.Address.Equals(wildcard) || row.Address.Equals(local.Address))).Select(row => row.ProcessId).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static uint? FindTcpOwner(Endpoint local, Endpoint remote)
    {
        var owners = local.AddressFamily == AddressFamilyKind.IPv4 ? ReadTcp4() : ReadTcp6();
        var matches = owners.Where(row => row.Local.Port == local.Port && row.Remote.Port == remote.Port && row.Local.Address.Equals(local.Address) && row.Remote.Address.Equals(remote.Address)).Select(row => row.ProcessId).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static unsafe IReadOnlyList<UdpOwner> ReadUdp4()
    {
        var buffer = ReadTable(AfInet, IPHelperAbi.UdpTableOwnerPid, out var rowCount, out var bytesWritten);
        try
        {
            ValidateRowCount(rowCount, bytesWritten, sizeof(IPHelperAbi.MibUdpRowOwnerPid), "IPv4 UDP owner table");
            var rows = new UdpOwner[rowCount];
            for (var index = 0; index < rows.Length; index++)
            {
                var row = ReadRow<IPHelperAbi.MibUdpRowOwnerPid>(buffer, index);
                rows[index] = new UdpOwner(new IPAddress(row.LocalAddress), IPHelperAbi.DecodeNetworkPort(row.LocalPort), row.ProcessId);
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static unsafe IReadOnlyList<UdpOwner> ReadUdp6()
    {
        var buffer = ReadTable(AfInet6, IPHelperAbi.UdpTableOwnerPid, out var rowCount, out var bytesWritten);
        try
        {
            ValidateRowCount(rowCount, bytesWritten, sizeof(IPHelperAbi.MibUdp6RowOwnerPid), "IPv6 UDP owner table");
            var rows = new UdpOwner[rowCount];
            for (var index = 0; index < rows.Length; index++)
            {
                var row = ReadRow<IPHelperAbi.MibUdp6RowOwnerPid>(buffer, index);
                rows[index] = new UdpOwner(IPHelperAbi.DecodeIpv6Address(new ReadOnlySpan<byte>(row.LocalAddress, 16), row.ScopeId), IPHelperAbi.DecodeNetworkPort(row.LocalPort), row.ProcessId);
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static unsafe IReadOnlyList<TcpOwner> ReadTcp4()
    {
        var buffer = ReadTable(AfInet, TcpTableOwnerPidAll, out var rowCount, out var bytesWritten);
        try
        {
            ValidateRowCount(rowCount, bytesWritten, sizeof(IPHelperAbi.MibTcpRowOwnerPid), "IPv4 TCP owner table");
            var rows = new TcpOwner[rowCount];
            for (var index = 0; index < rows.Length; index++)
            {
                var row = ReadRow<IPHelperAbi.MibTcpRowOwnerPid>(buffer, index);
                rows[index] = new TcpOwner(new Endpoint(AddressFamilyKind.IPv4, new IPAddress(row.LocalAddress), IPHelperAbi.DecodeNetworkPort(row.LocalPort)), new Endpoint(AddressFamilyKind.IPv4, new IPAddress(row.RemoteAddress), IPHelperAbi.DecodeNetworkPort(row.RemotePort)), row.ProcessId);
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static unsafe IReadOnlyList<TcpOwner> ReadTcp6()
    {
        var buffer = ReadTable(AfInet6, TcpTableOwnerPidAll, out var rowCount, out var bytesWritten);
        try
        {
            ValidateRowCount(rowCount, bytesWritten, sizeof(IPHelperAbi.MibTcp6RowOwnerPid), "IPv6 TCP owner table");
            var rows = new TcpOwner[rowCount];
            for (var index = 0; index < rows.Length; index++)
            {
                var row = ReadRow<IPHelperAbi.MibTcp6RowOwnerPid>(buffer, index);
                rows[index] = new TcpOwner(new Endpoint(AddressFamilyKind.IPv6, IPHelperAbi.DecodeIpv6Address(new ReadOnlySpan<byte>(row.LocalAddress, 16), row.LocalScopeId), IPHelperAbi.DecodeNetworkPort(row.LocalPort)), new Endpoint(AddressFamilyKind.IPv6, IPHelperAbi.DecodeIpv6Address(new ReadOnlySpan<byte>(row.RemoteAddress, 16), row.RemoteScopeId), IPHelperAbi.DecodeNetworkPort(row.RemotePort)), row.ProcessId);
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static nint ReadTable(int addressFamily, int tableClass, out int rowCount, out uint bytesWritten)
    {
        uint size = 0;
        var result = tableClass == IPHelperAbi.UdpTableOwnerPid
            ? Native.GetExtendedUdpTable(nint.Zero, ref size, false, addressFamily, tableClass, 0)
            : Native.GetExtendedTcpTable(nint.Zero, ref size, false, addressFamily, tableClass, 0);
        if (result != ErrorInsufficientBuffer || size < 4) throw new Win32Exception(result);
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        result = tableClass == IPHelperAbi.UdpTableOwnerPid
            ? Native.GetExtendedUdpTable(buffer, ref size, false, addressFamily, tableClass, 0)
            : Native.GetExtendedTcpTable(buffer, ref size, false, addressFamily, tableClass, 0);
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

    /// <summary>
    /// Fails closed when an iphlpapi owner table announces a row count its own written byte
    /// count cannot hold. The row count is read from the driver-filled buffer itself, so a
    /// stale or corrupt count would otherwise let <see cref="ReadRow{T}"/> walk past the
    /// allocation — the same driver-reported-count discipline the NDISAPI seam enforces on
    /// batch sizes. A negative count is equally inconsistent and rejected. Attribution callers
    /// tolerate the throw as "no attribution" (retry / null-identity path), never a wrong PID.
    /// </summary>
    internal static void ValidateRowCount(int rowCount, uint bytesWritten, int rowSize, string tableName)
    {
        if (rowCount < 0 || 4 + (long)rowCount * rowSize > bytesWritten)
            throw new InvalidOperationException($"The {tableName} announced {rowCount} rows of {rowSize} bytes each but wrote only {bytesWritten} bytes; refusing to read rows beyond the table payload.");
    }

    private static unsafe T ReadRow<T>(nint buffer, int index) where T : unmanaged =>
        Unsafe.ReadUnaligned<T>((void*)(buffer + 4 + index * sizeof(T)));

    private readonly record struct UdpOwner(IPAddress Address, ushort Port, uint ProcessId);
    [StructLayout(LayoutKind.Auto)] private readonly record struct TcpOwner(Endpoint Local, Endpoint Remote, uint ProcessId);

    private static partial class Native
    {
        [LibraryImport("iphlpapi.dll", EntryPoint = "GetExtendedTcpTable")]
        internal static partial int GetExtendedTcpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

        [LibraryImport("iphlpapi.dll", EntryPoint = "GetExtendedUdpTable")]
        internal static partial int GetExtendedUdpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
    }
}
