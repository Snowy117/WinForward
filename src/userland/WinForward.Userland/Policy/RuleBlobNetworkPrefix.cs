using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinForward.Userland.Policy;

/// <summary>
/// rule_blob.h 中 <c>network_prefix</c> 的逐字段镜像：20 字节，Pack = 4。
/// <c>reserved[2]</c> 拆成两个字节字段，偏移与 C++ 侧一致（2 与 3），两者都必须为 0。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct RuleBlobNetworkPrefix
{
    public byte PrefixLength;
    public byte Family;
    public byte Reserved0;
    public byte Reserved1;
    public NetworkAddress Network;
}

/// <summary>16 字节定长地址；用内联数组而不是 fixed 缓冲，免得整个工程打开 unsafe。</summary>
[InlineArray(RuleBlobLayout.NetworkAddressSize)]
internal struct NetworkAddress
{
    private byte _element0;
}
