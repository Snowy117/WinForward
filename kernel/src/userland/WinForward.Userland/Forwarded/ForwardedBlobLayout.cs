namespace WinForward.Userland.Forwarded;

/// <summary>
/// forwarded blob v1 的常量，数值全部照抄 forwarded_blob.h。这里的任何一个数字变动都要求
/// 同步提升版本号，并同时改 C++ 侧的 static_assert 与 C# 侧的布局测试。
/// </summary>
internal static class ForwardedBlobLayout
{
    internal const uint Magic = 0x31424657U;
    internal const ushort Version = 1;
    internal const ushort HeaderSize = 40;
    internal const int RuleSize = 24;
    internal const int NetworkPrefixSize = 20;
    internal const int NetworkAddressSize = 16;
    internal const int SegmentAlignment = 4;
    internal const uint AdapterAny = 0xFFFFFFFFU;
    internal const uint UpstreamNone = 0xFFFFFFFFU;
    internal const uint PoolIndexAny = 0xFFFFFFFFU;

    internal static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
