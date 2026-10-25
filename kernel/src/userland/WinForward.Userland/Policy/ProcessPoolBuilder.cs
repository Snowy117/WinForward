using System.Text;

namespace WinForward.Userland.Policy;

internal sealed class ProcessPoolBuilder
{
    private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal);
    private readonly List<byte> _bytes = new();

    /// <summary>
    /// 按归一化后的串去重、按首次出现顺序追加，返回进程池内的字节偏移（串首）。
    /// </summary>
    internal uint Add(string normalizedText)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalizedText);

        if (_offsets.TryGetValue(normalizedText, out uint existing))
        {
            return existing;
        }

        uint offset = (uint)_bytes.Count;
        _offsets.Add(normalizedText, offset);
        _bytes.AddRange(Encoding.UTF8.GetBytes(normalizedText));
        _bytes.Add(0);
        return offset;
    }

    internal byte[] ToBytes() => _bytes.ToArray();
}
