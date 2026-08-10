using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinForward.NdisApi;

public sealed record NdisAdapter(nint RuntimeHandle, string InternalName, uint Medium, byte[] MacAddress, ushort Mtu);

[SupportedOSPlatform("windows")]
public sealed class NdisApiDriver : IDisposable
{
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

public sealed unsafe class NdisPacketBuffer : IDisposable
{
    private IntermediateBuffer* _buffer;

    public NdisPacketBuffer()
    {
        _buffer = (IntermediateBuffer*)NativeMemory.AllocZeroed((nuint)sizeof(IntermediateBuffer));
        if (_buffer is null) throw new InvalidOperationException("Unable to allocate an NDISAPI packet buffer.");
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
        if (_buffer is null) return;
        NativeMemory.Free(_buffer);
        _buffer = null;
    }
}
