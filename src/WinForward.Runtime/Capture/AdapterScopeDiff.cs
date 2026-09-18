namespace WinForward.Runtime.Capture;

/// <summary>
/// The result of diffing two capture scopes' link state by stable ID (task 09-07
/// adapter-refresh no-op skip): adapters entering scope, adapters leaving scope, and in-scope
/// adapters whose (handle, MAC, MTU, address fingerprint) changed. An empty diff means the fresh
/// enumeration is observably identical to the running generation's — a spurious signal that must
/// not touch the running pumps. Since task 09-17 the address fingerprint participates, so host
/// address changes (IPv6 temporary-address rotation) count as link-state changes even though the
/// NDISRD bound-adapter list never rebuilds.
/// </summary>
internal readonly record struct AdapterScopeDiff(
    IReadOnlyList<AdapterEnumerationItem> Added,
    IReadOnlyList<AdapterEnumerationItem> Removed,
    IReadOnlyList<AdapterEnumerationItem> Changed)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

internal static class AdapterEnumerationDiff
{
    /// <summary>Diffs two scope item lists by stable ID; any of handle/MAC/MTU/address fingerprint changing counts as changed.</summary>
    public static AdapterScopeDiff Diff(IReadOnlyList<AdapterEnumerationItem> current, IReadOnlyList<AdapterEnumerationItem> next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);

        var currentByStableId = new Dictionary<string, AdapterEnumerationItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in current) currentByStableId[item.StableId] = item;
        var nextByStableId = new Dictionary<string, AdapterEnumerationItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in next) nextByStableId[item.StableId] = item;

        var added = new List<AdapterEnumerationItem>();
        var changed = new List<AdapterEnumerationItem>();
        foreach (var item in next)
        {
            if (!currentByStableId.TryGetValue(item.StableId, out var previous)) added.Add(item);
            else if (!LinkStateEquals(previous, item)) changed.Add(item);
        }

        var removed = current.Where(item => !nextByStableId.ContainsKey(item.StableId)).ToList();

        return new AdapterScopeDiff(added, removed, changed);
    }

    /// <summary>Fingerprints are pre-normalized (invariant lower-case), so equality is plain ordinal.</summary>
    private static bool LinkStateEquals(AdapterEnumerationItem left, AdapterEnumerationItem right) =>
        left.Adapter.RuntimeHandle == right.Adapter.RuntimeHandle &&
        left.Mtu == right.Mtu &&
        string.Equals(left.AddressFingerprint, right.AddressFingerprint, StringComparison.Ordinal) &&
        left.Mac.AsSpan().SequenceEqual(right.Mac);
}
