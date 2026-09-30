using WinForward.Core;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// A reinjection target for a UDP response: the NDISAPI enumeration handle of the adapter the
/// response is injected toward and the MAC to use when rebuilding the Ethernet header. Keyed by the
/// adapter's interned slot in <see cref="IUdpAdapterTargetSource"/> so every flow can route responses
/// toward the adapter on which it was captured; enumeration handles are runtime state, so the whole
/// map is refreshed when the driver re-enumerates its bound-adapter list.
/// </summary>
public readonly record struct UdpAdapterTarget(nint Handle, byte[] Mac);

/// <summary>
/// The refreshable source of UDP reinjection targets, consulted per response by
/// <see cref="UdpResponseReinjector"/>. <see cref="Host"/> is the capture scope's fallback
/// adapter (null when the scope is empty); <see cref="Resolve"/> maps a flow's origin adapter slot
/// to its current target (null when the adapter is no longer enumerated). A refresh swaps one
/// immutable snapshot so a response never observes a half-updated map.
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

    /// <summary>Resolves an adapter slot to its current reinjection target; null when the slot is unregistered or the adapter is not currently enumerated.</summary>
    UdpAdapterTarget? Resolve(ushort slot);

    /// <summary>
    /// The stable IDs currently present in the map (sorted for deterministic output), so a
    /// diagnostic event can summarize which adapters the reinjection map still knows about.
    /// </summary>
    IReadOnlyCollection<string> AdapterIds { get; }
}

/// <summary>
/// Thread-safe mutable <see cref="IUdpAdapterTargetSource"/>: a single immutable snapshot record
/// (host fallback + a slot-indexed target array) swapped wholesale via <c>Volatile.Write</c> by the
/// capture refresh runner. The constructor and <see cref="Update"/> copy the supplied array so later
/// caller-side mutation can never leak into a live view; reads allocate nothing and take no lock —
/// one array index plus a null check.
/// </summary>
public sealed class UdpAdapterTargetSource : IUdpAdapterTargetSource
{
    private sealed record Snapshot(UdpAdapterTarget? Host, UdpAdapterTarget?[] BySlot, IReadOnlyList<string> SortedAdapterIds);

    private readonly AdapterSlotTable _slots;
    private Snapshot _snapshot;

    /// <summary>
    /// Creates the source over <paramref name="slots"/> (the same interning table the flow keys use)
    /// and seeds the first snapshot from <paramref name="bySlot"/>. The seed resolves its adapter IDs
    /// through that table, exactly like <see cref="Update"/> does, so a caller that supplies a map
    /// does not have to refresh before <see cref="AdapterIds"/> reports it.
    /// </summary>
    public UdpAdapterTargetSource(AdapterSlotTable? slots = null, UdpAdapterTarget? host = null, IReadOnlyDictionary<ushort, UdpAdapterTarget>? bySlot = null)
    {
        _slots = slots ?? new AdapterSlotTable();
        _snapshot = CreateSnapshot(_slots, host, bySlot ?? new Dictionary<ushort, UdpAdapterTarget>());
    }

    public UdpAdapterTarget? Host => Volatile.Read(ref _snapshot).Host;

    /// <summary>Resolves an adapter slot to its current reinjection target; null when the adapter is not currently enumerated.</summary>
    public UdpAdapterTarget? Resolve(ushort slot)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return slot < snapshot.BySlot.Length ? snapshot.BySlot[slot] : null;
    }

    public IReadOnlyCollection<string> AdapterIds => Volatile.Read(ref _snapshot).SortedAdapterIds;

    /// <summary>Replaces the whole snapshot (host fallback + per-slot targets) atomically.</summary>
    public void Update(UdpAdapterTarget? host, IReadOnlyDictionary<ushort, UdpAdapterTarget> bySlot)
    {
        ArgumentNullException.ThrowIfNull(bySlot);
        ValidateHostMac(host);
        Volatile.Write(ref _snapshot, CreateSnapshot(_slots, host, bySlot));
    }

    private static Snapshot CreateSnapshot(AdapterSlotTable slots, UdpAdapterTarget? host, IReadOnlyDictionary<ushort, UdpAdapterTarget> bySlot)
    {
        ValidateHostMac(host);
        var length = 1;
        foreach (var slot in bySlot.Keys)
        {
            length = Math.Max(length, slot + 1);
        }

        var targets = new UdpAdapterTarget?[length];
        var ids = new List<string>(bySlot.Count);
        foreach (var (slot, target) in bySlot)
        {
            PublishTarget(slots, targets, ids, slot, target);
        }

        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return new Snapshot(host, targets, ids);
    }

    private static void PublishTarget(AdapterSlotTable slots, UdpAdapterTarget?[] targets, List<string> ids, ushort slot, UdpAdapterTarget target)
    {
        if (slot == AdapterSlotTable.NoSlot) return;
        if (slot >= targets.Length) return;
        targets[slot] = target;
        if (slots.TryResolve(slot, out var metadata) && metadata is not null) ids.Add(metadata.StableId);
    }

    private static void ValidateHostMac(UdpAdapterTarget? host)
    {
        if (host is { } target && target.Mac.Length != 6)
            throw new ArgumentOutOfRangeException(nameof(host), "The host adapter MAC must be exactly 6 bytes.");
    }
}
