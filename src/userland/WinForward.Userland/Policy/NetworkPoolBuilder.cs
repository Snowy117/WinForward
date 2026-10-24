namespace WinForward.Userland.Policy;

internal sealed class NetworkPoolBuilder
{
    private readonly Dictionary<string, uint>? _offsets;
    private readonly List<RuleBlobNetworkPrefix> _prefixes = new();

    /// <summary>
    /// <paramref name="deduplicate"/> 为 true 时按 (族, 前缀长度, 地址) 去重（forwarded blob 的约定）；
    /// host blob v2 按规则顺序追加，不做跨规则去重。
    /// </summary>
    internal NetworkPoolBuilder(bool deduplicate = true)
    {
        _offsets = deduplicate ? new Dictionary<string, uint>(StringComparer.Ordinal) : null;
    }

    internal uint Add(RemoteNetwork network)
    {
        if (_offsets is not null)
        {
            string key = $"{(byte)network.Family}/{network.PrefixLength}/{Convert.ToHexString(network.Address)}";
            if (_offsets.TryGetValue(key, out uint existing))
            {
                return existing;
            }

            uint offset = (uint)(_prefixes.Count * RuleBlobLayout.NetworkPrefixSize);
            _prefixes.Add(Create(network));
            _offsets.Add(key, offset);
            return offset;
        }

        uint appended = (uint)(_prefixes.Count * RuleBlobLayout.NetworkPrefixSize);
        _prefixes.Add(Create(network));
        return appended;
    }

    internal RuleBlobNetworkPrefix[] ToArray() => _prefixes.ToArray();

    internal int Count => _prefixes.Count;

    private static RuleBlobNetworkPrefix Create(RemoteNetwork network)
    {
        RuleBlobNetworkPrefix prefix = default;
        prefix.PrefixLength = (byte)network.PrefixLength;
        prefix.Family = (byte)network.Family;
        network.Address.CopyTo(prefix.Network);
        return prefix;
    }
}
