namespace WinForward.Core;

/// <summary>
/// The immutable identity triple of one interned capture-scope adapter: the slot a
/// <see cref="FlowKey"/> stores, the GUID-primary stable ID the adapter is interned on, the friendly
/// name logs and policy use, and the generation the owning enumeration published through
/// <see cref="AdapterSlotTable.TryIntern"/>.
/// A refresh publishes a replacement instance rather than mutating this one, so a reader that
/// already holds it keeps a coherent triple.
/// </summary>
public sealed record AdapterMetadata(ushort Slot, string StableId, string FriendlyName, long Generation);

/// <summary>
/// The process-lived interning table that lets <see cref="FlowKey"/> carry an adapter without
/// carrying a string: a capture-scope adapter is interned once per distinct stable ID and the key
/// stores the resulting slot. The table is created once at composition (the durable bundle), so it
/// outlives every capture generation and an adapter-list refresh cannot invalidate a key.
/// <para>
/// Slots are allocated monotonically and never reused, which is what makes the interning ABA-safe:
/// a retired adapter's slot is never reissued, so an old key can never compare equal to a key for a
/// different adapter. The cost is a ceiling of 65,535 interning identities per process (slot 0 is
/// <see cref="NoSlot"/>); the generation is deliberately <em>not</em> part of the interned identity
/// — it stays a <see cref="FlowKey"/> field compared by equality — so the table is bounded by the
/// number of distinct adapters this process has ever seen, not by adapters × refreshes.
/// </para>
/// <para>
/// Interning and refreshing run on cold edges (once per adapter per enumeration) under a gate; the
/// read side (<see cref="TryResolve"/>) is lock-free: a grow-only array reference read with
/// <c>Volatile.Read</c> plus one element read.
/// </para>
/// </summary>
public sealed class AdapterSlotTable
{
    /// <summary>The adapter-less slot: the UDP relay-alias key and adapter-less test keys.</summary>
    public const ushort NoSlot = 0;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ushort> _slotsByStableId = new(StringComparer.Ordinal);
    private AdapterMetadata?[] _slots = new AdapterMetadata?[16];
    private int _nextSlot = NoSlot + 1;

    /// <summary>The number of interned adapters (diagnostics only).</summary>
    public int CountForDiagnostics
    {
        get
        {
            lock (_gate) return _nextSlot - 1;
        }
    }

    /// <summary>Whether the slot space is exhausted, so the next <see cref="TryIntern"/> would refuse (diagnostics only).</summary>
    public bool ExhaustedForDiagnostics
    {
        get
        {
            lock (_gate) return _nextSlot > ushort.MaxValue;
        }
    }

    /// <summary>
    /// Interns <paramref name="stableId"/> or refreshes it when it is already interned: the slot is
    /// stable for the process lifetime, and the generation and friendly name are republished. Returns
    /// <see langword="false"/> when the slot space is exhausted — the caller must then refuse the
    /// adapter instead of keying it, so no captured packet ever carries <see cref="NoSlot"/> for a
    /// real adapter.
    /// </summary>
    public bool TryIntern(string stableId, long generation, string? friendlyName, out ushort slot)
    {
        ArgumentException.ThrowIfNullOrEmpty(stableId);
        lock (_gate)
        {
            if (_slotsByStableId.TryGetValue(stableId, out slot))
            {
                PublishUnderGate(slot, new AdapterMetadata(slot, stableId, friendlyName ?? stableId, generation));
                return true;
            }

            if (_nextSlot > ushort.MaxValue)
            {
                slot = NoSlot;
                return false;
            }

            slot = (ushort)_nextSlot++;
            _slotsByStableId.Add(stableId, slot);
            PublishUnderGate(slot, new AdapterMetadata(slot, stableId, friendlyName ?? stableId, generation));
            return true;
        }
    }

    /// <summary>Resolves an already-interned stable ID to its slot without creating one (cold edges and tests).</summary>
    public bool TryGetSlot(string stableId, out ushort slot)
    {
        ArgumentException.ThrowIfNullOrEmpty(stableId);
        lock (_gate) return _slotsByStableId.TryGetValue(stableId, out slot);
    }

    /// <summary>Resolves a slot to its published metadata; false for <see cref="NoSlot"/> and for a slot that was never interned.</summary>
    public bool TryResolve(ushort slot, out AdapterMetadata? metadata)
    {
        metadata = slot == NoSlot ? null : ResolveUnderGate(slot);
        return metadata is not null;
    }

    private AdapterMetadata? ResolveUnderGate(ushort slot)
    {
        var slots = Volatile.Read(ref _slots);
        return slot < slots.Length ? Volatile.Read(ref slots[slot]) : null;
    }

    private void PublishUnderGate(ushort slot, AdapterMetadata metadata)
    {
        var slots = Volatile.Read(ref _slots);
        if (slot >= slots.Length)
        {
            var grown = new AdapterMetadata?[Math.Max(slot + 1, slots.Length * 2)];
            slots.CopyTo(grown, 0);
            Volatile.Write(ref _slots, grown);
            slots = grown;
        }

        Volatile.Write(ref slots[slot], metadata);
    }
}
