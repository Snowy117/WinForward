using System.Runtime.InteropServices;

namespace WinForward.Userland.Policy;

/// <summary>
/// rule_blob.h 中 <c>port_range</c> 的逐字段镜像：4 字节，Pack = 4。
/// 闭区间，<see cref="Start"/> ≤ <see cref="End"/>；每条记录已经按规则内排序合并。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct RuleBlobPortRange
{
    public ushort Start;
    public ushort End;
}
