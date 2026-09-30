using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinForward.NdisApi;

/// <summary>
/// The driver's two read-path native calls behind one injectable seam: the batched
/// <c>ReadPackets</c> and the <c>GetAdapterPacketQueueSize</c> disambiguator. The seam exists so the
/// read <em>shape</em> — which call, in what order, with what requested count — is exactly provable
/// without <c>ndisapi.dll</c>. It is deliberately not a general native-call abstraction: the send
/// and adapter-mode surfaces stay on <see cref="NdisApiDriver"/>.
/// </summary>
/// <remarks>
/// The method names mirror the native entry points, so the driver's read path reads as the native
/// call sequence it is. The ETH_M_REQUEST layout itself stays on <see cref="NdisApiDriver"/>
/// (<c>BuildMultiRequest</c>/<c>MultiRequestByteCount</c>), so an implementation calls that one
/// builder rather than carrying a second copy of the layout — the null-slot guard included.
/// </remarks>
internal interface INdisReadPacketCalls
{
    /// <summary>
    /// Queries the adapter's packet queue depth. False means the query itself failed; the native
    /// error is then captured immediately after the call and
    /// <paramref name="queuedPacketCount"/> is meaningless.
    /// </summary>
    bool GetAdapterPacketQueueSize(nint adapterHandle, out uint queuedPacketCount, out int nativeError);

    /// <summary>
    /// Issues the batched read for the first <paramref name="count"/> slots of
    /// <paramref name="buffers"/>. False is the raw native FALSE; <paramref name="nativeError"/> is
    /// captured immediately after the call, and <paramref name="packetsSuccess"/> carries whatever
    /// the driver wrote into <c>dwPacketsSuccess</c>.
    /// </summary>
    bool ReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers, int count, out uint packetsSuccess, out int nativeError);
}

/// <summary>
/// The production seam over the driver's open handle, which it borrows and never disposes. Requests
/// that fit the stack budget are stack-allocated; only a caller array larger than that budget (never
/// the pump's 32-slot batch) reaches the unmanaged heap.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class NdisNativeReadPacketCalls(NdisApiSafeHandle handle) : INdisReadPacketCalls
{
    public bool GetAdapterPacketQueueSize(nint adapterHandle, out uint queuedPacketCount, out int nativeError)
    {
        uint queued = 0;
        var result = NdisApiNative.GetAdapterPacketQueueSize(handle, adapterHandle, &queued);
        nativeError = result == 0 ? Marshal.GetLastWin32Error() : 0;
        queuedPacketCount = queued;
        return result != 0;
    }

    public bool ReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers, int count, out uint packetsSuccess, out int nativeError)
    {
        var requestByteCount = NdisApiDriver.MultiRequestByteCount(count);
        if (requestByteCount <= NdisApiDriver.MaxStackMultiRequestBytes)
        {
            var stackBytes = stackalloc byte[(int)requestByteCount];
            return ReadPacketsRequest(stackBytes, adapterHandle, buffers, count, out packetsSuccess, out nativeError);
        }

        var heapBytes = (byte*)NativeMemory.AllocZeroed(requestByteCount);
        try
        {
            return ReadPacketsRequest(heapBytes, adapterHandle, buffers, count, out packetsSuccess, out nativeError);
        }
        finally
        {
            NativeMemory.Free(heapBytes);
        }
    }

    private bool ReadPacketsRequest(byte* requestMemory, nint adapterHandle, NdisPacketBuffer[] buffers, int count, out uint packetsSuccess, out int nativeError)
    {
        NdisApiDriver.BuildMultiRequest(requestMemory, adapterHandle, buffers, count, offset: 0);
        var request = (EthernetMultiRequest*)requestMemory;
        var result = NdisApiNative.ReadPackets(handle, request);
        nativeError = result == 0 ? Marshal.GetLastWin32Error() : 0;
        packetsSuccess = request->PacketsSuccess;
        return result != 0;
    }
}
