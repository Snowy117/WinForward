using System.Net;
using System.Runtime.InteropServices;

namespace WinForward.Probe;

// 与 src/driver/probe_ioctl.h 逐字节对应的控制面 ABI（v7）。
internal static class Protocol
{
    public const uint AbiVersion = 7;


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

    // v4：MAC 帧层（forwarded）规则表与计数。与 host 的规则表/计数完全独立。
    public const uint IoctlSetForwardedBlob = 0x808;
    public const uint IoctlGetForwardedInfo = 0x809;
    public const uint IoctlGetForwardedStats = 0x80A;
    public const uint IoctlResetForwardedStats = 0x80B;

    // v5：forwarded TCP 数据面。
    public const uint IoctlSetForwardedConfig = 0x80C;
    public const uint IoctlGetForwardedConfig = 0x80D;
    public const uint IoctlQueryForwardedRedirect = 0x80E;

    // v7：per-ingress listener 映射（16 字节头 + 0..16 条 28 字节记录）。
    public const uint IoctlSetForwardedIngressConfig = 0x80F;
    public const uint IoctlGetForwardedIngressConfig = 0x810;
    public const uint ForwardedIngressCapacity = 16;
    public const uint ForwardedIngressAnyIfIndex = 0xFFFFFFFF;

    public static uint SetConfigCode => MakeIoctl(IoctlSetConfig);
    public static uint GetConfigCode => MakeIoctl(IoctlGetConfig);

    public static uint GetStatsCode => MakeIoctl(IoctlGetStats);
    public static uint ResetStatsCode => MakeIoctl(IoctlResetStats);
    public static uint GetEventsCode => MakeIoctl(IoctlGetEvents);
    public static uint SetRuleBlobCode => MakeIoctl(IoctlSetRuleBlob);
    public static uint GetRuleInfoCode => MakeIoctl(IoctlGetRuleInfo);
    public static uint MeasureProcessPathCode => MakeIoctl(IoctlMeasureProcessPath);
    public static uint SetForwardedBlobCode => MakeIoctl(IoctlSetForwardedBlob);
    public static uint GetForwardedInfoCode => MakeIoctl(IoctlGetForwardedInfo);
    public static uint GetForwardedStatsCode => MakeIoctl(IoctlGetForwardedStats);
    public static uint ResetForwardedStatsCode => MakeIoctl(IoctlResetForwardedStats);
    public static uint SetForwardedConfigCode => MakeIoctl(IoctlSetForwardedConfig);
    public static uint GetForwardedConfigCode => MakeIoctl(IoctlGetForwardedConfig);
    public static uint QueryForwardedRedirectCode => MakeIoctl(IoctlQueryForwardedRedirect);
    public static uint SetForwardedIngressConfigCode => MakeIoctl(IoctlSetForwardedIngressConfig);
    public static uint GetForwardedIngressConfigCode => MakeIoctl(IoctlGetForwardedIngressConfig);

    public const uint MaxSelfProcessIds = 8;
    public const uint RuleBlobMaxSize = 64 * 1024;
    public const uint MaxBlockFilters = 128;

    public const uint RuleBlobMagic = 0x31425257;
    public const ushort RuleBlobVersion = 2;
    public const int RuleBlobHeaderSize = 56;
    public const int RuleBlobRuleSize = 32;
    public const int RuleBlobProcessSelectorSize = 8;
    public const int RuleBlobPortRangeSize = 4;
    public const int RuleBlobNetworkPrefixSize = 20;
    public const int RuleBlobSegmentAlignment = 4;
    public const int RuleBlobProcessSelectorTextMaxSize = 511;

    public const byte ProcessSelectorKindFileName = 0;
    public const byte ProcessSelectorKindPath = 1;

    public const byte RuleProtocolMaskTcp = 0x01;
    public const byte RuleProtocolMaskUdp = 0x02;
    public const byte RuleFamilyMaskIpv4 = 0x01;
    public const byte RuleFamilyMaskIpv6 = 0x02;

    public const byte RuleFamilyAny = 0;
    public const byte RuleFamilyIpv4 = 4;
    public const byte RuleFamilyIpv6 = 6;

    public const uint RuleActionPermit = 0;
    public const uint RuleActionBlock = 1;
    public const uint RuleActionRedirect = 2;

    // 规则协议；与 IPPROTO_TCP / IPPROTO_UDP 的数值一致。
    public const byte RuleProtocolAny = 0;
    public const byte RuleProtocolTcp = 6;
    public const byte RuleProtocolUdp = 17;

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
    public ulong UdpClassifyCalls;
    public ulong UdpPermitted;
    public ulong UdpBlocked;
    public ulong UdpRedirected;
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

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct ProbeForwardedInfo
{
    public uint AbiVersion;
    public uint Generation;
    public uint Installed;
    public uint RuleCount;
    public uint FallbackAction;
    public uint TotalSize;
    public uint NetworkPoolCount;
    public uint Reserved0;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct ProbeForwardedStats
{
    public ulong ClassifyCalls;
    public ulong InboundFrames;
    public ulong OutboundFrames;
    public ulong Permitted;
    public ulong Blocked;
    public ulong Matched;
    public ulong MatchedPermit;
    public ulong MatchedBlock;
    public ulong Fallback;
    public ulong FallbackPermit;
    public ulong FallbackBlock;
    public ulong SelfInjected;
    public ulong NoTable;
    public ulong Malformed;
    public ulong NonIp;
    public ulong NoWriteRight;
    public ulong RejectedBlobs;
    public ulong TablesReplaced;
    public ulong MatchedRedirect;
    public ulong Redirected;
    public ulong RedirectCreated;
    public ulong RedirectIngress;
    public ulong RedirectReturn;
    public ulong RedirectBlocked;
    public ulong RedirectReturnFailed;
    public ulong RedirectNoConfig;
    public ulong RedirectNoState;
    public ulong RedirectCapacity;
    public ulong RedirectNonTcp;
    public ulong RedirectInjectFailed;
    public ulong RedirectExpired;
    public ulong RedirectReturnMiss;
    public ulong RedirectMultiNb;
    public ulong InjectCompleteCalls;
    public ulong InjectCompleteFailed;
    public ulong UdpCreated;
    public ulong UdpIngress;
    public ulong UdpReturn;
    public ulong UdpBlocked;
    public ulong UdpReturnFailed;
    public ulong UdpReturnMiss;
    public ulong UdpNoConfig;
    public ulong UdpNoState;
    public ulong UdpCapacity;
    public ulong UdpFragmented;
    public ulong UdpInjectFailed;
    public ulong UdpExpired;
    public ulong UdpMultiNb;
    public uint RedirectActive;
    public uint RedirectHighWater;
    public uint LastStatus;
    public uint AbiVersion;
    public ulong RedirectReturnAmbiguous;
    public ulong UdpReturnAmbiguous;
    public ulong RedirectNoIngress;
    public ulong UdpNoIngress;
    public ulong RedirectMultiL;
    public ulong UdpMultiL;
    public ulong UdpReturnMissBlocked;

}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct ProbeForwardedConfig
{
    public uint Enabled;
    public ushort ListenerPort;
    public byte Family;
    public byte Reserved0;
    public fixed byte ListenerAddress[16];
    public uint Reserved1;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct ProbeForwardedIngressConfig
{
    public uint AbiVersion;
    public uint Enabled;
    public uint IngressCount;
    public uint Reserved0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct ProbeForwardedIngress
{
    public uint IfIndex;
    public ushort ListenerPort;
    public byte Family;
    public byte Reserved0;
    public fixed byte ListenerAddress[16];
     public uint Reserved1;

     public readonly IPAddress? ListenerIP
     {
         get
         {
             if (Family != 4 && Family != 6)
             {
                 return null;
             }

             fixed (byte* pointer = ListenerAddress)
             {
                 return Family == 4
                            ? new IPAddress(new ReadOnlySpan<byte>(pointer, 4))
                            : new IPAddress(new ReadOnlySpan<byte>(pointer, 16));
             }
         }
     }
}


[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct ProbeForwardedRedirectQuery
{
    public ushort ListenerPort;
    public byte Family;
    public byte Reserved0;
    public fixed byte ClientAddress[16];
    public ushort ClientPort;
    public byte Protocol; // 0/6 = TCP，17 = UDP
    public byte Reserved1;
    public fixed byte ListenerAddress[16];
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct ProbeForwardedRedirect
{
    public const uint MagicValue = 0x31524657; // "WFR1"
    public const ushort VersionValue = 2;
    public const ushort LegacyVersionValue = 1;

    public uint Magic;
    public ushort Version;
    public byte Family;
    public byte Protocol;
    public fixed byte ClientAddress[16];
    public ushort ClientPort;
    public fixed byte ServerAddress[16];
    public ushort ServerPort;
    public ushort ListenerPort;
    public ushort Reserved0;
    public uint IngressIfIndex;
    public uint UpstreamId;
    public uint RuleIndex;
    public uint State;
    public fixed byte ListenerAddress[16];
    public uint Generation;
    public uint Reserved1;

    public IPAddress ClientIP
    {
        get
        {
            fixed (byte* pointer = ClientAddress)
            {
                return AddressOf(pointer, Family);
            }
        }
    }

    public IPAddress ServerIP
    {
        get
        {
            fixed (byte* pointer = ServerAddress)
            {
                return AddressOf(pointer, Family);
            }
        }
    }

    public IPAddress? ListenerIP
    {
        get
        {
            if (Version < VersionValue)
            {
                return null;
            }

            fixed (byte* pointer = ListenerAddress)
            {
                return AddressOf(pointer, Family);
            }
        }
    }

    private static IPAddress AddressOf(byte* address, byte family) =>
        family == 4 ? new IPAddress(new ReadOnlySpan<byte>(address, 4)) : new IPAddress(new ReadOnlySpan<byte>(address, 16));
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
    public byte Protocol;
    public byte Reserved0;
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
        Check<ProbeStats>(264, "ProbeStats");
        Check<ProbeRuleInfo>(32, "ProbeRuleInfo");
        Check<ProbeForwardedInfo>(32, "ProbeForwardedInfo");
        Check<ProbeForwardedStats>(456, "ProbeForwardedStats");
        Check<ProbeForwardedConfig>(28, "ProbeForwardedConfig");
        Check<ProbeForwardedIngressConfig>(16, "ProbeForwardedIngressConfig");
        Check<ProbeForwardedIngress>(28, "ProbeForwardedIngress");
        Check<ProbeForwardedRedirectQuery>(40, "ProbeForwardedRedirectQuery");
        Check<ProbeForwardedRedirect>(88, "ProbeForwardedRedirect");

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
