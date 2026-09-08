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

    // One batched send request spans 16 + 8*N bytes (ETH_M_REQUEST header plus one pointer slot
    // per packet); this chunk size keeps a full request inside the stackalloc budget above.
    internal const int MaxPacketsPerSendRequest = (MaxStackMultiRequestBytes - 16) / 8;

    private readonly NdisApiSafeHandle _handle;
    private long _batchedSendFlushCount;
    private long _batchedSendPacketCount;
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

    /// <summary>
    /// Registers or releases the driver's TCP/IP bound adapter-list-change notification
    /// (native <c>SetAdapterListChangeEvent</c>). While registered, the driver signals the
    /// caller-provided Win32 event whenever the bound adapter list is rebuilt (adapter
    /// plug/unplug, enable/disable, standby/resume) — every enumeration handle previously
    /// returned by <see cref="GetAdapters"/> is stale from that point and must be
    /// re-enumerated. Passing 0 (<see cref="nint.Zero"/>) releases the registration. The
    /// caller owns the event lifetime: the handle must remain valid for as long as the
    /// registration is active; the driver never closes it.
    /// </summary>
    /// <remarks>
    /// Cold-path control operation (one-time registration at startup, release at shutdown)
    /// routed through the control gate. A native FALSE throws <see cref="Win32Exception"/> —
    /// startup treats a registration failure as fatal because the in-process adapter
    /// refresh is unusable without the notification.
    /// </remarks>
    public void SetAdapterListChangeEvent(nint win32Event)
    {
        using var gateLease = _controlGate.Enter();
        if (NdisApiNative.SetAdapterListChangeEvent(_handle, win32Event) == 0)
        {
            var nativeError = Marshal.GetLastWin32Error();
            var operation = win32Event == nint.Zero ? "release" : "register";
            throw new Win32Exception(nativeError, $"Unable to {operation} the NDISAPI adapter-list-change event (native error {nativeError}, 0x{nativeError:X8}).");
        }
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
        BuildMultiRequest(requestMemory, adapterHandle, buffers, count, offset: 0);
        var request = (EthernetMultiRequest*)requestMemory;
        var result = NdisApiNative.ReadPackets(_handle, request);
        nativeError = result == 0 ? Marshal.GetLastWin32Error() : 0;
        packetsSuccess = request->PacketsSuccess;
        return result;
    }

    // Fills the request header and the packet-pointer slots [0, count) from buffers[offset, offset+count).
    // Visible to tests for direct ABI-layer slot verification (same layout discipline as NdisApiAbiTests).
    internal static unsafe void BuildMultiRequest(byte* requestMemory, nint adapterHandle, NdisPacketBuffer[] buffers, int count, int offset)
    {
        var request = (EthernetMultiRequest*)requestMemory;
        request->AdapterHandle = adapterHandle;
        request->PacketsNumber = (uint)count;
        request->PacketsSuccess = 0;
        var slots = (NdisrdEthernetPacket*)&request->FirstBuffer;
        for (var index = 0; index < count; index++)
        {
            var buffer = buffers[offset + index];
            if (buffer is null) throw new ArgumentNullException(nameof(buffers));
            slots[index] = new NdisrdEthernetPacket { Buffer = buffer.Pointer };
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

    /// <summary>
    /// Injects <paramref name="count"/> packets from <paramref name="buffers"/> toward MSTCP in one
    /// batched request (chunks of at most <see cref="MaxPacketsPerSendRequest"/> keep each
    /// ETH_M_REQUEST inside the stackalloc budget; one adapter-gate lease spans the whole call).
    /// The batched send IOCTLs report no per-packet success count — the user-mode DLL passes no
    /// output buffer, so <c>dwPacketsSuccess</c> never returns (task 08-30-batched-ioctls research:
    /// wiresock/ndisapi@417b8734 ndisapi.cpp + local DLL disassembly) — so a failed batch throws
    /// with the same fail-closed semantics as a failed single send.
    /// </summary>
    public unsafe void SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers, int count) =>
        SendPacketsBatch(adapterHandle, buffers, count, toMstcp: true);

    /// <summary>
    /// Injects <paramref name="count"/> packets from <paramref name="buffers"/> toward the adapter
    /// in one batched request. See <see cref="SendPacketsToMstcp(nint, NdisPacketBuffer[], int)"/>
    /// for the chunking, gate-lease, and all-or-nothing failure contract.
    /// </summary>
    public unsafe void SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count) =>
        SendPacketsBatch(adapterHandle, buffers, count, toMstcp: false);

    private unsafe void SendPacketsBatch(nint adapterHandle, NdisPacketBuffer[] buffers, int count, bool toMstcp)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffers.Length);
        if (count == 0) return;

        var adapterGate = _adapterGates.Get(adapterHandle);
        using var gateLease = adapterGate.Enter();
        var chunkCapacity = Math.Min(count, MaxPacketsPerSendRequest);
        var requestBytes = stackalloc byte[(int)MultiRequestByteCount(chunkCapacity)];
        for (var offset = 0; offset < count; offset += chunkCapacity)
        {
            var chunkCount = Math.Min(chunkCapacity, count - offset);
            BuildMultiRequest(requestBytes, adapterHandle, buffers, chunkCount, offset);
            var request = (EthernetMultiRequest*)requestBytes;
            var result = toMstcp
                ? NdisApiNative.SendPacketsToMstcp(_handle, request)
                : NdisApiNative.SendPacketsToAdapter(_handle, request);
            if (result == 0)
            {
                var error = Marshal.GetLastWin32Error();
                var target = toMstcp ? "MSTCP" : "the adapter";
                throw new Win32Exception(error, $"Unable to inject {chunkCount} NDISAPI packets toward {target} (native error {error}, packets {offset}..{offset + chunkCount - 1} of {count}, adapter 0x{adapterHandle:X}).");
            }
        }
        Interlocked.Increment(ref _batchedSendFlushCount);
        Interlocked.Add(ref _batchedSendPacketCount, count);
    }

    public void Dispose()
    {
        using var gateLease = _controlGate.Enter();
        _handle.Dispose();
    }

    /// <summary>Telemetry: maximum concurrent native calls observed on the control gate.</summary>
    internal int ControlGateMaxConcurrentCalls => _controlGate.MaxConcurrentCalls;

    /// <summary>
    /// Telemetry: successful batched send flushes. Compared with <see cref="BatchedSendPacketCount"/>
    /// this yields the average reinjection batch size — the syscall-amortization evidence for the
    /// batched pass path (task 08-30-batched-ioctls).
    /// </summary>
    internal long BatchedSendFlushCount => Volatile.Read(ref _batchedSendFlushCount);

    /// <summary>Telemetry: packets delivered through successful batched send flushes.</summary>
    internal long BatchedSendPacketCount => Volatile.Read(ref _batchedSendPacketCount);

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
