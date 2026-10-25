using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// <c>probe_forwarded_stats</c> 的逐字段镜像：456 字节，Pack = 8；v6 的 400 字节前缀不变，
/// v7 在尾部追加 7 个 u64（回程歧义 / 未配置入口 / 多 L 选择 / UDP 混合 NBL 丢弃）。
/// 恒等式（对任意 reset 之后的增量成立）：
/// <c>classify_calls == no_write_right + inbound_frames + outbound_frames</c>；
/// 帧去向 = self_injected + no_table + malformed + non_ip + permitted + blocked + redirected；
/// <c>matched == matched_permit + matched_block + matched_redirect</c>；
/// <c>matched_redirect + udp_fragmented == redirect_ingress + redirect_blocked + udp_ingress + udp_blocked</c>；
/// <c>permitted == matched_permit + fallback_permit + udp_return_miss</c>；
/// <c>blocked</c> 另含 <c>redirect_return_ambiguous</c> / <c>udp_return_ambiguous</c>。
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

    // B5：UDP 数据面计数（尾部追加；redirect_active/high_water/last_status/abi_version 后移）。
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

    // v7 追加（v6 的 400 字节前缀不变）：per-ingress/多 L 的失败与歧义计数。
    public ulong RedirectReturnAmbiguous;
    public ulong UdpReturnAmbiguous;
    public ulong RedirectNoIngress;
    public ulong UdpNoIngress;
    public ulong RedirectMultiL;
    public ulong UdpMultiL;

    // v7 追加诊断：UDP 回程混合 NBL 中未命中的候选帧（随原 NBL 一起 block，不能单帧 permit）。
    public ulong UdpReturnMissBlocked;
}
