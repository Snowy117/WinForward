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
public interface IUdpAdapterTargetSource
{
    /// <summary>The fallback target for host flows without a resolvable origin adapter; null when the scope is empty.</summary>
    UdpAdapterTarget? Host { get; }

    /// <summary>Resolves an adapter stable ID to its current reinjection target; null when the adapter is not currently enumerated.</summary>
    UdpAdapterTarget? Resolve(string stableId);
}

/// <summary>
/// Thread-safe mutable <see cref="IUdpAdapterTargetSource"/>: a single immutable snapshot record
/// (host fallback + stable-ID map, ordinal-ignore-case) swapped wholesale via
/// <see cref="Volatile.Write"/> by the capture refresh runner. The constructor and
/// <see cref="Update"/> copy the supplied map into the snapshot so later caller-side mutation
/// can never leak into a live view; reads allocate nothing.
/// </summary>
public sealed class UdpAdapterTargetSource : IUdpAdapterTargetSource
{
    private sealed record Snapshot(UdpAdapterTarget? Host, IReadOnlyDictionary<string, UdpAdapterTarget> ByStableId);

    private Snapshot _snapshot;

    public UdpAdapterTargetSource(UdpAdapterTarget? host = null, IReadOnlyDictionary<string, UdpAdapterTarget>? byStableId = null)
    {
        ValidateHostMac(host);
        _snapshot = new Snapshot(host, CopyMap(byStableId));
    }

    public UdpAdapterTarget? Host => Volatile.Read(ref _snapshot).Host;

    public UdpAdapterTarget? Resolve(string stableId) => Volatile.Read(ref _snapshot).ByStableId.TryGetValue(stableId, out var target) ? target : null;

    /// <summary>Replaces the whole snapshot (host fallback + per-adapter map) atomically.</summary>
    public void Update(UdpAdapterTarget? host, IReadOnlyDictionary<string, UdpAdapterTarget> byStableId)
    {
        ArgumentNullException.ThrowIfNull(byStableId);
        ValidateHostMac(host);
        Volatile.Write(ref _snapshot, new Snapshot(host, CopyMap(byStableId)));
    }

    private static IReadOnlyDictionary<string, UdpAdapterTarget> CopyMap(IReadOnlyDictionary<string, UdpAdapterTarget>? byStableId) =>
        byStableId is null
            ? new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, UdpAdapterTarget>(byStableId, StringComparer.OrdinalIgnoreCase);

    private static void ValidateHostMac(UdpAdapterTarget? host)
    {
        if (host is { } target && target.Mac.Length != 6)
            throw new ArgumentOutOfRangeException(nameof(host), "The host adapter MAC must be exactly 6 bytes.");
    }
}
