using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinForward.NdisApi;

/// <summary>
/// The four static-filter-table native calls behind one injectable seam, in the same shape as
/// <see cref="INdisReadPacketCalls"/>: the table byte layout, the read-back verification, and every
/// failure mapping are provable without <c>ndisapi.dll</c>. The seam is deliberately not a general
/// native-call abstraction — the read and send surfaces stay on <see cref="NdisApiDriver"/>.
/// </summary>
/// <remarks>
/// Every method reports the raw native result; the driver captures <c>Marshal.GetLastWin32Error()</c>
/// immediately, so the seam never reads or masks a native error itself. A missing export surfaces as
/// <see cref="EntryPointNotFoundException"/> (or <see cref="DllNotFoundException"/>) exactly as the
/// real resolver produces it, which is what the driver maps to
/// <see cref="NdisKernelFilterStatus.Unavailable"/>.
/// </remarks>
internal interface INdisStaticFilterCalls
{
    /// <summary>Replaces the whole table from the header-prefixed bytes in <paramref name="table"/>.</summary>
    bool SetPacketFilterTable(ReadOnlySpan<byte> table, out int nativeError);

    /// <summary>Clears the whole table.</summary>
    bool ResetPacketFilterTable(out int nativeError);

    /// <summary>Reports the record count of the driver's current table.</summary>
    bool GetPacketFilterTableSize(out uint tableSize, out int nativeError);

    /// <summary>
    /// Reads the table back into <paramref name="table"/>. The caller has already written the
    /// requested record count into the buffer's header; the native wrapper sizes the copy from it.
    /// </summary>
    bool GetPacketFilterTable(Span<byte> table, out int nativeError);
}

/// <summary>The production seam over the driver's open handle, which it borrows and never disposes.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class NdisNativeStaticFilterCalls(NdisApiSafeHandle handle) : INdisStaticFilterCalls
{
    public bool SetPacketFilterTable(ReadOnlySpan<byte> table, out int nativeError)
    {
        fixed (byte* tableBytes = table)
        {
            var result = NdisApiNative.SetPacketFilterTable(handle, tableBytes);
            nativeError = result == 0 ? Marshal.GetLastWin32Error() : 0;
            return result != 0;
        }
    }

    public bool ResetPacketFilterTable(out int nativeError)
    {
        var result = NdisApiNative.ResetPacketFilterTable(handle);
        nativeError = result == 0 ? Marshal.GetLastWin32Error() : 0;
        return result != 0;
    }

    public bool GetPacketFilterTableSize(out uint tableSize, out int nativeError)
    {
        uint size = 0;
        var result = NdisApiNative.GetPacketFilterTableSize(handle, &size);
        nativeError = result == 0 ? Marshal.GetLastWin32Error() : 0;
        tableSize = size;
        return result != 0;
    }

    public bool GetPacketFilterTable(Span<byte> table, out int nativeError)
    {
        fixed (byte* tableBytes = table)
        {
            var result = NdisApiNative.GetPacketFilterTable(handle, tableBytes);
            nativeError = result == 0 ? Marshal.GetLastWin32Error() : 0;
            return result != 0;
        }
    }
}
