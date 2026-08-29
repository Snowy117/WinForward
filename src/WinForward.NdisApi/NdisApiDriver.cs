using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinForward.NdisApi;

/// <summary>
/// NDISAPI driver wrapper. Native calls use a two-level gate topology (design D3 of task
/// 08-28-udp-loss-design-flaws): cold control operations (adapter enumeration, adapter mode
/// snapshot/set, close) share one control gate, while hot data operations (batched reads,
/// packet reinjection) serialize per adapter enumeration handle, so a slow IOCTL on one
/// adapter cannot stall every pump.
/// </summary>
/// <remarks>
/// The per-adapter split deliberately supersedes the single process-wide gate previously pinned
/// in .trellis/spec/backend/windows-ndisapi.md: the mutable-OVERLAPPED concern is scoped to each
/// request structure (all of them are method-local here), ndisrd already serves concurrent
/// client processes, and within one adapter handle every call remains serialized, preserving
/// per-adapter reinjection order.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NdisApiDriver : IDisposable, INdisPacketReader
{
    private const int MaxStackMultiRequestBytes = 1024;

    private readonly NdisApiSafeHandle _handle;
    private readonly NdisNativeCallGate _controlGate = new();
    private readonly NdisAdapterGateMap _adapterGates = new();

    private NdisApiDriver(NdisApiSafeHandle handle) => _handle = handle;

    public static NdisApiDriver Open()
    {
        NdisApiAbi.AssertManagedX64Layout();
        var rawHandle = NdisApiNative.OpenFilterDriver("NDISRD");
        var openError = Marshal.GetLastWin32Error();
        if (!NdisNativeCallStatus.HasValidNativeHandle(rawHandle)) NdisNativeCallStatus.ThrowIfOpenFailed(rawHandle, isDriverLoaded: false, openError);

        var handle = NdisApiSafeHandle.FromRawHandle(rawHandle);
        try
        {
            var isDriverLoaded = NdisApiNative.IsDriverLoaded(handle) != 0;
            var loadError = isDriverLoaded ? 0 : Marshal.GetLastWin32Error();
            if (!isDriverLoaded && loadError == 0) loadError = openError;
            NdisNativeCallStatus.ThrowIfOpenFailed(rawHandle, isDriverLoaded, loadError);
            return new NdisApiDriver(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public unsafe IReadOnlyList<NdisAdapter> GetAdapters()
    {
        TcpAdapterList native = default;
        using (var gateLease = _controlGate.Enter())
        {
            if (NdisApiNative.GetTcpipBoundAdaptersInfo(_handle, &native) == 0)
            {
                var nativeError = Marshal.GetLastWin32Error();
                throw new Win32Exception(nativeError, $"Unable to enumerate NDISAPI adapters (native error {nativeError}, 0x{nativeError:X8}).");
            }
        }

        var count = checked((int)Math.Min(native.AdapterCount, NdisApiAbi.AdapterListSize));
        var adapters = new List<NdisAdapter>(count);
        for (var index = 0; index < count; index++)
        {
            var name = ReadAscii(native.AdapterNames, index * NdisApiAbi.AdapterNameSize, NdisApiAbi.AdapterNameSize);
            var mac = new byte[NdisApiAbi.EthernetAddressLength];
            for (var octet = 0; octet < mac.Length; octet++) mac[octet] = native.CurrentAddresses[index * mac.Length + octet];
            adapters.Add(new NdisAdapter((nint)native.AdapterHandles[index], name, native.AdapterMediums[index], mac, native.Mtus[index]));
        }
        return adapters;
    }

    public unsafe uint GetAdapterMode(nint adapterHandle)
    {
        var mode = new AdapterMode { AdapterHandle = adapterHandle };
        using (var gateLease = _controlGate.Enter())
        {
            if (NdisApiNative.GetAdapterMode(_handle, &mode) == 0)
            {
                var nativeError = Marshal.GetLastWin32Error();
                throw new Win32Exception(nativeError, $"Unable to read NDISAPI adapter mode (native error {nativeError}, 0x{nativeError:X8}).");
            }
        }
        return mode.Flags;
    }

    public unsafe void SetAdapterMode(nint adapterHandle, uint flags)
    {
        var mode = new AdapterMode { AdapterHandle = adapterHandle, Flags = flags };
        using var gateLease = _controlGate.Enter();
        if (NdisApiNative.SetAdapterMode(_handle, &mode) == 0)
        {
            var nativeError = Marshal.GetLastWin32Error();
            throw new Win32Exception(nativeError, $"Unable to set NDISAPI adapter mode (native error {nativeError}, 0x{nativeError:X8}).");
        }
    }

    /// <summary>
    /// Reads up to one batch of packets from the adapter queue into the caller-provided buffers
    /// (one kernel round trip; the queue query and the batched read share a single gate lease).
    /// Returns the number of buffers actually filled; 0 means the queue was empty. A queue
    /// inspection failure, or a failed read from a non-empty queue, throws <see cref="Win32Exception"/>.
    /// </summary>
    public unsafe int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        if (buffers.Length == 0) return 0;

        uint queuedPacketCount = 0;
        uint packetsSuccess = 0;
        int requestedCount = 0;
        int readResult = 0;
        int readError = 0;

        var adapterGate = _adapterGates.Get(adapterHandle);
        using (var gateLease = adapterGate.Enter())
        {
            var queueResult = NdisApiNative.GetAdapterPacketQueueSize(_handle, adapterHandle, &queuedPacketCount);
            var queueError = queueResult == 0 ? Marshal.GetLastWin32Error() : 0;
            if (NdisNativeCallStatus.HasQueuedPackets(queueResult, queueError, queuedPacketCount, adapterHandle))
            {
                requestedCount = (int)Math.Min(queuedPacketCount, (uint)buffers.Length);
                readResult = ReadPacketsBatch(adapterHandle, buffers, requestedCount, out packetsSuccess, out readError);
            }
        }

        return NdisNativeCallStatus.InterpretBatchReadResult(queuedPacketCount, requestedCount, readResult, readError, packetsSuccess, adapterHandle);
    }

    // The caller must already hold the native call gate; stack-allocates small requests and falls
    // back to the unmanaged heap for oversized batches.
    private unsafe int ReadPacketsBatch(nint adapterHandle, NdisPacketBuffer[] buffers, int count, out uint packetsSuccess, out int nativeError)
    {
        var requestByteCount = MultiRequestByteCount(count);
        if (requestByteCount <= MaxStackMultiRequestBytes)
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

    private unsafe int ReadPacketsRequest(byte* requestMemory, nint adapterHandle, NdisPacketBuffer[] buffers, int count, out uint packetsSuccess, out int nativeError)
    {
        BuildMultiRequest(requestMemory, adapterHandle, buffers, count);
        var request = (EthernetMultiRequest*)requestMemory;
        var result = NdisApiNative.ReadPackets(_handle, request);
        nativeError = result == 0 ? Marshal.GetLastWin32Error() : 0;
        packetsSuccess = request->PacketsSuccess;
        return result;
    }

    private static unsafe void BuildMultiRequest(byte* requestMemory, nint adapterHandle, NdisPacketBuffer[] buffers, int count)
    {
        var request = (EthernetMultiRequest*)requestMemory;
        request->AdapterHandle = adapterHandle;
        request->PacketsNumber = (uint)count;
        request->PacketsSuccess = 0;
        var slots = (NdisrdEthernetPacket*)&request->FirstBuffer;
        for (var index = 0; index < count; index++)
        {
            if (buffers[index] is null) throw new ArgumentNullException(nameof(buffers));
            slots[index] = new NdisrdEthernetPacket { Buffer = buffers[index].Pointer };
        }
    }

    private static unsafe nuint MultiRequestByteCount(int count) =>
        (nuint)sizeof(EthernetMultiRequest) + (nuint)(count - 1) * (nuint)sizeof(NdisrdEthernetPacket);

    public unsafe void SendPacketToMstcp(nint adapterHandle, NdisPacketBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var request = new EthernetRequest { AdapterHandle = adapterHandle, Packet = new NdisrdEthernetPacket { Buffer = buffer.Pointer } };
        var adapterGate = _adapterGates.Get(adapterHandle);
        using var gateLease = adapterGate.Enter();
        if (NdisApiNative.SendPacketToMstcp(_handle, &request) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Unable to inject an NDISAPI packet toward MSTCP (native error {error}, length {buffer.Length}, device flags 0x{buffer.DeviceFlags:X}, NDIS flags 0x{buffer.Flags:X}, adapter 0x{adapterHandle:X}).");
        }
    }

    public unsafe void SendPacketToAdapter(nint adapterHandle, NdisPacketBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var request = new EthernetRequest { AdapterHandle = adapterHandle, Packet = new NdisrdEthernetPacket { Buffer = buffer.Pointer } };
        var adapterGate = _adapterGates.Get(adapterHandle);
        using var gateLease = adapterGate.Enter();
        if (NdisApiNative.SendPacketToAdapter(_handle, &request) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Unable to inject an NDISAPI packet toward the adapter (native error {error}, length {buffer.Length}, device flags 0x{buffer.DeviceFlags:X}, NDIS flags 0x{buffer.Flags:X}, adapter 0x{adapterHandle:X}).");
        }
    }

    public void Dispose()
    {
        using var gateLease = _controlGate.Enter();
        _handle.Dispose();
    }

    /// <summary>Telemetry: maximum concurrent native calls observed on the control gate.</summary>
    internal int ControlGateMaxConcurrentCalls => _controlGate.MaxConcurrentCalls;

    /// <summary>
    /// Telemetry snapshot of the maximum concurrent native calls observed per adapter gate,
    /// keyed by adapter enumeration handle.
    /// </summary>
    internal IReadOnlyDictionary<nint, int> GetAdapterGateMaxConcurrentCalls() => _adapterGates.GetMaxConcurrentCalls();

    private static unsafe string ReadAscii(byte* source, int offset, int capacity)
    {
        var length = 0;
        while (length < capacity && source[offset + length] != 0) length++;
        return System.Text.Encoding.ASCII.GetString(new ReadOnlySpan<byte>(source + offset, length));
    }
}
