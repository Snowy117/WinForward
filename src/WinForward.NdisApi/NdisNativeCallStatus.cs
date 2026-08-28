using System.ComponentModel;

namespace WinForward.NdisApi;

internal static class NdisNativeCallStatus
{
    internal static bool HasValidNativeHandle(nint handle) => handle != 0 && handle != -1;

    internal static uint EnsureDriverVersion(uint version, int nativeError)
    {
        if (version != uint.MaxValue) return version;
        throw new Win32Exception(nativeError, $"Unable to read the NDISAPI driver version (native error {nativeError}, 0x{nativeError:X8}).");
    }

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

    internal static bool InterpretReadResult(uint queuedPacketCount, int nativeResult, int nativeError, nint adapterHandle)
    {
        if (queuedPacketCount == 0) return false;
        if (nativeResult != 0) return true;
        throw new Win32Exception(nativeError, $"Unable to read an NDISAPI packet from a non-empty queue (native error {nativeError}, queued {queuedPacketCount}, adapter 0x{adapterHandle:X}).");
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
}
