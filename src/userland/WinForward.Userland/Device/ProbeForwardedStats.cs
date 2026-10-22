using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// <c>probe_forwarded_stats</c> 的逐字段镜像：296 字节，Pack = 8。
/// 恒等式（对任意 reset 之后的增量成立）：
/// <c>classify_calls == no_write_right + inbound_frames + outbound_frames</c>；
/// 帧去向 = self_injected + no_table + malformed + non_ip + permitted + blocked + redirected；
/// <c>matched == matched_permit + matched_block + matched_redirect</c>；
/// <c>matched_redirect == redirect_ingress + redirect_blocked</c>。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ProbeForwardedStats
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

    // v5 在 stats 尾部追加 redirect 计数；v4 的 0–143 字段偏移不变，LastStatus / AbiVersion 后移。
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
    public uint RedirectActive;
    public uint RedirectHighWater;
    public uint LastStatus;
    public uint AbiVersion;
}
