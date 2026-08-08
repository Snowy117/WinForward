using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinForward.NdisApi;

public static class NdisApiAbi
{
    public const string UpstreamVersion = "v3.6.2";
    public const string UpstreamCommit = "417b8734e844083a10236387fba705d94a2d6bc9";
    public const int AdapterListSize = 32;
    public const int AdapterNameSize = 256;
    public const int EthernetAddressLength = 6;
    public const int MaximumEthernetFrame = 1514;
    public const uint PacketFlagOnSend = 0x00000001;
    public const uint PacketFlagOnReceive = 0x00000002;
    public const uint SentTunnel = 0x00000001;
    public const uint ReceiveTunnel = 0x00000002;
    public const uint LoopbackFilter = 0x00000020;

    public static void AssertManagedX64Layout()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("WinForward supports only x64 NDISAPI ABI in the first release.");
        AssertSize<TcpAdapterList>(8836);
        AssertSize<IntermediateBuffer>(1566);
        AssertSize<EthernetRequest>(16);
        AssertSize<AdapterMode>(12);
        AssertOffset<IntermediateBuffer>(nameof(IntermediateBuffer.DeviceFlags), 16);
        AssertOffset<IntermediateBuffer>(nameof(IntermediateBuffer.Length), 20);
        AssertOffset<IntermediateBuffer>(nameof(IntermediateBuffer.Buffer), 52);
    }

    private static void AssertSize<T>(int expected) where T : unmanaged
    {
        var actual = Unsafe.SizeOf<T>();
        if (actual != expected) throw new TypeLoadException($"NDISAPI ABI mismatch for {typeof(T).Name}: expected {expected}, actual {actual}.");
    }

    private static void AssertOffset<T>(string field, int expected)
    {
        var actual = Marshal.OffsetOf<T>(field).ToInt32();
        if (actual != expected) throw new TypeLoadException($"NDISAPI ABI mismatch for {typeof(T).Name}.{field}: expected {expected}, actual {actual}.");
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct TcpAdapterList
{
    public uint AdapterCount;
    public fixed byte AdapterNames[NdisApiAbi.AdapterListSize * NdisApiAbi.AdapterNameSize];
    public fixed long AdapterHandles[NdisApiAbi.AdapterListSize];
    public fixed uint AdapterMediums[NdisApiAbi.AdapterListSize];
    public fixed byte CurrentAddresses[NdisApiAbi.AdapterListSize * NdisApiAbi.EthernetAddressLength];
    public fixed ushort Mtus[NdisApiAbi.AdapterListSize];
}

[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 1566)]
public unsafe struct IntermediateBuffer
{
    [FieldOffset(0)] public nint AdapterHandle;
    [FieldOffset(8)] public nint UnionPadding;
    [FieldOffset(16)] public uint DeviceFlags;
    [FieldOffset(20)] public uint Length;
    [FieldOffset(24)] public uint Flags;
    [FieldOffset(28)] public uint Ieee8021q;
    [FieldOffset(32)] public uint FilterId;
    [FieldOffset(36)] public fixed uint Reserved[4];
    [FieldOffset(52)] public fixed byte Buffer[NdisApiAbi.MaximumEthernetFrame];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NdisrdEthernetPacket
{
    public nint Buffer;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EthernetRequest
{
    public nint AdapterHandle;
    public NdisrdEthernetPacket Packet;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AdapterMode
{
    public nint AdapterHandle;
    public uint Flags;
}

public sealed class NdisApiSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private NdisApiSafeHandle() : base(ownsHandle: true) { }

    internal static NdisApiSafeHandle FromRawHandle(nint handle)
    {
        var safeHandle = new NdisApiSafeHandle();
        safeHandle.SetHandle(handle);
        return safeHandle;
    }

    protected override bool ReleaseHandle()
    {
        NdisApiNative.CloseFilterDriver(handle);
        return true;
    }
}

internal static partial class NdisApiNative
{
    private const string LibraryName = "ndisapi.dll";

    [LibraryImport(LibraryName, EntryPoint = "OpenFilterDriver", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint OpenFilterDriver(string driverName);

    [LibraryImport(LibraryName, EntryPoint = "CloseFilterDriver")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial void CloseFilterDriver(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "GetDriverVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial uint GetDriverVersion(NdisApiSafeHandle handle);

    [LibraryImport(LibraryName, EntryPoint = "GetTcpipBoundAdaptersInfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int GetTcpipBoundAdaptersInfo(NdisApiSafeHandle handle, TcpAdapterList* adapters);

    [LibraryImport(LibraryName, EntryPoint = "SetAdapterMode", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int SetAdapterMode(NdisApiSafeHandle handle, AdapterMode* mode);

    [LibraryImport(LibraryName, EntryPoint = "GetAdapterMode", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int GetAdapterMode(NdisApiSafeHandle handle, AdapterMode* mode);

    [LibraryImport(LibraryName, EntryPoint = "ReadPacket", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int ReadPacket(NdisApiSafeHandle handle, EthernetRequest* request);

    [LibraryImport(LibraryName, EntryPoint = "SendPacketToMstcp", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int SendPacketToMstcp(NdisApiSafeHandle handle, EthernetRequest* request);

    [LibraryImport(LibraryName, EntryPoint = "SendPacketToAdapter", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int SendPacketToAdapter(NdisApiSafeHandle handle, EthernetRequest* request);
}
