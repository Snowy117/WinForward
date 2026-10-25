using System.Text;

namespace WinForward.Userland.Policy;

/// <summary>
/// 只把 ASCII <c>A</c>..<c>Z</c> 折叠成小写，其余字符原样保留。
/// 与 matcher.h 的 <c>ascii_lower</c> 逐字节对齐，UTF-8 多字节序列因此不受影响。
/// </summary>
internal static class AsciiLowercaseNormalizer
{
    internal static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder builder = new(text.Length);
        foreach (char character in text)
        {
            builder.Append(character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character);
        }

        return builder.ToString();
    }
}
