using System.Text;

namespace WinForward.Userland.Policy;

/// <summary>
/// 进程选择器：调用方写的镜像路径。写进 blob 的字符串由 <see cref="IProcessPathNormalizer"/> 决定，
/// 这里只保证原始串能放进 NUL 结尾的进程池。
/// </summary>
/// <remarks>
/// 控制字符与 NUL 一律拒绝：NUL 是池内分隔符，控制字符在 Windows 路径里也不可能出现。
/// 非 ASCII 允许（按 UTF-8 落盘）；大小写折叠与「DOS 路径 / 设备路径」这类表示转换属于归一化步骤。
/// </remarks>
public sealed class ProcessSelector
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private ProcessSelector(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static ProcessSelector Parse(string path)
    {
        EnsureRepresentable(path);
        return new ProcessSelector(path);
    }

    /// <summary>进程池只能装 UTF-8 字节串：拒绝空串、控制字符、NUL 与非法 UTF-16。</summary>
    internal static void EnsureRepresentable(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        foreach (char character in path)
        {
            if (character < ' ' || character == '\u007F')
            {
                throw new ArgumentException(
                    $"进程路径含控制字符 U+{(int)character:X4}，池内无法表示：{path}", nameof(path));
            }
        }

        // 孤立代理项会在这里抛 EncoderFallbackException（它继承 ArgumentException）。
        _ = StrictUtf8.GetByteCount(path);
    }

    public override string ToString() => Path;
}
