using System.Runtime.InteropServices;

namespace WinForward.Userland.Policy;

/// <summary>rule_blob.h 中 <c>rule</c> 的逐字段镜像：16 字节，Pack = 4。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct RuleBlobRule
{
    public uint ProcessIndex;
    public uint NetworkIndex;
    public ushort RemotePortStart;
    public ushort RemotePortEnd;
    public byte Protocol;
    public byte Family;
    public byte Action;
    public byte Reserved;
}
