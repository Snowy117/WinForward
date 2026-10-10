using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using WinForward.Core;

namespace WinForward.Windows;

public sealed partial class WindowsProcessAttributor : IProcessAttributor
{
    private const int ErrorInsufficientBuffer = 122;
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(2);
    private readonly int _cacheCapacity;
    private readonly Dictionary<ProcessCacheKey, ProcessIdentity> _identityCache = [];
    private readonly Queue<ProcessCacheKey> _cacheOrder = [];
    private readonly Lock _cacheGate = new();
    private readonly ProcessOwnerTableCache _ownerTables;

    public WindowsProcessAttributor(int cacheCapacity = 1024, Action? ownerTableReadSink = null)
        : this(cacheCapacity, CreateDefaultOwnerTableReader(), ProcessOwnerTableCache.DefaultWindowMs)
    {
        _ownerTables.ReadSink = ownerTableReadSink;
    }

    /// <summary>
    /// The seam constructor: the owner-table provider and the snapshot window are injectable so the
    /// cache's coalescing, reuse and staleness rules are exercisable without the native tables.
    /// </summary>
    internal WindowsProcessAttributor(int cacheCapacity, IProcessOwnerTableReader ownerTableReader, int ownerTableCacheWindowMs, Func<DateTimeOffset>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cacheCapacity);
        _cacheCapacity = cacheCapacity;
        _ownerTables = new ProcessOwnerTableCache(ownerTableReader, ownerTableCacheWindowMs, clock);
    }

    /// <summary>The one owner-table provider for a host without the native tables.</summary>
    private static IProcessOwnerTableReader CreateDefaultOwnerTableReader() =>
        OperatingSystem.IsWindows() ? new IPHelperOwnerTableReader() : UnavailableOwnerTableReader.Instance;

    public async ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken cancellationToken)
    {
        // Attempt 1 asks now; the retry asks again 2 ms later. Both go through the snapshot cache
        // with their own request instant, so the retry coalesces onto any read another caller
        // started after it asked instead of forcing a second scan, while the worst case (one read,
        // then one genuine post-delay read) is unchanged.
        var result = FindOwnerSafely(key);
        if (result is null && s_retryDelay > TimeSpan.Zero)
        {
            await Task.Delay(s_retryDelay, cancellationToken).ConfigureAwait(false);
            result = FindOwnerSafely(key);
        }

        // The owner tables are reachable on any host through the injected reader — that is what
        // makes the cache's coalescing and staleness rules testable here — but reading a process
        // identity is native to Windows, so an owner found off-Windows resolves to no identity.
        return result is null || !OperatingSystem.IsWindows() ? null : ReadProcessIdentity(result.Value);
    }

    private uint? FindOwnerSafely(FlowKey key)
    {
        try { return _ownerTables.Lookup(key); }
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

            var identity = new ProcessIdentity(name, path);
            lock (_cacheGate)
            {
                if (_identityCache.TryGetValue(cacheKey, out var value)) return value;
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

        const int maximumPathLength = 32_768;
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

            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || capacity >= maximumPathLength) return null;
            capacity = Math.Min(capacity * 2, maximumPathLength);
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
            result.SetHandle(Native.OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId));
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

/// <summary>
/// The bounds helpers the <c>iphlpapi</c> table readers share.
/// </summary>
internal static class IPHelperTables
{
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
        if (rowCount < 0 || 4 + ((long)rowCount * rowSize) > bytesWritten)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The {tableName} announced {rowCount} rows of {rowSize} bytes each but wrote only {bytesWritten} bytes; refusing to read rows beyond the table payload."));
    }

    /// <summary>
    /// Reads one table row at its native offset; shared by every iphlpapi table reader. The
    /// DWORD-only owner-pid rows are 4-byte aligned, so their <c>Table[0]</c> sits directly after
    /// the count (offset 4, the default); a row carrying 8-byte-aligned members — the unicast
    /// address row's <c>NET_LUID</c>/<c>LARGE_INTEGER</c> — forces the table header to pad
    /// <c>Table[0]</c> to <paramref name="firstRowOffset"/> 8 (documented in netioapi.h: access
    /// must assume padding between <c>NumEntries</c> and the first row).
    /// </summary>
    internal static unsafe T ReadRow<T>(nint buffer, int index, int firstRowOffset = 4) where T : unmanaged =>
        Unsafe.ReadUnaligned<T>((void*)(buffer + firstRowOffset + (index * sizeof(T))));
}
