using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary><c>probe_stats</c> 的逐字段镜像：264 字节，Pack = 8。计数由驱动无锁累加，这里是 IOCTL 回读的快照。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ProbeStats
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
