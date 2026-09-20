using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("WinForward.Core.Tests")]
[assembly: InternalsVisibleTo("WinForward.Benchmarks")]

namespace WinForward.NdisApi;

public static class NdisApiAbi
{
    public const int AdapterListSize = 32;
    public const int AdapterNameSize = 256;
    public const int EthernetAddressLength = 6;
    public const int MaximumEthernetFrame = 1514;
    public const uint PacketFlagOnSend = 0x00000001;
    public const uint PacketFlagOnReceive = 0x00000002;
    public const uint SentTunnel = 0x00000001;
    public const uint ReceiveTunnel = 0x00000002;

    public static void AssertManagedX64Layout()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("WinForward supports only x64 NDISAPI ABI in the first release.");
        AssertSize<TcpAdapterList>(8836);
        AssertSize<IntermediateBuffer>(1566);
        AssertSize<NdisrdEthernetPacket>(8);
        AssertSize<EthernetRequest>(16);
        AssertSize<EthernetMultiRequest>(24);
        AssertSize<AdapterMode>(12);
        AssertOffset<TcpAdapterList>(nameof(TcpAdapterList.AdapterCount), 0);
        AssertOffset<TcpAdapterList>(nameof(TcpAdapterList.AdapterNames), 4);
        AssertOffset<TcpAdapterList>(nameof(TcpAdapterList.AdapterHandles), 8196);
        AssertOffset<TcpAdapterList>(nameof(TcpAdapterList.AdapterMediums), 8452);
        AssertOffset<TcpAdapterList>(nameof(TcpAdapterList.CurrentAddresses), 8580);
        AssertOffset<TcpAdapterList>(nameof(TcpAdapterList.Mtus), 8772);
        AssertOffset<IntermediateBuffer>(nameof(IntermediateBuffer.AdapterHandle), 0);
        AssertOffset<IntermediateBuffer>(nameof(IntermediateBuffer.UnionPadding), 8);
        AssertOffset<IntermediateBuffer>(nameof(IntermediateBuffer.DeviceFlags), 16);
        AssertOffset<IntermediateBuffer>(nameof(IntermediateBuffer.Length), 20);
        AssertOffset<IntermediateBuffer>(nameof(IntermediateBuffer.Flags), 24);
        AssertOffset<IntermediateBuffer>(nameof(IntermediateBuffer.Ieee8021q), 28);
        AssertOffset<IntermediateBuffer>(nameof(IntermediateBuffer.FilterId), 32);
        AssertOffset<IntermediateBuffer>(nameof(IntermediateBuffer.Reserved), 36);
        AssertOffset<IntermediateBuffer>(nameof(IntermediateBuffer.Buffer), 52);
        AssertOffset<NdisrdEthernetPacket>(nameof(NdisrdEthernetPacket.Buffer), 0);
        AssertOffset<EthernetRequest>(nameof(EthernetRequest.AdapterHandle), 0);
        AssertOffset<EthernetRequest>(nameof(EthernetRequest.Packet), 8);
        AssertOffset<EthernetMultiRequest>(nameof(EthernetMultiRequest.AdapterHandle), 0);
        AssertOffset<EthernetMultiRequest>(nameof(EthernetMultiRequest.PacketsNumber), 8);
        AssertOffset<EthernetMultiRequest>(nameof(EthernetMultiRequest.PacketsSuccess), 12);
        AssertOffset<EthernetMultiRequest>(nameof(EthernetMultiRequest.FirstBuffer), 16);
        AssertOffset<AdapterMode>(nameof(AdapterMode.AdapterHandle), 0);
        AssertOffset<AdapterMode>(nameof(AdapterMode.Flags), 8);
    }

    private static void AssertSize<T>(int expected) where T : unmanaged
    {
        var actual = Unsafe.SizeOf<T>();
        if (actual != expected) throw new TypeLoadException(string.Create(CultureInfo.InvariantCulture, $"NDISAPI ABI mismatch for {typeof(T).Name}: expected {expected}, actual {actual}."));
    }

    private static void AssertOffset<T>(string field, int expected)
    {
        var actual = Marshal.OffsetOf<T>(field).ToInt32();
        if (actual != expected) throw new TypeLoadException(string.Create(CultureInfo.InvariantCulture, $"NDISAPI ABI mismatch for {typeof(T).Name}.{field}: expected {expected}, actual {actual}."));
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct TcpAdapterList
{
    public uint AdapterCount;
#pragma warning disable MA0189 // These fixed buffers mirror the ndisapi.h TCP_ADAPTER_LIST declaration byte-for-byte (size and every offset pinned by AssertManagedX64Layout); InlineArray would replace the native declaration mirror with generated buffer types for no runtime gain.
    public fixed byte AdapterNames[NdisApiAbi.AdapterListSize * NdisApiAbi.AdapterNameSize];
    public fixed long AdapterHandles[NdisApiAbi.AdapterListSize];
    public fixed uint AdapterMediums[NdisApiAbi.AdapterListSize];
    public fixed byte CurrentAddresses[NdisApiAbi.AdapterListSize * NdisApiAbi.EthernetAddressLength];
    public fixed ushort Mtus[NdisApiAbi.AdapterListSize];
#pragma warning restore MA0189
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
#pragma warning disable MA0189 // Fixed buffers mirror the ndisapi.h INTERMEDIATE_BUFFER declaration (size 1566 and the Reserved/Buffer offsets pinned by AssertManagedX64Layout); callers build spans from the fixed pointers, which InlineArray would force through MemoryMarshal rewrites for no runtime gain.
    [FieldOffset(36)] public fixed uint Reserved[4];
    [FieldOffset(52)] public fixed byte Buffer[NdisApiAbi.MaximumEthernetFrame];
#pragma warning restore MA0189
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

/// <summary>
/// Managed header of the variable-size native ETH_M_REQUEST (upstream ndisapi.h, Pack=1, x64):
/// hAdapterHandle(8) + dwPacketsNumber(4, in) + dwPacketsSuccess(4, out) + NDISRD_ETH_Packet[N].
/// The array is laid out manually in unmanaged memory: the fixed header occupies 16 bytes and
/// each packet slot is one <see cref="NdisrdEthernetPacket"/> pointer (8 bytes), so a request for
/// N buffers spans 16 + 8*N bytes. <see cref="FirstBuffer"/> aliases EthPacket[0].
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EthernetMultiRequest
{
    public nint AdapterHandle;
    public uint PacketsNumber;
    public uint PacketsSuccess;
    public nint FirstBuffer;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AdapterMode
{
    public nint AdapterHandle;
    public uint Flags;
}

public sealed class NdisApiSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
#pragma warning disable CA1419 // The handle is produced only by the driver's Open path via FromRawHandle; a public constructor would let callers construct an un-opened handle. No interop import returns this type (the handle-typed imports take it as an input parameter, which needs no public constructor), so the marshalling scenario CA1419 guards against is absent here.
    private NdisApiSafeHandle() : base(ownsHandle: true) { }
#pragma warning restore CA1419

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

    static NdisApiNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(NdisApiNative).Assembly, ResolveLibrary);
    }

    private static nint ResolveLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        // ReSharper disable once ConvertIfStatementToReturnStatement // Foreign-library probe: the early "not our library" return keeps the load path out of a 150+ char ternary (B1 disposition).
        if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase)) return nint.Zero;
        return NativeLibrary.Load(GetApplicationLocalLibraryPath(AppContext.BaseDirectory));
    }

    internal static string GetApplicationLocalLibraryPath(string applicationBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationBaseDirectory);
        var path = Path.Combine(applicationBaseDirectory, LibraryName);
        // ReSharper disable once ConvertIfStatementToReturnStatement // Guard-clause + throw reads failure-first; the suggested `cond ? throw ... : value` form has no precedent in this repo (B1 disposition).
        if (!File.Exists(path)) throw new DllNotFoundException($"WinForward requires {LibraryName} beside the executable: {path}");
        return path;
    }

    [LibraryImport(LibraryName, EntryPoint = "OpenFilterDriver", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial nint OpenFilterDriver(string driverName);

    [LibraryImport(LibraryName, EntryPoint = "IsDriverLoaded", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int IsDriverLoaded(NdisApiSafeHandle handle);

    [LibraryImport(LibraryName, EntryPoint = "CloseFilterDriver", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial void CloseFilterDriver(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "GetTcpipBoundAdaptersInfo", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int GetTcpipBoundAdaptersInfo(NdisApiSafeHandle handle, TcpAdapterList* adapters);

    [LibraryImport(LibraryName, EntryPoint = "SetAdapterListChangeEvent", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int SetAdapterListChangeEvent(NdisApiSafeHandle handle, nint win32Event);

    [LibraryImport(LibraryName, EntryPoint = "SetAdapterMode", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int SetAdapterMode(NdisApiSafeHandle handle, AdapterMode* mode);

    [LibraryImport(LibraryName, EntryPoint = "GetAdapterMode", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int GetAdapterMode(NdisApiSafeHandle handle, AdapterMode* mode);

    [LibraryImport(LibraryName, EntryPoint = "GetAdapterPacketQueueSize", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int GetAdapterPacketQueueSize(NdisApiSafeHandle handle, nint adapterHandle, uint* packetCount);

    [LibraryImport(LibraryName, EntryPoint = "ReadPackets", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int ReadPackets(NdisApiSafeHandle handle, EthernetMultiRequest* request);

    [LibraryImport(LibraryName, EntryPoint = "SendPacketToMstcp", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int SendPacketToMstcp(NdisApiSafeHandle handle, EthernetRequest* request);

    [LibraryImport(LibraryName, EntryPoint = "SendPacketsToMstcp", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int SendPacketsToMstcp(NdisApiSafeHandle handle, EthernetMultiRequest* request);

    [LibraryImport(LibraryName, EntryPoint = "SendPacketToAdapter", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int SendPacketToAdapter(NdisApiSafeHandle handle, EthernetRequest* request);

    [LibraryImport(LibraryName, EntryPoint = "SendPacketsToAdapter", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static unsafe partial int SendPacketsToAdapter(NdisApiSafeHandle handle, EthernetMultiRequest* request);
}
