using System.Runtime.InteropServices;

namespace WinForward.Probe;

// 与 src/driver/probe_ioctl.h 逐字节对应的控制面 ABI。字段顺序、宽度、对齐都不能单独改。
internal static class Protocol
{
    public const uint AbiVersion = 1;

    // CTL_CODE(FILE_DEVICE_UNKNOWN, function, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private static uint MakeIoctl(uint function) => (0x22u << 16) | (function << 2);

    public const uint IoctlSetConfig = 0x800;
    public const uint IoctlGetConfig = 0x801;
    public const uint IoctlGetStats = 0x802;
    public const uint IoctlResetStats = 0x803;
    public const uint IoctlGetEvents = 0x804;

    public static uint SetConfigCode => MakeIoctl(IoctlSetConfig);
    public static uint GetConfigCode => MakeIoctl(IoctlGetConfig);
    public static uint GetStatsCode => MakeIoctl(IoctlGetStats);
    public static uint ResetStatsCode => MakeIoctl(IoctlResetStats);
    public static uint GetEventsCode => MakeIoctl(IoctlGetEvents);

    public const uint RedirectModeDocumented = 0;
    public const uint RedirectModeForceLoopback = 1;
    public const uint RedirectModeForceLocal = 2;

    // 驱动里 ExAllocatePoolWithTag(..., 'rPfW') 在小端内存中的字节序。
    public const string PoolTag = "WfPr";

    public const uint RedirectContextMagic = 0x31434657; // "WFC1"
    public const ushort RedirectContextVersion = 1;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct ProbeConfig
{
    public uint AbiVersion;
    public uint Enabled;
    public uint Mode;
    public uint TargetProcessId;
    public uint ListenerProcessId;
    public uint LocalAddressOverride;
    public ushort ListenerPort;
    public ushort Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct ProbeStats
{
    public ulong ClassifyCalls;
    public ulong Redirected;
    public ulong ContextsAllocated;
    public ulong ContextAllocFailures;
    public ulong ApplyFailures;
    public ulong PermittedDisabled;
    public ulong PermittedWrongProcess;
    public ulong PermittedListenerPort;
    public ulong PermittedRedirectState;
    public ulong PermittedAlreadyRedirected;
    public ulong PermittedNoWriteRight;
    public ulong PermittedNonTcp;
    public uint LastStatus;
    public uint AbiVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct ProbeEvent
{
    public ulong Timestamp100ns;
    public uint Sequence;
    public uint ProcessId;
    public fixed byte OriginalAddress[4];
    public fixed byte LocalAddress[4];
    public ushort OriginalPort;
    public ushort LocalPort;
    public ushort RedirectPort;
    public byte Decision;
    public byte Mode;
    public byte Branch;
    public byte AddressFamily;
}

// winforward::wfp::redirect_context（src/common/redirect_context.h）的镜像。
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct RedirectContext
{
    public uint Magic;
    public uint RuleIndex;
    public uint ProcessId;
    public ushort Version;
    public ushort OriginalPort;
    public byte Family;
    public byte Protocol;
    public fixed byte Reserved[2];
    public fixed byte OriginalAddress[16];
}

internal static class ProtocolLayout
{
    public static void Verify()
    {
        Check<ProbeConfig>(28, "ProbeConfig");
        Check<ProbeStats>(104, "ProbeStats");
        Check<ProbeEvent>(40, "ProbeEvent");
        Check<RedirectContext>(36, "RedirectContext");
    }

    private static void Check<T>(int expected, string name) where T : unmanaged
    {
        int actual = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        if (actual != expected)
            throw new InvalidOperationException($"{name} 布局不一致：托管侧 {actual} 字节，驱动侧 {expected} 字节");
    }
}
