using System.Net;
using System.Runtime.InteropServices;

namespace WinForward.Windows;

public static class IpHelperAbi
{
    internal const int UdpTableOwnerPid = 1;

    public static void AssertManagedLayout()
    {
        AssertSize<MibTcpRowOwnerPid>(24);
        AssertSize<MibUdpRowOwnerPid>(12);
        AssertSize<MibTcp6RowOwnerPid>(56);
        AssertSize<MibUdp6RowOwnerPid>(28);
        AssertOffset<MibTcp6RowOwnerPid>(nameof(MibTcp6RowOwnerPid.LocalPort), 20);
        AssertOffset<MibTcp6RowOwnerPid>(nameof(MibTcp6RowOwnerPid.RemoteAddress), 24);
        AssertOffset<MibTcp6RowOwnerPid>(nameof(MibTcp6RowOwnerPid.ProcessId), 52);
    }

    internal static ushort DecodeNetworkPort(uint value) => (ushort)IPAddress.NetworkToHostOrder((short)(value & 0xffff));

    internal static IPAddress DecodeIpv6Address(ReadOnlySpan<byte> address, uint scopeId) => new(address, scopeId);

    private static void AssertSize<T>(int expected) where T : struct
    {
        var actual = Marshal.SizeOf<T>();
        if (actual != expected) throw new TypeLoadException($"IP Helper ABI mismatch for {typeof(T).Name}: expected {expected}, actual {actual}.");
    }

    private static void AssertOffset<T>(string field, int expected)
    {
        var actual = Marshal.OffsetOf<T>(field).ToInt32();
        if (actual != expected) throw new TypeLoadException($"IP Helper ABI mismatch for {typeof(T).Name}.{field}: expected {expected}, actual {actual}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibUdpRowOwnerPid
    {
        public uint LocalAddress;
        public uint LocalPort;
        public uint ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MibUdp6RowOwnerPid
    {
        public fixed byte LocalAddress[16];
        public uint ScopeId;
        public uint LocalPort;
        public uint ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MibTcp6RowOwnerPid
    {
        public fixed byte LocalAddress[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public fixed byte RemoteAddress[16];
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint ProcessId;
    }
}
