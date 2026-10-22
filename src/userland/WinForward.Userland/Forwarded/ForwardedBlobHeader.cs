using System.Runtime.InteropServices;

namespace WinForward.Userland.Forwarded;

/// <summary>
/// forwarded_blob.h 中 <c>forwarded_blob_header</c> 的逐字段镜像：40 字节，Pack = 4。
/// 字段顺序、偏移与 C++ 侧 static_assert 一一对应，改动即破坏性变更。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct ForwardedBlobHeader
{
    public uint Magic;
    public ushort Version;
    public ushort HeaderSize;
    public uint TotalSize;
    public uint RuleCount;
    public uint FallbackAction;
    public uint RulesOffset;
    public uint NetworkPoolOffset;
    public uint NetworkPoolSize;
    public uint Reserved;
    public uint Reserved2;
}
