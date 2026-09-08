using System.ComponentModel;

namespace WinForward.NdisApi;

internal static class NdisNativeCallStatus
{
    internal static bool HasValidNativeHandle(nint handle) => handle != 0 && handle != -1;

    internal static void ThrowIfOpenFailed(nint rawHandle, bool isDriverLoaded, int nativeError)
    {
        if (HasValidNativeHandle(rawHandle) && isDriverLoaded) return;
        var state = HasValidNativeHandle(rawHandle) ? "The NDISAPI wrapper opened, but the NDISRD driver is unavailable" : "Unable to open the WinpkFilter NDISRD driver";
        throw new Win32Exception(nativeError, $"{state} (native error {nativeError}, 0x{nativeError:X8}).");
    }

    internal static bool HasQueuedPackets(int nativeResult, int nativeError, uint queuedPacketCount, nint adapterHandle)
    {
        if (nativeResult != 0) return queuedPacketCount != 0;
        throw new Win32Exception(nativeError, $"Unable to inspect the NDISAPI packet queue (native error {nativeError}, adapter 0x{adapterHandle:X}).");
    }

    internal static int InterpretBatchReadResult(uint queuedPacketCount, int requestedCount, int nativeResult, int nativeError, uint packetsSuccess, nint adapterHandle)
    {
        if (queuedPacketCount == 0) return 0;
        if (nativeResult == 0)
        {
            throw new Win32Exception(nativeError, $"Unable to read NDISAPI packets from a non-empty queue (native error {nativeError}, queued {queuedPacketCount}, requested {requestedCount}, adapter 0x{adapterHandle:X}).");
        }
        // The driver fills dwPacketsSuccess with the actual count; clamp defensively so a
        // misbehaving driver can never make the pump read past the prepared buffers.
        return (int)Math.Min(packetsSuccess, (uint)requestedCount);
    }

    /// <summary>
    /// Whether a native packet-read failure is plausibly transient (adapter power transition,
    /// driver pause, a removal in progress) and therefore worth a bounded retry before the
    /// adapter's interception degrades. Evidence: the ndisrd driver is closed-source, so the
    /// table is cross-checked against the adjacent NDIS filter-driver class (Npcap, nmap#2036):
    /// sleep-wake/removal transitions surface as <c>ERROR_OPERATION_ABORTED</c> (995) and
    /// <c>STATUS_DEVICE_REMOVED</c>-projected codes (<c>ERROR_GEN_FAILURE</c> 31 /
    /// <c>ERROR_DEVICE_NOT_CONNECTED</c> 1167). Every unlisted code classifies as permanent —
    /// fail-closed conservative: a mis-classified permanent error only costs one bounded retry
    /// window before degradation, while a mis-classified transient code would silently keep the
    /// historical global-exit behavior. The degraded-exit log records the full native error so a
    /// real run can refine this table.
    /// </summary>
    internal static bool IsTransientReadError(int nativeError) =>
        nativeError is 21         // ERROR_NOT_READY
            or 170                // ERROR_BUSY
            or 1237               // ERROR_RETRY
            or 995                // ERROR_OPERATION_ABORTED (STATUS_CANCELLED projection)
            or 1167               // ERROR_DEVICE_NOT_CONNECTED
            or 31;                // ERROR_GEN_FAILURE (STATUS_DEVICE_REMOVED projection)
}
