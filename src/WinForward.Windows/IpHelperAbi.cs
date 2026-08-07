using System.Runtime.InteropServices;

namespace WinForward.Windows;

public static class IpHelperAbi
{
    public static void AssertManagedLayout()
    {
        AssertSize<IpHelperTcp4Row>(24);
        AssertSize<IpHelperUdp4Row>(12);
        AssertSize<IpHelperTcp6Row>(56);
        AssertSize<IpHelperUdp6Row>(28);
        AssertOffset<IpHelperTcp6Row>(nameof(IpHelperTcp6Row.LocalPort), 20);
        AssertOffset<IpHelperTcp6Row>(nameof(IpHelperTcp6Row.RemoteAddress), 24);
        AssertOffset<IpHelperTcp6Row>(nameof(IpHelperTcp6Row.ProcessId), 52);
    }

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
    private struct IpHelperTcp4Row
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IpHelperUdp4Row
    {
        public uint LocalAddress, LocalPort, ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct IpHelperTcp6Row
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

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct IpHelperUdp6Row
    {
        public fixed byte LocalAddress[16];
        public uint ScopeId;
        public uint LocalPort;
        public uint ProcessId;
    }
}
