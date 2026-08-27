using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinForward.NdisApi;

public sealed record NdisAdapter(nint RuntimeHandle, string InternalName, uint Medium, byte[] MacAddress, ushort Mtu);

[SupportedOSPlatform("windows")]
public sealed class NdisApiDriver : IDisposable, INdisPacketReader
{
    private const int MaxStackMultiRequestBytes = 1024;

    private readonly NdisApiSafeHandle _handle;
    private readonly NdisNativeCallGate _nativeCallGate = new();

    private NdisApiDriver(NdisApiSafeHandle handle) => _handle = handle;

    public uint Version
    {
        get
        {
            using var gateLease = _nativeCallGate.Enter();
            var version = NdisApiNative.GetDriverVersion(_handle);
            var nativeError = version == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
            return NdisNativeCallStatus.EnsureDriverVersion(version, nativeError);
        }
    }

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
        using (var gateLease = _nativeCallGate.Enter())
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
        using (var gateLease = _nativeCallGate.Enter())
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
        using var gateLease = _nativeCallGate.Enter();
        if (NdisApiNative.SetAdapterMode(_handle, &mode) == 0)
        {
            var nativeError = Marshal.GetLastWin32Error();
            throw new Win32Exception(nativeError, $"Unable to set NDISAPI adapter mode (native error {nativeError}, 0x{nativeError:X8}).");
        }
    }

    public unsafe bool TryReadPacket(nint adapterHandle, NdisPacketBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        uint queuedPacketCount = 0;
        int queueResult;
        int queueError;
        int readResult = 0;
        int readError = 0;

        using (var gateLease = _nativeCallGate.Enter())
        {
            queueResult = NdisApiNative.GetAdapterPacketQueueSize(_handle, adapterHandle, &queuedPacketCount);
            queueError = queueResult == 0 ? Marshal.GetLastWin32Error() : 0;
            if (NdisNativeCallStatus.HasQueuedPackets(queueResult, queueError, queuedPacketCount, adapterHandle))
            {
                var request = new EthernetRequest { AdapterHandle = adapterHandle, Packet = new NdisrdEthernetPacket { Buffer = buffer.Pointer } };
                readResult = NdisApiNative.ReadPacket(_handle, &request);
                readError = readResult == 0 ? Marshal.GetLastWin32Error() : 0;
            }
        }

        return NdisNativeCallStatus.InterpretReadResult(queuedPacketCount, readResult, readError, adapterHandle);
    }

    /// <summary>
    /// Reads up to one batch of packets from the adapter queue into the caller-provided buffers
    /// (one kernel round trip; the queue query and the batched read share a single gate lease).
    /// Returns the number of buffers actually filled; 0 means the queue was empty. A queue
    /// inspection failure, or a failed read from a non-empty queue, throws <see cref="Win32Exception"/>,
    /// matching the single-packet error semantics.
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

        using (var gateLease = _nativeCallGate.Enter())
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

    public unsafe void SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers) =>
        SendPacketsBatch(towardMstcp: true, adapterHandle, buffers);

    public unsafe void SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers) =>
        SendPacketsBatch(towardMstcp: false, adapterHandle, buffers);

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

    private unsafe void SendPacketsBatch(bool towardMstcp, nint adapterHandle, NdisPacketBuffer[] buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        if (buffers.Length == 0) return;

        int result;
        int nativeError;
        using (var gateLease = _nativeCallGate.Enter())
        {
            result = SendPacketsBatchCore(towardMstcp, adapterHandle, buffers, out nativeError);
        }

        if (result != 0) return;
        var target = towardMstcp ? "MSTCP" : "the adapter";
        throw new Win32Exception(nativeError, $"Unable to inject {buffers.Length} NDISAPI packet(s) toward {target} (native error {nativeError}, 0x{nativeError:X8}, adapter 0x{adapterHandle:X}).");
    }

    private unsafe int SendPacketsBatchCore(bool towardMstcp, nint adapterHandle, NdisPacketBuffer[] buffers, out int nativeError)
    {
        var requestByteCount = MultiRequestByteCount(buffers.Length);
        if (requestByteCount <= MaxStackMultiRequestBytes)
        {
            var stackBytes = stackalloc byte[(int)requestByteCount];
            return SendPacketsRequest(stackBytes, towardMstcp, adapterHandle, buffers, out nativeError);
        }

        var heapBytes = (byte*)NativeMemory.AllocZeroed(requestByteCount);
        try
        {
            return SendPacketsRequest(heapBytes, towardMstcp, adapterHandle, buffers, out nativeError);
        }
        finally
        {
            NativeMemory.Free(heapBytes);
        }
    }

    private unsafe int SendPacketsRequest(byte* requestMemory, bool towardMstcp, nint adapterHandle, NdisPacketBuffer[] buffers, out int nativeError)
    {
        BuildMultiRequest(requestMemory, adapterHandle, buffers, buffers.Length);
        var request = (EthernetMultiRequest*)requestMemory;
        var result = towardMstcp ? NdisApiNative.SendPacketsToMstcp(_handle, request) : NdisApiNative.SendPacketsToAdapter(_handle, request);
        nativeError = result == 0 ? Marshal.GetLastWin32Error() : 0;
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
        using var gateLease = _nativeCallGate.Enter();
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
        using var gateLease = _nativeCallGate.Enter();
        if (NdisApiNative.SendPacketToAdapter(_handle, &request) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Unable to inject an NDISAPI packet toward the adapter (native error {error}, length {buffer.Length}, device flags 0x{buffer.DeviceFlags:X}, NDIS flags 0x{buffer.Flags:X}, adapter 0x{adapterHandle:X}).");
        }
    }

    public void Dispose()
    {
        using var gateLease = _nativeCallGate.Enter();
        _handle.Dispose();
    }

    private static unsafe string ReadAscii(byte* source, int offset, int capacity)
    {
        var length = 0;
        while (length < capacity && source[offset + length] != 0) length++;
        return System.Text.Encoding.ASCII.GetString(new ReadOnlySpan<byte>(source + offset, length));
    }
}

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

internal sealed class NdisNativeCallGate
{
    private readonly object _syncRoot = new();
    private readonly Action? _onContention;
    private int _activeCalls;
    private int _maxConcurrentCalls;

    internal NdisNativeCallGate(Action? onContention = null) => _onContention = onContention;

    internal int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

    internal GateLease Enter()
    {
        if (!Monitor.TryEnter(_syncRoot))
        {
            _onContention?.Invoke();
            Monitor.Enter(_syncRoot);
        }
        var activeCalls = Interlocked.Increment(ref _activeCalls);
        UpdateMaximum(activeCalls);
        return new GateLease(_syncRoot, this);
    }

    internal readonly struct GateLease : IDisposable
    {
        private readonly object _syncRoot;
        private readonly NdisNativeCallGate _owner;

        internal GateLease(object syncRoot, NdisNativeCallGate owner)
        {
            _syncRoot = syncRoot;
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Decrement(ref _owner._activeCalls);
            Monitor.Exit(_syncRoot);
        }
    }

    private void UpdateMaximum(int activeCalls)
    {
        var observed = Volatile.Read(ref _maxConcurrentCalls);
        while (observed < activeCalls)
        {
            var previous = Interlocked.CompareExchange(ref _maxConcurrentCalls, activeCalls, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }
}

/// <summary>
/// A managed wrapper over one native <see cref="IntermediateBuffer"/>. Buffers created with the
/// public constructor are privately owned: <see cref="Dispose"/> frees the native memory. Buffers
/// rented from <see cref="NdisPacketBufferPool"/> keep pool ownership: <see cref="Dispose"/>
/// returns them to the pool instead of freeing, so <c>using</c>-style callers need no changes
/// when switching from per-injection allocation to pooling.
/// </summary>
public sealed unsafe class NdisPacketBuffer : IDisposable
{
    private const int StateRented = 1;
    private const int StateIdle = 2;

    private IntermediateBuffer* _buffer;
    private NdisPacketBufferPool? _ownerPool;
    private int _pooledState;

    public NdisPacketBuffer()
    {
        _buffer = Allocate();
    }

    internal NdisPacketBuffer(NdisPacketBufferPool ownerPool)
    {
        _buffer = Allocate();
        _ownerPool = ownerPool;
        _pooledState = StateRented;
    }

    private static IntermediateBuffer* Allocate()
    {
        var pointer = (IntermediateBuffer*)NativeMemory.AllocZeroed((nuint)sizeof(IntermediateBuffer));
        if (pointer is null) throw new InvalidOperationException("Unable to allocate an NDISAPI packet buffer.");
        return pointer;
    }

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            return (nint)_buffer;
        }
    }
    public uint DeviceFlags => _buffer is null ? 0 : _buffer->DeviceFlags;
    public uint Flags => _buffer is null ? 0 : _buffer->Flags;
    public nint CapturedAdapterHandle => _buffer is null ? 0 : _buffer->AdapterHandle;
    public int Length => _buffer is null ? 0 : checked((int)_buffer->Length);

    internal bool IsRentedFrom(NdisPacketBufferPool pool) =>
        ReferenceEquals(_ownerPool, pool) && Volatile.Read(ref _pooledState) == StateRented;

    internal bool TryMarkRented() => Interlocked.CompareExchange(ref _pooledState, StateRented, StateIdle) == StateIdle;

    /// <summary>
    /// Detaches pool ownership and frees the native memory. Called only by the owning pool, for
    /// buffers it exclusively holds (returned past capacity, or drained at pool disposal).
    /// </summary>
    internal void ReleaseFromPool()
    {
        _ownerPool = null;
        var pointer = _buffer;
        _buffer = null;
        if (pointer is not null) NativeMemory.Free(pointer);
    }

    public Span<byte> GetFrame()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (_buffer->Length > NdisApiAbi.MaximumEthernetFrame) throw new InvalidDataException("NDISAPI returned a frame larger than the pinned ABI.");
        return new Span<byte>(_buffer->Buffer, checked((int)_buffer->Length));
    }

    public void SetFrame(ReadOnlySpan<byte> frame, uint deviceFlags, nint adapterHandle)
        => SetFrame(frame, deviceFlags, adapterHandle, flags: 0);

    public void SetFrame(ReadOnlySpan<byte> frame, uint deviceFlags, nint adapterHandle, uint flags)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (frame.Length > NdisApiAbi.MaximumEthernetFrame) throw new ArgumentOutOfRangeException(nameof(frame));
        _buffer->AdapterHandle = adapterHandle;
        _buffer->UnionPadding = 0;
        _buffer->DeviceFlags = deviceFlags;
        _buffer->Length = (uint)frame.Length;
        _buffer->Flags = flags;
        frame.CopyTo(new Span<byte>(_buffer->Buffer, frame.Length));
    }

    public void Dispose()
    {
        if (_ownerPool is { } pool)
        {
            // A pooled buffer's Dispose returns it to its pool exactly once; a repeat Dispose of an
            // already-returned buffer is a no-op because the pool owns it now.
            if (Interlocked.CompareExchange(ref _pooledState, StateIdle, StateRented) == StateRented) pool.OnReturned(this);
            return;
        }
        if (_buffer is null) return;
        NativeMemory.Free(_buffer);
        _buffer = null;
    }
}
