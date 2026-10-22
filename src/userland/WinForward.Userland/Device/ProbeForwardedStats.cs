using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// <c>probe_forwarded_stats</c> 的逐字段镜像：152 字节，Pack = 8。
/// 恒等式（对任意 reset 之后的增量成立）：
/// <c>classify_calls == no_write_right + inbound_frames + outbound_frames</c>；
/// 帧去向 = self_injected + no_table + malformed + non_ip + permitted + blocked。
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
    public uint LastStatus;
    public uint AbiVersion;
}
