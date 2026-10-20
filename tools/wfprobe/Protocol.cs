using System.Runtime.InteropServices;

namespace WinForward.Probe;

// 与 src/driver/probe_ioctl.h 逐字节对应的控制面 ABI（v2）。
internal static class Protocol
{
    public const uint AbiVersion = 2;

    // CTL_CODE(FILE_DEVICE_UNKNOWN, function, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private static uint MakeIoctl(uint function) => (0x22u << 16) | (function << 2);

    public const uint IoctlSetConfig = 0x800;
    public const uint IoctlGetConfig = 0x801;
    public const uint IoctlGetStats = 0x802;
    public const uint IoctlResetStats = 0x803;
    public const uint IoctlGetEvents = 0x804;
    public const uint IoctlSetRuleBlob = 0x805;
    public const uint IoctlGetRuleInfo = 0x806;
    public const uint IoctlMeasureProcessPath = 0x807;

    public static uint SetConfigCode => MakeIoctl(IoctlSetConfig);
    public static uint GetConfigCode => MakeIoctl(IoctlGetConfig);
    public static uint GetStatsCode => MakeIoctl(IoctlGetStats);
    public static uint ResetStatsCode => MakeIoctl(IoctlResetStats);
    public static uint GetEventsCode => MakeIoctl(IoctlGetEvents);
    public static uint SetRuleBlobCode => MakeIoctl(IoctlSetRuleBlob);
    public static uint GetRuleInfoCode => MakeIoctl(IoctlGetRuleInfo);
    public static uint MeasureProcessPathCode => MakeIoctl(IoctlMeasureProcessPath);

    public const uint MaxSelfProcessIds = 8;
    public const uint RuleBlobMaxSize = 64 * 1024;

    public const uint RuleActionPermit = 0;
    public const uint RuleActionBlock = 1;
    public const uint RuleActionRedirect = 2;

    // 驱动里 ExAllocatePoolWithTag(..., 'rPfW') 在小端内存中的字节序。
    public const string PoolTag = "WfPr";

    public const uint RedirectContextMagic = 0x31434657; // "WFC1"
    public const ushort RedirectContextVersion = 1;

    public const uint InvalidRuleIndex = 0xFFFFFFFF;

    // probe_decision
    public const byte DecisionPermittedDisabled = 0;
    public const byte DecisionPermittedNoWriteRight = 1;
    public const byte DecisionPermittedNonTcp = 2;
    public const byte DecisionPermittedListenerPort = 3;
    public const byte DecisionPermittedConditionFlags = 4;
    public const byte DecisionPermittedRedirectState = 5;
    public const byte DecisionPermittedAlreadyRedirected = 6;
    public const byte DecisionPermittedSelfProcess = 7;
    public const byte DecisionPermittedNoRules = 8;
    public const byte DecisionPermittedUnsupported = 9;
    public const byte DecisionPermittedFallback = 10;
    public const byte DecisionPermittedRule = 11;
    public const byte DecisionBlockedRule = 12;
    public const byte DecisionBlockedFallback = 13;
    public const byte DecisionRedirected = 14;
    public const byte DecisionRedirectFailed = 15;
    public const byte DecisionContextAllocFailed = 16;
    public const byte DecisionPermittedDeferredBlock = 17;

    public const byte ActionPermit = 0;
    public const byte ActionBlock = 1;
    public const byte ActionRedirect = 2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct ProbeConfig
{
    public uint AbiVersion;
    public uint Enabled;
    public uint ListenerProcessId;
    public ushort ListenerPort;
    public ushort Reserved0;
    public uint SelfProcessCount;
    public fixed uint SelfProcessIds[8];
    public fixed uint Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct ProbeStats
{
    public ulong ClassifyCalls;
    public ulong Permitted;
    public ulong Blocked;
    public ulong Redirected;
    public ulong RulesReplaced;
    public ulong RejectedBlobs;
    public ulong ContextsAllocated;
    public ulong ContextAllocFailures;
    public ulong ApplyFailures;
    public ulong RedirectFailures;
    public ulong PermittedDisabled;
    public ulong PermittedNoRules;
    public ulong PermittedNoWriteRight;
    public ulong PermittedUnsupported;
    public ulong PermittedNonTcp;
    public ulong PermittedListenerPort;
    public ulong PermittedRedirectState;
    public ulong PermittedConditionFlags;
    public ulong PermittedAlreadyRedirected;
    public ulong PermittedSelfProcess;
    public ulong MatchedPermit;
    public ulong MatchedBlock;
    public ulong MatchedRedirect;
    public ulong FallbackPermit;
    public ulong FallbackBlock;
    public ulong FallbackRedirect;
    public ulong DeferredBlocks;
    public ulong ProcessPathMetadataSeen;
    public uint LastStatus;
    public uint AbiVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct ProbeRuleInfo
{
    public uint AbiVersion;
    public uint Generation;
    public uint Installed;
    public uint RuleCount;
    public uint FallbackAction;
    public uint TotalSize;
    public uint ProcessPoolSize;
    public uint NetworkPoolCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct ProbeEvent
{
    public ulong Timestamp100ns;
    public uint Sequence;
    public uint ProcessId;
    public fixed byte RemoteAddress[16];
    public fixed byte LocalAddress[16];
    public ushort RemotePort;
    public ushort LocalPort;
    public ushort RedirectPort;
    public byte Decision;
    public byte Family;
    public byte Action;
    public byte Branch;
    public uint MatchedRule;
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
        Check<ProbeConfig>(64, "ProbeConfig");
        Check<ProbeStats>(232, "ProbeStats");
        Check<ProbeRuleInfo>(32, "ProbeRuleInfo");
        Check<ProbeEvent>(64, "ProbeEvent");
        Check<RedirectContext>(36, "RedirectContext");
    }

    private static void Check<T>(int expected, string name) where T : unmanaged
    {
        int actual = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        if (actual != expected)
            throw new InvalidOperationException($"{name} 布局不一致：托管侧 {actual} 字节，驱动侧 {expected} 字节");
    }
}
