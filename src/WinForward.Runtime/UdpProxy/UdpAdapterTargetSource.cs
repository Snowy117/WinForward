namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// A reinjection target for a UDP response: the NDISAPI enumeration handle of the adapter the
/// response is injected toward and the MAC to use when rebuilding the Ethernet header. Keyed by
/// adapter stable ID in <see cref="IUdpAdapterTargetSource"/> so every flow can route responses
/// toward the adapter on which it was captured; enumeration handles are runtime state, so the
/// whole map is refreshed when the driver re-enumerates its bound-adapter list.
/// </summary>
public readonly record struct UdpAdapterTarget(nint Handle, byte[] Mac);

/// <summary>
/// The refreshable source of UDP reinjection targets, consulted per response by
/// <see cref="UdpResponseReinjector"/>. <see cref="Host"/> is the capture scope's fallback
/// adapter (null when the scope is empty); <see cref="Resolve"/> maps a flow's origin adapter
/// stable ID to its current target (null when the adapter is no longer enumerated). Lookups are
/// lock-free dictionary probes; a refresh swaps one immutable snapshot so a response never
/// observes a half-updated map.
/// </summary>
/// <remarks>
/// A composition-only seam: <see cref="UdpAdapterTargetSource"/> is the single production
/// implementation, and no substituting fake exists — the real source is directly exercisable
/// (the relay tests build it and drive responses through it), so a fake would not exercise any
/// scenario the production implementation does not already cover.
/// </remarks>
public interface IUdpAdapterTargetSource
{
    /// <summary>The fallback target for host flows without a resolvable origin adapter; null when the scope is empty.</summary>
    UdpAdapterTarget? Host { get; }

    /// <summary>Resolves an adapter stable ID to its current reinjection target; null when the adapter is not currently enumerated.</summary>
    UdpAdapterTarget? Resolve(string stableId);

    /// <summary>
    /// The stable IDs currently present in the map (sorted for deterministic output), so a
    /// diagnostic event can summarize which adapters the reinjection map still knows about.
    /// </summary>
    IReadOnlyCollection<string> AdapterIds { get; }
}

/// <summary>
/// Thread-safe mutable <see cref="IUdpAdapterTargetSource"/>: a single immutable snapshot record
/// (host fallback + stable-ID map, ordinal-ignore-case) swapped wholesale via
/// <see cref="Volatile.Write"/> by the capture refresh runner. The constructor and
/// <see cref="Update"/> copy the supplied map into the snapshot so later caller-side mutation
/// can never leak into a live view; reads allocate nothing.
/// </summary>
public sealed class UdpAdapterTargetSource(UdpAdapterTarget? host = null, IReadOnlyDictionary<string, UdpAdapterTarget>? byStableId = null) : IUdpAdapterTargetSource
{
    private sealed record Snapshot(UdpAdapterTarget? Host, IReadOnlyDictionary<string, UdpAdapterTarget> ByStableId, IReadOnlyList<string> SortedAdapterIds);

    private Snapshot _snapshot = CreateSnapshot(host, CopyMap(byStableId));

    public UdpAdapterTarget? Host => Volatile.Read(ref _snapshot).Host;

    public UdpAdapterTarget? Resolve(string stableId) => Volatile.Read(ref _snapshot).ByStableId.TryGetValue(stableId, out var target) ? target : null;

    public IReadOnlyCollection<string> AdapterIds => Volatile.Read(ref _snapshot).SortedAdapterIds;

    /// <summary>Replaces the whole snapshot (host fallback + per-adapter map) atomically.</summary>
    public void Update(UdpAdapterTarget? host, IReadOnlyDictionary<string, UdpAdapterTarget> byStableId)
    {
        ArgumentNullException.ThrowIfNull(byStableId);
        ValidateHostMac(host);
        Volatile.Write(ref _snapshot, CreateSnapshot(host, CopyMap(byStableId)));
    }

    private static Snapshot CreateSnapshot(UdpAdapterTarget? host, IReadOnlyDictionary<string, UdpAdapterTarget> byStableId)
    {
        ValidateHostMac(host);
        var sortedIds = new string[byStableId.Count];
        var index = 0;
        foreach (var stableId in byStableId.Keys) sortedIds[index++] = stableId;
        Array.Sort(sortedIds, StringComparer.OrdinalIgnoreCase);
        return new Snapshot(host, byStableId, sortedIds);
    }

    private static Dictionary<string, UdpAdapterTarget> CopyMap(IReadOnlyDictionary<string, UdpAdapterTarget>? byStableId) =>
        byStableId is null
            ? new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, UdpAdapterTarget>(byStableId, StringComparer.OrdinalIgnoreCase);

    private static void ValidateHostMac(UdpAdapterTarget? host)
    {
        if (host is { } target && target.Mac.Length != 6)
            throw new ArgumentOutOfRangeException(nameof(host), "The host adapter MAC must be exactly 6 bytes.");
    }
}
