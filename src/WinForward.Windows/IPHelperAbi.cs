using System.Net;
using System.Runtime.InteropServices;

namespace WinForward.Windows;

public static class IPHelperAbi
{
    internal const int UdpTableOwnerPid = 1;

    /// <summary>SOCKADDR_INET offsets inside the 28-byte union: family at 0, then the family-specific payload.</summary>
    internal const int Ipv4AddressOffset = 4;
    internal const int Ipv6AddressOffset = 8;
    internal const int Ipv6ScopeIdOffset = 24;
    internal const int SockaddrInetSize = 28;

    /// <summary>
    /// The offset of <c>Table[0]</c> inside MIB_UNICASTIPADDRESS_TABLE: the row carries 8-byte
    /// aligned members (<c>NET_LUID</c>, <c>LARGE_INTEGER</c>), so after the 4-byte
    /// <c>NumEntries</c> the native layout pads the row array to offset 8. netioapi.h documents
    /// that padding may sit between <c>NumEntries</c> and the first row and that access must
    /// assume it — reading rows at offset 4 (the owner-pid table layout) shifts every row by
    /// four bytes and parses garbage.
    /// </summary>
    internal const int UnicastTableFirstRowOffset = 8;

    public static void AssertManagedLayout()
    {
        AssertSize<MibTcpRowOwnerPid>(24);
        AssertSize<MibUdpRowOwnerPid>(12);
        AssertSize<MibTcp6RowOwnerPid>(56);
        AssertSize<MibUdp6RowOwnerPid>(28);
        AssertOffset<MibTcp6RowOwnerPid>(nameof(MibTcp6RowOwnerPid.LocalPort), 20);
        AssertOffset<MibTcp6RowOwnerPid>(nameof(MibTcp6RowOwnerPid.RemoteAddress), 24);
        AssertOffset<MibTcp6RowOwnerPid>(nameof(MibTcp6RowOwnerPid.ProcessId), 52);
        AssertSize<MibUnicastIpAddressRow>(80);
        AssertOffset<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.InterfaceLuid), 32);
        AssertOffset<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.InterfaceIndex), 40);
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

    /// <summary>
    /// MIB_UNICASTIPADDRESS_ROW (netioapi.h) with every native field declared to pin the x64
    /// 80-byte stride: the 28-byte SOCKADDR_INET union, the interface LUID/index, and the
    /// lifetime/state tail. Only Address, InterfaceLuid, and InterfaceIndex are ever read.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MibUnicastIpAddressRow
    {
        public fixed byte Address[SockaddrInetSize];
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint PrefixOrigin;
        public uint SuffixOrigin;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public byte OnLinkPrefixLength;
        public byte SkipAsSource;
        public uint DadState;
        public uint ScopeId;
        public long CreationTimeStamp;
    }
}
