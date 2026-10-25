using System.Runtime.InteropServices;

namespace WinForward.Userland.Policy;

/// <summary>
/// rule_blob.h 中 <c>rule</c> 的逐字段镜像：32 字节，Pack = 4。
/// 三个 <c>*_first</c> 都是池内记录下标；对应 <c>*_count</c> 为 0 时 <c>*_first</c> 必须是 0。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct RuleBlobRule
{
    public uint ProcessFirst;
    public uint ProcessCount;
    public uint NetworkFirst;
    public uint NetworkCount;
    public uint PortFirst;
    public uint PortCount;
    public byte ProtocolMask;
    public byte FamilyMask;
    public byte Action;
    public byte Reserved;
    public uint Reserved2;
}
