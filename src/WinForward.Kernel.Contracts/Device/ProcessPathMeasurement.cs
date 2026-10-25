using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// 诊断接口 <c>ioctl_measure_process_path</c> 的输出：520 字节，Pack = 4。
/// <see cref="Utf8Length"/> 为 0 表示「整条不匹配」（超过 511 字节或含非法代理项）。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ProcessPathMeasurement
{
    public uint Utf8Length;
    public uint Reserved;
    public Utf8Buffer Utf8;

    [InlineArray(512)]
    public struct Utf8Buffer
    {
        private byte _element0;
    }
}
