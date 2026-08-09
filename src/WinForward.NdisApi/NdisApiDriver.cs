using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinForward.NdisApi;

public sealed record NdisAdapter(nint RuntimeHandle, string InternalName, uint Medium, byte[] MacAddress, ushort Mtu);

[SupportedOSPlatform("windows")]
public sealed class NdisApiDriver : IDisposable
{
    private readonly NdisApiSafeHandle _handle;

    private NdisApiDriver(NdisApiSafeHandle handle) => _handle = handle;

    public uint Version => NdisApiNative.GetDriverVersion(_handle);

    public static NdisApiDriver Open()
    {
        NdisApiAbi.AssertManagedX64Layout();
        var rawHandle = NdisApiNative.OpenFilterDriver("NDISRD");
        var handle = NdisApiSafeHandle.FromRawHandle(rawHandle);
        if (handle.IsInvalid)
        {
            var nativeError = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(nativeError, $"Unable to open the WinpkFilter NDISRD driver (native error {nativeError}, 0x{nativeError:X8}).");
        }

        return new NdisApiDriver(handle);
    }

    public unsafe IReadOnlyList<NdisAdapter> GetAdapters()
    {
        TcpAdapterList native = default;
        if (NdisApiNative.GetTcpipBoundAdaptersInfo(_handle, &native) == 0)
        {
            var nativeError = Marshal.GetLastWin32Error();
            throw new Win32Exception(nativeError, $"Unable to enumerate NDISAPI adapters (native error {nativeError}, 0x{nativeError:X8}).");
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
        if (NdisApiNative.GetAdapterMode(_handle, &mode) == 0) throw new Win32Exception("Unable to read NDISAPI adapter mode.");
        return mode.Flags;
    }

    public unsafe void SetAdapterMode(nint adapterHandle, uint flags)
    {
        var mode = new AdapterMode { AdapterHandle = adapterHandle, Flags = flags };
        if (NdisApiNative.SetAdapterMode(_handle, &mode) == 0) throw new Win32Exception("Unable to set NDISAPI adapter mode.");
    }

    public unsafe bool TryReadPacket(nint adapterHandle, NdisPacketBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var request = new EthernetRequest { AdapterHandle = adapterHandle, Packet = new NdisrdEthernetPacket { Buffer = buffer.Pointer } };
        return NdisApiNative.ReadPacket(_handle, &request) != 0;
    }

    public unsafe void SendPacketToMstcp(nint adapterHandle, NdisPacketBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var request = new EthernetRequest { AdapterHandle = adapterHandle, Packet = new NdisrdEthernetPacket { Buffer = buffer.Pointer } };
        if (NdisApiNative.SendPacketToMstcp(_handle, &request) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Unable to inject an NDISAPI packet toward MSTCP (native error {error}, length {buffer.Length}, flags 0x{buffer.DeviceFlags:X}, adapter 0x{adapterHandle:X}).");
        }
    }

    public unsafe void SendPacketToAdapter(nint adapterHandle, NdisPacketBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var request = new EthernetRequest { AdapterHandle = adapterHandle, Packet = new NdisrdEthernetPacket { Buffer = buffer.Pointer } };
        if (NdisApiNative.SendPacketToAdapter(_handle, &request) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Unable to inject an NDISAPI packet toward the adapter (native error {error}, length {buffer.Length}, flags 0x{buffer.DeviceFlags:X}, adapter 0x{adapterHandle:X}).");
        }
    }

    public void Dispose() => _handle.Dispose();

    private static unsafe string ReadAscii(byte* source, int offset, int capacity)
    {
        var length = 0;
        while (length < capacity && source[offset + length] != 0) length++;
        return System.Text.Encoding.ASCII.GetString(new ReadOnlySpan<byte>(source + offset, length));
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

    internal nint Pointer => (nint)_buffer;
    public uint DeviceFlags => _buffer is null ? 0 : _buffer->DeviceFlags;
    public nint CapturedAdapterHandle => _buffer is null ? 0 : _buffer->AdapterHandle;
    public int Length => _buffer is null ? 0 : checked((int)_buffer->Length);

    public Span<byte> GetFrame()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (_buffer->Length > NdisApiAbi.MaximumEthernetFrame) throw new InvalidDataException("NDISAPI returned a frame larger than the pinned ABI.");
        return new Span<byte>(_buffer->Buffer, checked((int)_buffer->Length));
    }

    public void SetFrame(ReadOnlySpan<byte> frame, uint deviceFlags, nint adapterHandle)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (frame.Length > NdisApiAbi.MaximumEthernetFrame) throw new ArgumentOutOfRangeException(nameof(frame));
        _buffer->AdapterHandle = adapterHandle;
        _buffer->UnionPadding = 0;
        _buffer->DeviceFlags = deviceFlags;
        _buffer->Length = (uint)frame.Length;
        frame.CopyTo(new Span<byte>(_buffer->Buffer, frame.Length));
    }

    public void Dispose()
    {
        if (_buffer is null) return;
        NativeMemory.Free(_buffer);
        _buffer = null;
    }
}
