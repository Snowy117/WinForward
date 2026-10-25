using System.Runtime.InteropServices;

namespace WinForward.Userland.Policy;

/// <summary>
/// 远端端口的闭区间，<see cref="Any"/> 为 0..65535 —— 与 rule_blob.h 的「任意端口」哨兵一致。
/// </summary>
/// <remarks>
/// <para>
/// 构造函数不校验起点不大于终点：blob 的取值校验统一放在 <see cref="RuleBlobCompiler"/>，
/// 与内核侧 try_open_blob「一次校验完」的风格对齐，端口区间颠倒这条拒绝路径因此是可测的。
/// </para>
/// <para>
/// 显式 <see cref="LayoutKind.Sequential"/>：公开结构体可能被反射读取，
/// <see cref="LayoutKind.Auto"/> 会让 <c>Marshal.SizeOf</c> / <c>Marshal.OffsetOf</c> 抛错，
/// 而 Sequential 与 C# 结构体的默认布局一致，4 字节的闭区间形状不变。
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct PortRange(ushort begin, ushort end)
{
    public ushort Begin { get; } = begin;

    public ushort End { get; } = end;

    public static PortRange Any => new(0, ushort.MaxValue);

    public static PortRange Single(ushort port) => new(port, port);

    public bool IsAny => Begin == 0 && End == ushort.MaxValue;

    public override string ToString() => $"{Begin}..{End}";
}
