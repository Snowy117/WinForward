namespace WinForward.Userland.Policy;

/// <summary>
/// blob v2 的常量，数值全部照抄 <c>src/common/rule_blob.h</c>。这里的任何一个数字变动都要求
/// 同步提升版本号，并同时改 C++ 侧的 static_assert 与 C# 侧的布局测试。
/// </summary>
/// <remarks>
/// v2 的规则选择器全部是「下标 + 数量」：进程选择器下标指向 8 字节的 selector 池，
/// 网段下标指向 20 字节的网段记录，端口下标指向 4 字节的端口记录。数量为 0 表示任意，
/// 此时对应的 first 必须为 0（规范化编码）。
/// </remarks>
internal static class RuleBlobLayout
{
    internal const uint Magic = 0x31425257U;
    internal const ushort Version = 2;
    internal const ushort HeaderSize = 56;
    internal const int RuleSize = 32;
    internal const int ProcessSelectorSize = 8;
    internal const int PortRangeSize = 4;
    internal const int NetworkPrefixSize = 20;
    internal const int NetworkAddressSize = 16;
    internal const int SegmentAlignment = 4;

    /// <summary>
    /// 「未命中任何规则」的规则序号哨兵，只用于控制面统计；v2 的规则字段不再使用索引哨兵。
    /// </summary>
    internal const uint PoolIndexAny = 0xFFFFFFFFU;

    // protocol_mask / family_mask 的位。0 表示任意；两个位都置位表示「两者都行」。
    internal const byte ProtocolTcpBit = 0x01;
    internal const byte ProtocolUdpBit = 0x02;
    internal const byte FamilyIpv4Bit = 0x01;
    internal const byte FamilyIpv6Bit = 0x02;

    internal static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
