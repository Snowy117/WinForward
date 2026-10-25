using System.Runtime.InteropServices;

namespace WinForward.Userland.Policy;

/// <summary>
/// rule_blob.h 中 <c>process_selector</c> 的逐字段镜像：8 字节，Pack = 4。
/// <see cref="StringOffset"/> 是进程串池的字节偏移；串以 NUL 结尾、UTF-8、1..511 字节。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct RuleBlobProcessSelector
{
    internal const byte KindFileName = 0;
    internal const byte KindPath = 1;

    public uint StringOffset;
    public byte Kind;
    public byte IsDirectory;
    public byte Reserved0;
    public byte Reserved1;
}
