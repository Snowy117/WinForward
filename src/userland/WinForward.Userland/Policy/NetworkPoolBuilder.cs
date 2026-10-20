namespace WinForward.Userland.Policy;

internal sealed class NetworkPoolBuilder
{
    private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal);
    private readonly List<RuleBlobNetworkPrefix> _prefixes = new();

    internal uint Add(RemoteNetwork network)
    {
        string key = $"{(byte)network.Family}/{network.PrefixLength}/{Convert.ToHexString(network.Address)}";
        if (_offsets.TryGetValue(key, out uint existing))
        {
            return existing;
        }

        uint offset = (uint)(_prefixes.Count * RuleBlobLayout.NetworkPrefixSize);
        RuleBlobNetworkPrefix prefix = default;
        prefix.PrefixLength = (byte)network.PrefixLength;
        prefix.Family = (byte)network.Family;
        network.Address.CopyTo(prefix.Network);
        _prefixes.Add(prefix);
        _offsets.Add(key, offset);
        return offset;
    }

    internal RuleBlobNetworkPrefix[] ToArray() => _prefixes.ToArray();
}
