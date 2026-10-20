using System.Text;

namespace WinForward.Userland.Policy;

/// <summary>
/// 只把 ASCII <c>A</c>..<c>Z</c> 折叠成小写，其余字节原样保留。
/// 与 matcher.h 的 <c>ascii_lower</c> 逐字节对齐，UTF-8 多字节序列因此不受影响。
/// </summary>
public sealed class AsciiLowercaseNormalizer : IProcessPathNormalizer
{
    private AsciiLowercaseNormalizer()
    {
    }

    public static AsciiLowercaseNormalizer Instance { get; } = new();

    public string? Normalize(string path)
    {
        StringBuilder builder = new(path.Length);
        foreach (char character in path)
        {
            builder.Append(character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character);
        }

        return builder.ToString();
    }
}
