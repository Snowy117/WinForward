using System.Text;

namespace WinForward.Userland.Policy;

/// <summary>
/// 进程选择器：调用方写的名字或路径，以及它属于哪种匹配形态。
/// </summary>
/// <remarks>
/// 不含 <c>/</c> 或 <c>\</c> 的写法是文件名选择器，只比较连接路径的最后一段；
/// 含分隔符的写法是路径选择器，<see cref="Parse"/> 会把 <c>/</c> 归一成 <c>\</c>，
/// 真正的设备路径解析留给 <see cref="IProcessPathNormalizer"/>。两种形态都保留原始大小写，
/// 编译期统一折叠 ASCII。
/// 控制字符与 NUL 一律拒绝：NUL 是池内分隔符，控制字符在 Windows 路径里也不可能出现。
/// </remarks>
public sealed class ProcessSelector
{
    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private ProcessSelector(ProcessSelectorKind kind, string value)
    {
        if (kind == ProcessSelectorKind.FileName && (value.Contains('/') || value.Contains('\\')))
        {
            throw new ArgumentException($"文件名选择器不能包含路径分隔符：{value}", nameof(value));
        }

        Kind = kind;
        Value = value;
    }

    public ProcessSelectorKind Kind { get; }

    public string Value { get; }

    public static ProcessSelector Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var trimmed = text.Trim();
        EnsureRepresentable(trimmed);

        var normalized = trimmed.Replace('/', '\\');
        var kind = normalized.Contains('\\') ? ProcessSelectorKind.Path : ProcessSelectorKind.FileName;
        return new ProcessSelector(kind, normalized);
    }

    /// <summary>进程池只能装 UTF-8 字节串：拒绝空串、控制字符、NUL 与非法 UTF-16。</summary>
    internal static void EnsureRepresentable(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        foreach (var character in text)
        {
            // 与 rule_blob.h 的 is_control_code_point 一致：C0、DEL 与 C1 都在拒绝范围内。
            // ReSharper disable once MergeIntoPattern // 显式区间比较逐段对应 rule_blob.h 的 is_control_code_point；关系模式会让 Sonar S3267 误判这条失败即抛的扫描为 Where 过滤
            if (character < '\u0020' || (character >= '\u007F' && character <= '\u009F'))
            {
                throw new ArgumentException(
                    $"进程选择器含控制字符 U+{(int)character:X4}，池内无法表示：{text}", nameof(text));
            }
        }

        // 孤立代理项会在这里抛 EncoderFallbackException（它继承 ArgumentException）。
        _ = s_strictUtf8.GetByteCount(text);
    }

    public override string ToString() => Value;
}
