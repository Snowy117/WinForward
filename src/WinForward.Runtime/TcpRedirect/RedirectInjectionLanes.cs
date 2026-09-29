using System.Collections.Concurrent;
using System.Diagnostics;
using WinForward.NdisApi;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The redirect data legs' deferred-injection lanes (R2): one lane per (adapter handle, target
/// direction) accumulates a pump iteration's rewritten frames and hands them to one batched
/// reinjection call at that iteration's flush. Mechanics mirror the proven pass lanes
/// (<c>NdisPacketActionExecutor.PendingPassLane</c>): a lock-free linear scan of the
/// volatile-published entry array once a lane exists, lane creation under one small lock,
/// doubling growth with a cap, and parallel per-frame arrays (buffer, rented flag, association)
/// in append (= capture) order. A refused append tells the caller to send that frame immediately;
/// the container counts the refusal in <see cref="OverflowCount"/>.
/// <para>
/// Unlike the pass lane table, entries are recycled every iteration: the flush takes a lane,
/// <see cref="Release"/> frees its slot and returns the lane object — with its per-frame arrays —
/// to a spare pool, so no adapter-handle entry can outlive the iteration that created it, no
/// scope-install retirement hook is needed, and steady-state lane creation allocates nothing.
/// Serialization is the pump's, not this container's: every append and every flush of one key runs
/// on that adapter's strictly-ordered pump handler chain (windows-ndisapi.md, "Batched reinjection
/// sends" — serialization rests on the pump's await chain, not thread identity), so a lane needs no
/// lock of its own, and a caller outside that chain must not append at all.
/// </para>
/// </summary>
internal sealed class RedirectInjectionLanes
{
    private const int InitialLaneCapacity = 8;
    private const int MaxLanes = 64;
    private const int InitialLaneFrames = 8;

    // One batched send request spans at most NdisApiDriver.MaxPacketsPerSendRequest (126) packets and
    // the driver throws at the first rejected chunk, so a lane must never be wider than one chunk: a
    // mid-batch failure would otherwise leave earlier chunks delivered while the degraded per-frame
    // retry re-sends them. The linkage is asserted by TcpRedirectInjectionBatchingTests.
    private const int MaxFramesPerLane = 126;

    /// <summary>The lane-table cap, exposed so the overflow warn can report the bound it hit.</summary>
    internal static int LaneTableCapacity => MaxLanes;

    /// <summary>The per-lane frame cap, exposed so the overflow warn can report the bound it hit.</summary>
    internal static int LaneFrameCapacity => MaxFramesPerLane;

    private readonly Lock _creationLock = new();
    private readonly ConcurrentQueue<Lane> _spareLanes = new();
    private Lane?[] _lanes = new Lane?[InitialLaneCapacity];
    private long _overflowCount;

    /// <summary>
    /// One taken lane as the flush sees it: the parallel arrays, the taken frame count, and the
    /// entry slot the lane occupied (so <see cref="Release"/> frees exactly that entry).
    /// </summary>
    internal readonly record struct View(
        NdisPacketBuffer[] Buffers,
        bool[] Rented,
        TcpRedirectAssociation[] Associations,
        int Count,
        int Slot);

    /// <summary>
    /// Appends one rewritten, already-staged frame to its (adapter handle, target direction) lane.
    /// Returns false when the lane table is at its cap or the lane is at its per-lane cap — the
    /// caller then sends that frame as an immediate single send (counted here, warned by the
    /// caller). Must be called on the owning adapter's pump chain only.
    /// </summary>
    internal bool TryAppend(nint adapterHandle, bool towardMstcp, NdisPacketBuffer buffer, bool rented, TcpRedirectAssociation association)
    {
        var lanes = Volatile.Read(ref _lanes);
        for (var index = 0; index < lanes.Length; index++)
        {
            if (MatchLane(lanes, index, adapterHandle, towardMstcp) is { } existing) return Append(existing, buffer, rented, association);
        }

        lock (_creationLock)
        {
            // Re-read inside the lock: a concurrent creation may have grown or filled the table
            // while this caller was waiting, and lane creation must land in the current table.
            lanes = Volatile.Read(ref _lanes);
            var freeIndex = -1;
            for (var index = 0; index < lanes.Length; index++)
            {
                if (MatchLane(lanes, index, adapterHandle, towardMstcp) is { } existing) return Append(existing, buffer, rented, association);
                if (freeIndex < 0 && Volatile.Read(ref lanes[index]) is null) freeIndex = index;
            }

            if (freeIndex < 0)
            {
                var currentLength = lanes.Length;
                if (currentLength >= MaxLanes)
                {
                    Interlocked.Increment(ref _overflowCount);
                    return false;
                }
                // Cold path: the table doubles until the cap. Existing lanes migrate by reference
                // into the fresh array (a lock-free scanner may keep using the old array, where the
                // same lane objects are still published), and the new slots start free.
                var grown = new Lane?[Math.Min(currentLength * 2, MaxLanes)];
                Array.Copy(lanes, grown, currentLength);
                Volatile.Write(ref _lanes, grown);
                lanes = grown;
                freeIndex = currentLength;
            }

            var lane = _spareLanes.TryDequeue(out var spare) ? spare : new Lane();
            lane.AdapterHandle = adapterHandle;
            lane.TowardMstcp = towardMstcp;
            Volatile.Write(ref lanes[freeIndex], lane);
            return Append(lane, buffer, rented, association);
        }
    }

    /// <summary>
    /// Takes the frames accumulated for one key without sending or releasing them: the caller
    /// injects the batch and then calls <see cref="Release"/> in a <see langword="finally"/>.
    /// Returns false when the key has no lane (nothing to flush).
    /// </summary>
    internal bool TryTake(nint adapterHandle, bool towardMstcp, out View view)
    {
        var lanes = Volatile.Read(ref _lanes);
        for (var index = 0; index < lanes.Length; index++)
        {
            if (MatchLane(lanes, index, adapterHandle, towardMstcp) is { } lane)
            {
                view = new View(lane.Buffers, lane.Rented, lane.Associations, lane.Count, index);
                return true;
            }
        }
        view = default;
        return false;
    }

    /// <summary>
    /// Releases a taken lane exactly once: rented pooled buffers return to the pool, every frame
    /// reference is cleared, and the entry is freed so no adapter-handle entry outlives its
    /// iteration. The freed lane — its per-frame arrays included — joins the spare pool, so the
    /// next iteration's lane for any key is created without allocating. The references are cleared
    /// before the slot is freed, and the slot is only nulled while it still holds this lane's
    /// arrays, so a slot reused by a later lane can never observe this lane's buffers.
    /// </summary>
    internal void Release(in View view)
    {
        for (var index = 0; index < view.Count; index++)
        {
            if (view.Rented[index]) view.Buffers[index].Dispose();
            view.Buffers[index] = null!;
            view.Associations[index] = null!;
        }

        var lanes = Volatile.Read(ref _lanes);
        if ((uint)view.Slot < (uint)lanes.Length
            && Volatile.Read(ref lanes[view.Slot]) is { } lane
            && ReferenceEquals(lane.Buffers, view.Buffers))
        {
            Volatile.Write(ref lanes[view.Slot], null);
            lane.Count = 0;
            _spareLanes.Enqueue(lane);
        }
    }

    /// <summary>Telemetry: frames currently waiting for a flush across every lane.</summary>
    internal int PendingCount
    {
        get
        {
            var lanes = Volatile.Read(ref _lanes);
            var total = 0;
            for (var index = 0; index < lanes.Length; index++)
            {
                if (Volatile.Read(ref lanes[index]) is { } lane) total += lane.Count;
            }
            return total;
        }
    }

    /// <summary>
    /// Telemetry: frames forced onto the immediate single-send path because the lane table or a
    /// lane hit its cap — the redirect analogue of the pass path's
    /// <c>ImmediateSendLaneOverflowCount</c>. A degradation is never silent: the caller warns
    /// rate-limited alongside this counter.
    /// </summary>
    internal long OverflowCount => Interlocked.Read(ref _overflowCount);

    /// <summary>
    /// Debug-only guard for the drain contract (the redirect analogue of
    /// <c>NdisPacketActionExecutor.DebugAssertNoPendingPasses</c>): every lane must have been
    /// flushed and released by its iteration-end flush, so a lane — hence frames that would never
    /// be sent and rented buffers that would never return — is caught in debug builds and tests.
    /// Called between generations from the bundle's scope-installed path.
    /// </summary>
    [Conditional("DEBUG")]
    internal void DebugAssertNoPending()
    {
        var lanes = Volatile.Read(ref _lanes);
        for (var index = 0; index < lanes.Length; index++)
        {
            Debug.Assert(
                Volatile.Read(ref lanes[index]) is null,
                $"A redirect injection lane (Entry {index}) outlived its pump iteration; every iteration must flush and release its lanes (batched redirect injection contract).");
        }
    }

    private static Lane? MatchLane(Lane?[] lanes, int index, nint adapterHandle, bool towardMstcp) =>
        Volatile.Read(ref lanes[index]) is { } lane && lane.AdapterHandle == adapterHandle && lane.TowardMstcp == towardMstcp ? lane : null;

    private bool Append(Lane lane, NdisPacketBuffer buffer, bool rented, TcpRedirectAssociation association)
    {
        var count = lane.Count;
        if (count == lane.Buffers.Length)
        {
            if (count >= MaxFramesPerLane)
            {
                // Deeper than one pump batch by an order of magnitude: the iteration-end flush was
                // missed. Refuse instead of growing without bound; the frame still goes out.
                Interlocked.Increment(ref _overflowCount);
                return false;
            }
            var grown = Math.Min(count * 2, MaxFramesPerLane);
            Array.Resize(ref lane.Buffers, grown);
            Array.Resize(ref lane.Rented, grown);
            Array.Resize(ref lane.Associations, grown);
        }
        lane.Buffers[count] = buffer;
        lane.Rented[count] = rented;
        lane.Associations[count] = association;
        lane.Count = count + 1;
        return true;
    }

    /// <summary>
    /// One (adapter handle, target direction) accumulation lane: staged frames in append order,
    /// each flagged for whether the flush must return it to the buffer pool (a pooled copy) or
    /// leave it alone (a frame the pump owns, such as a capture slot), plus the association each
    /// frame must be attributed to if its send fails after the batch. The key fields are stamped at
    /// creation because a released lane is reused for the next key, and its arrays are kept (a lane
    /// that had grown keeps its grown arrays), so steady-state lane creation allocates nothing.
    /// </summary>
    private sealed class Lane
    {
        public nint AdapterHandle;
        public bool TowardMstcp;
        public NdisPacketBuffer[] Buffers = new NdisPacketBuffer[InitialLaneFrames];
        public bool[] Rented = new bool[InitialLaneFrames];
        public TcpRedirectAssociation[] Associations = new TcpRedirectAssociation[InitialLaneFrames];
        public int Count;
    }
}
