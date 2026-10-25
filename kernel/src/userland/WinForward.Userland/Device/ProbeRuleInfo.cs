using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// <c>probe_rule_info</c> 的逐字段镜像：32 字节，Pack = 4。
/// <see cref="Generation"/> 每次成功换表 +1，是「非法 blob 没有动旧表」的判据。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ProbeRuleInfo
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
