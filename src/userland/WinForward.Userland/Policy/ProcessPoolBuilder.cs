using System.Text;

namespace WinForward.Userland.Policy;

internal sealed class ProcessPoolBuilder
{
    private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal);
    private readonly List<byte> _bytes = new();

    internal uint Add(string? normalizedPath)
    {
        if (normalizedPath is null)
        {
            return RuleBlobLayout.PoolIndexAny;
        }

        if (_offsets.TryGetValue(normalizedPath, out uint existing))
        {
            return existing;
        }

        uint offset = (uint)_bytes.Count;
        _offsets.Add(normalizedPath, offset);
        _bytes.AddRange(Encoding.UTF8.GetBytes(normalizedPath));
        _bytes.Add(0);
        return offset;
    }

    internal byte[] ToBytes() => _bytes.ToArray();
}
