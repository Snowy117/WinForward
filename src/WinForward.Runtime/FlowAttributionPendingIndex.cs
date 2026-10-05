using System.Runtime.InteropServices;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime;

/// <summary>What the pipeline should do with a packet that entered admission.</summary>
internal enum AttributionAdmission
{
    /// <summary>The packet was retained; the caller completes its lease as deferred and returns.</summary>
    Deferred,

    /// <summary>The flow resolved in the table while the pending gate was held; the caller continues its resolved path and must not touch the lease differently.</summary>
    Inline,

    /// <summary>The entry is failed; its packets fail closed and are blocked by the caller.</summary>
    BlockedFailed,

    /// <summary>The pipeline is sealed; the packet blocks fail-closed rather than being attributed inline.</summary>
    BlockedSealed,

    /// <summary>The entry cap or the global retained-byte budget refused a new entry.</summary>
    BlockedPending,

    /// <summary>The per-flow ring is full, or the frame is larger than the retention pool's buffer.</summary>
    BlockedFlowFull,

    /// <summary>The flow is inside its post-failure attribution cooldown.</summary>
    BlockedCooldown,
}

/// <summary>The disposition of a delivery's final claim.</summary>
internal enum AttributionClaim
{
    /// <summary>The flow table stored this entry's decision.</summary>
    Claimed,

    /// <summary>The transport tuple already carried a decision (a second adapter's observation, or a post-reclaim retry); this entry's attribution was redundant.</summary>
    AlreadyAttributed,

    /// <summary>The flow table is at capacity; the entry is kept and its later packets block fail-closed.</summary>
    CapacityBlocked,
}

/// <summary>The state of one pending entry's attribution.</summary>
internal enum PendingAttributionState
{
    Pending,
    Decided,
    Failed,
}

/// <summary>
/// One retained packet of a pending flow: the copy's lease plus everything a rebuilt
/// <see cref="CapturedFlowPacket"/> needs. A struct, so a ring slot never allocates.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal struct RetainedPacket
{
    public NativeLease Frame;
    public int Length;
    public PacketCaptureMetadata Metadata;
    public long PacketSequence;
    public long FlowGeneration;
    public PacketLayout Layout;
}

/// <summary>
/// One admitted pending flow attribution: the flow context the worker attributes, the state
/// machine the delivery branches on, the fixed-capacity ring of retained packets, and the
/// per-entry budget charge. Every member is mutated only under
/// <see cref="FlowAttributionPendingIndex"/>'s gate.
/// </summary>
internal sealed class PendingFlowAttribution
{
    private readonly RetainedPacket[] _ring = new RetainedPacket[FlowAttributionPendingIndex.RingCapacity];
    private int _head;

    public required FlowKey Key { get; init; }

    /// <summary>The adapter handle whose pump admitted, delivers and drains this entry.</summary>
    public required nint AdapterHandle { get; init; }

    /// <summary>The claim-time context; the worker writes the attributed <see cref="FlowContext.Process"/> here.</summary>
    public FlowContext Context { get; set; }

    /// <summary>The worker's verdict, written before the state flip and read only after a gated dequeue.</summary>
    public FlowDecision? Decision { get; set; }

    public PendingAttributionState State { get; set; }

    /// <summary>True when the failure was the pipeline's own shutdown cancellation, which arms no cooldown and counts no failure.</summary>
    public bool Shutdown { get; set; }

    /// <summary>Set by a false claim; the entry is kept and its later packets block fail-closed.</summary>
    public bool ClaimFailed { get; set; }

    /// <summary>True while this entry sits in its adapter's decided queue, so a retry cannot enqueue it twice.</summary>
    public bool Queued { get; set; }

    /// <summary>The TTL basis, refreshed on every append.</summary>
    public DateTimeOffset LastWriteUtc { get; set; }

    /// <summary>The retained bytes this entry currently charges against the global budget.</summary>
    public int ChargedBytes { get; set; }

    /// <summary>The packets currently retained, oldest first.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Appends one retained packet, keeping the oldest prefix when the ring is full — the head is
    /// the flow's triggering packet, so drop-oldest would discard the packet whose delivery the
    /// rest of the batch is ordered after (<c>design.md</c> §2.1). Returns false when the ring is
    /// full, leaving the ring untouched.
    /// </summary>
    public bool TryAppend(RetainedPacket packet)
    {
        if (Count >= _ring.Length) return false;
        _ring[(_head + Count) % _ring.Length] = packet;
        Count++;
        return true;
    }

    /// <summary>Moves every retained packet into <paramref name="destination"/>, oldest first, and empties the ring.</summary>
    public int TakeAll(List<RetainedPacket> destination)
    {
        var taken = Count;
        for (var index = 0; index < taken; index++)
        {
            var slot = (_head + index) % _ring.Length;
            destination.Add(_ring[slot]);
            _ring[slot] = default;
        }

        _head = 0;
        Count = 0;
        return taken;
    }

    /// <summary>Releases every lease still in the ring (the TTL, dispose-drain and refusal sinks).</summary>
    public void DisposeRingContents()
    {
        for (var index = 0; index < Count; index++) _ring[(_head + index) % _ring.Length].Frame.Dispose();
        _head = 0;
        Count = 0;
    }
}

/// <summary>
/// The bounded pending-attribution index: one entry per admitted host flow awaiting its verdict, a
/// fixed-capacity ring of retained packets per entry, a global retained-byte budget, a per-adapter
/// queue of decided entries, and one counter per refusal class.
/// <para>
/// The gate is a leaf: admission takes it and then the flow table's gate (pending → flow), and
/// nothing may take the flow table's gate and then this one — a flow-table decision factory must
/// never reach the pipeline. Delivery runs on the pump that owns the entry's adapter, one entry at
/// a time, and a packet leaves the ring exactly once because every take empties the slot under the
/// gate.
/// </para>
/// </summary>
internal sealed class FlowAttributionPendingIndex
{
    internal const int DefaultCapacity = 1024;

    /// <summary>The per-flow ring: enough for a handshake-plus-first-data burst, and the bound that keeps a pending flow's slow-path re-entry finite.</summary>
    internal const int RingCapacity = 32;

    /// <summary>The default cross-flow bound on retained packets; the entry cap binds first at standard MTUs.</summary>
    internal const long DefaultGlobalByteBudget = 8L * 1024 * 1024;

    /// <summary>
    /// How long an entry stays deliverable without a verdict. The delivery path, not this TTL, is
    /// what normally ends an entry's life; the TTL is the backstop for a stopped pump or a stuck
    /// worker and is enforced by the sweeper leg.
    /// </summary>
    private static readonly TimeSpan s_retentionTtl = TimeSpan.FromSeconds(5);

    /// <summary>The per-flow cooldown after a genuine attribution failure, so a retransmitting client cannot re-arm a failing path at packet rate.</summary>
    private static readonly TimeSpan s_failureCooldown = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<FlowKey, PendingFlowAttribution> _pending = [];
    private readonly Dictionary<FlowKey, DateTimeOffset> _cooldowns = [];
    private readonly Dictionary<nint, DecidedQueue> _decided = [];
    private readonly NativeBufferPool _pool;
    private readonly FlowTable _flows;
    private readonly int _capacity;
    private readonly long _byteBudget;
    private long _chargedBytes;
    private int _decidedCount;
    private bool _sealed;
    private long _pendingRejectedCount;
    private long _flowFullCount;
    private long _sealedCount;
    private long _cooldownBlockCount;
    private long _ttlExpiredCount;
    private long _claimFailedCount;
    private long _reAdmissionCount;

    public FlowAttributionPendingIndex(NativeBufferPool pool, FlowTable flows, int? capacity = null, long? byteBudget = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(flows);
        _pool = pool;
        _flows = flows;
        _capacity = capacity ?? DefaultCapacity;
        _byteBudget = byteBudget ?? DefaultGlobalByteBudget;
        if (_capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (_byteBudget <= 0) throw new ArgumentOutOfRangeException(nameof(byteBudget));
    }

    /// <summary>The live pending entries; for tests and diagnostics.</summary>
    public int ActiveCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>The live entry for a key, or null; for tests and diagnostics.</summary>
    public PendingFlowAttribution? EntryForDiagnostics(FlowKey key)
    {
        lock (_gate) return _pending.GetValueOrDefault(key);
    }

    /// <summary>The retained bytes currently charged against the global budget; for tests and diagnostics.</summary>
    public long ChargedBytes => Interlocked.Read(ref _chargedBytes);

    /// <summary>Whether any adapter has a decided entry waiting; the delivery fast path reads this without the gate.</summary>
    public bool HasDecided => Volatile.Read(ref _decidedCount) != 0;

    /// <summary>The decided-but-undelivered entry count across every adapter; for tests and diagnostics.</summary>
    public int DecidedDepth => Volatile.Read(ref _decidedCount);

    public long PendingRejectedCount => Interlocked.Read(ref _pendingRejectedCount);
    public long FlowFullCount => Interlocked.Read(ref _flowFullCount);
    public long SealedCount => Interlocked.Read(ref _sealedCount);
    public long CooldownBlockCount => Interlocked.Read(ref _cooldownBlockCount);
    public long TtlExpiredCount => Interlocked.Read(ref _ttlExpiredCount);
    public long ClaimFailedCount => Interlocked.Read(ref _claimFailedCount);
    public long ReAdmissionCount => Interlocked.Read(ref _reAdmissionCount);

    /// <summary>The live failure-cooldown entries; for tests and diagnostics.</summary>
    public int CooldownCount
    {
        get { lock (_gate) return _cooldowns.Count; }
    }

    /// <summary>
    /// Admits one packet of an eligible host-flow miss. Called on the pump thread.
    /// <paramref name="createdEntry"/> is set only when this call created the pending entry (the
    /// caller launches its setup work item), and <paramref name="resolved"/> only for
    /// <see cref="AttributionAdmission.Inline"/>.
    /// </summary>
    public AttributionAdmission Admit(in CapturedFlowPacket packet, DateTimeOffset now, out PendingFlowAttribution? createdEntry, out FlowState? resolved)
    {
        createdEntry = null;
        resolved = null;
        var key = packet.Context.Key;
        lock (_gate)
        {
            if (_sealed)
            {
                _ = Interlocked.Increment(ref _sealedCount);
                RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionSealed);
                return AttributionAdmission.BlockedSealed;
            }

            if (_pending.TryGetValue(key, out var entry)) return AdmitExistingUnderGate(entry, packet, now);

            // Another pump can claim the flow between the dispatcher's miss and this gate. The
            // resolved decision is the one the flow already has, so the caller takes its own path.
            if (_flows.TryResolve(key, out var existing) && existing is not null)
            {
                resolved = existing;
                return AttributionAdmission.Inline;
            }

            return TryCreateUnderGate(packet, key, now, out createdEntry);
        }
    }

    private AttributionAdmission AdmitExistingUnderGate(PendingFlowAttribution entry, in CapturedFlowPacket packet, DateTimeOffset now)
    {
        var length = packet.InspectionSpan.Length;
        if (entry.State == PendingAttributionState.Failed) return AttributionAdmission.BlockedFailed;
        if (!TryAppendUnderGate(entry, packet, length, now)) return AttributionAdmission.BlockedFlowFull;

        // A claim the flow table refused at capacity left the entry out of the decided queue; this
        // arrival re-queues it so the pump retries the claim and blocks what it cannot deliver.
        if (entry is { ClaimFailed: true, Queued: false })
        {
            EnqueueUnderGate(entry);
            entry.Queued = true;
        }

        return AttributionAdmission.Deferred;
    }

    private AttributionAdmission TryCreateUnderGate(in CapturedFlowPacket packet, FlowKey key, DateTimeOffset now, out PendingFlowAttribution? createdEntry)
    {
        createdEntry = null;
        var length = packet.InspectionSpan.Length;
        if (IsInCooldownUnderGate(key, now))
        {
            _ = Interlocked.Increment(ref _cooldownBlockCount);
            RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionCooldownBlocks);
            return AttributionAdmission.BlockedCooldown;
        }

        if (_pending.Count >= _capacity)
        {
            _ = Interlocked.Increment(ref _pendingRejectedCount);
            RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionPendingRejected);
            return AttributionAdmission.BlockedPending;
        }

        // The size guard runs before the rent so an oversized frame can never half-write an entry.
        if (length > _pool.BufferSize)
        {
            _ = Interlocked.Increment(ref _flowFullCount);
            RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionFlowFull);
            return AttributionAdmission.BlockedFlowFull;
        }

        if (!TryChargeBytes(length))
        {
            _ = Interlocked.Increment(ref _pendingRejectedCount);
            RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionPendingRejected);
            return AttributionAdmission.BlockedPending;
        }

        var created = new PendingFlowAttribution
        {
            Key = key,
            AdapterHandle = packet.Metadata.AdapterHandle,
            Context = packet.Context,
            State = PendingAttributionState.Pending,
            LastWriteUtc = now,
            ChargedBytes = length,
        };
        var lease = _pool.Rent();
        packet.InspectionSpan.CopyTo(lease.Span);
        _ = created.TryAppend(Retained(packet, lease, length));
        _pending.Add(key, created);
        createdEntry = created;
        return AttributionAdmission.Deferred;
    }

    /// <summary>Records the worker's verdict and queues the entry for its adapter's pump.</summary>
    public void MarkDecided(PendingFlowAttribution entry, FlowContext context, FlowDecision decision)
    {
        lock (_gate)
        {
            entry.Context = context;
            entry.Decision = decision;
            entry.State = PendingAttributionState.Decided;
            EnqueueUnderGate(entry);
            entry.Queued = true;
        }
    }

    /// <summary>
    /// Records a failed attribution. A shutdown cancellation is not a failure: it arms no cooldown
    /// and is counted by nothing, so a disposing pipeline never leaves cooldowns behind.
    /// </summary>
    public void MarkFailed(PendingFlowAttribution entry, bool shutdown)
    {
        lock (_gate)
        {
            entry.State = PendingAttributionState.Failed;
            entry.Shutdown = shutdown;
            EnqueueUnderGate(entry);
            entry.Queued = true;
        }
    }

    /// <summary>Dequeues one decided entry for <paramref name="adapterHandle"/>; the batch list is that adapter's reusable scratch.</summary>
    public bool TryDequeueDecided(nint adapterHandle, out PendingFlowAttribution? entry, out List<RetainedPacket>? batch)
    {
        lock (_gate)
        {
            if (!_decided.TryGetValue(adapterHandle, out var queue) || !queue.Entries.TryDequeue(out entry))
            {
                entry = null;
                batch = null;
                return false;
            }

            entry.Queued = false;
            _decidedCount--;
            batch = queue.Batch;
            return true;
        }
    }

    /// <summary>
    /// Puts a delivered entry back in its adapter's queue when a delivery aborted, so the next
    /// drain retries its claim instead of leaving the entry reachable only through the TTL. A no-op
    /// while the entry is already queued.
    /// </summary>
    public void Requeue(PendingFlowAttribution entry)
    {
        lock (_gate)
        {
            if (entry.Queued || _sealed) return;
            EnqueueUnderGate(entry);
            entry.Queued = true;
        }
    }

    /// <summary>Moves the entry's retained packets into <paramref name="batch"/> and reports the state the take observed, under one gate hold.</summary>
    public int TakeAll(PendingFlowAttribution entry, List<RetainedPacket> batch, out PendingAttributionState state)
    {
        lock (_gate)
        {
            batch.Clear();
            state = entry.State;
            return entry.TakeAll(batch);
        }
    }

    /// <summary>
    /// The failure arm: branches on the entry's state <em>inside</em> the gate hold, then takes
    /// every retained packet, removes the entry, credits its bytes and — for a genuine failure only
    /// — arms the per-flow cooldown. Returns false when the entry is not failed.
    /// </summary>
    public bool TryTakeFailed(PendingFlowAttribution entry, List<RetainedPacket> batch, DateTimeOffset now, out int taken)
    {
        lock (_gate)
        {
            if (entry.State != PendingAttributionState.Failed)
            {
                taken = 0;
                return false;
            }

            batch.Clear();
            taken = entry.TakeAll(batch);
            if (!entry.Shutdown) WriteCooldownUnderGate(entry.Key, now);
            RemoveUnderGate(entry);
            return true;
        }
    }

    /// <summary>
    /// The delivery's final step: claims the flow for the entry's decision in the same critical
    /// section that removes the entry, so no packet can be appended after the claim and no packet
    /// can bypass the ring and be followed by an older one. <paramref name="generation"/> is the
    /// claimed flow's generation, or 0 when no flow was claimed.
    /// </summary>
    public AttributionClaim Claim(PendingFlowAttribution entry, out long generation)
    {
        lock (_gate)
        {
            // A tuple that already carries a decision (a second adapter's observation of the same
            // transport tuple, or a retry after this entry's claim was refused) means this entry's
            // attribution was redundant. The observable outcome is unchanged either way.
            if (_flows.TryResolve(entry.Key, out var existing) && existing is not null)
            {
                generation = 0;
                _ = Interlocked.Increment(ref _reAdmissionCount);
                RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionReAdmission);
                RemoveUnderGate(entry);
                return AttributionClaim.AlreadyAttributed;
            }

            if (entry.Decision is { } decision && _flows.TryClaimResolved(entry.Key, decision, out var claimed))
            {
                generation = claimed?.Generation ?? 0;
                RemoveUnderGate(entry);
                return AttributionClaim.Claimed;
            }

            generation = 0;
            entry.ClaimFailed = true;
            _ = Interlocked.Increment(ref _claimFailedCount);
            RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionClaimFailed);
            return AttributionClaim.CapacityBlocked;
        }
    }

    /// <summary>
    /// Reclaims entries whose verdict never landed inside the retention TTL and prunes elapsed
    /// cooldowns, returning the reclaimed entries in <paramref name="reclaimed"/> so the caller
    /// releases their leases outside the gate.
    /// </summary>
    public void RemoveExpired(DateTimeOffset now, List<PendingFlowAttribution> reclaimed)
    {
        List<FlowKey>? expired = null;
        lock (_gate)
        {
            foreach (var pair in _pending)
            {
                if (now - pair.Value.LastWriteUtc > s_retentionTtl) (expired ??= []).Add(pair.Key);
            }

            if (expired is not null)
            {
                foreach (var key in expired)
                {
                    if (!_pending.Remove(key, out var entry)) continue;
                    CreditBytes(entry.ChargedBytes);
                    entry.ChargedBytes = 0;
                    reclaimed.Add(entry);
                }

                _ = Interlocked.Add(ref _ttlExpiredCount, expired.Count);
                RuntimeCounters.Shared.Add(RuntimeCounters.AttributionPendingTtlExpired, expired.Count);
            }

            if (_cooldowns.Count == 0) return;
            List<FlowKey>? elapsed = null;
            foreach (var pair in _cooldowns)
            {
                if (pair.Value <= now) (elapsed ??= []).Add(pair.Key);
            }

            if (elapsed is null) return;
            foreach (var key in elapsed) _cooldowns.Remove(key);
        }
    }

    /// <summary>Releases a reclaimed entry's retained leases; call outside the gate.</summary>
    public static void DisposeReclaimed(PendingFlowAttribution entry) => entry.DisposeRingContents();

    /// <summary>
    /// Seals the index: every later admission blocks fail-closed (never an inline attribution on
    /// the pump) and every pending entry is removed and handed back for a fail-closed drain.
    /// </summary>
    public void Seal(List<PendingFlowAttribution> pending)
    {
        lock (_gate)
        {
            _sealed = true;
            foreach (var entry in _pending.Values)
            {
                CreditBytes(entry.ChargedBytes);
                entry.ChargedBytes = 0;
                pending.Add(entry);
            }

            _pending.Clear();
            _cooldowns.Clear();
            _decided.Clear();
            _decidedCount = 0;
        }
    }

    /// <summary>Charges bytes against the global budget; on exhaustion the charge is rolled back.</summary>
    private bool TryChargeBytes(int length)
    {
        if (Interlocked.Add(ref _chargedBytes, length) > _byteBudget)
        {
            Interlocked.Add(ref _chargedBytes, -length);
            return false;
        }

        return true;
    }

    private void CreditBytes(int length) => Interlocked.Add(ref _chargedBytes, -length);

    private bool TryAppendUnderGate(PendingFlowAttribution entry, in CapturedFlowPacket packet, int length, DateTimeOffset now)
    {
        if (entry.Count >= RingCapacity || length > _pool.BufferSize || !TryChargeBytes(length))
        {
            _ = Interlocked.Increment(ref _flowFullCount);
            RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionFlowFull);
            return false;
        }

        var lease = _pool.Rent();
        packet.InspectionSpan.CopyTo(lease.Span);
        if (!entry.TryAppend(Retained(packet, lease, length)))
        {
            // The ring filled between the two checks; the charge is rolled back with the buffer.
            lease.Dispose();
            CreditBytes(length);
            _ = Interlocked.Increment(ref _flowFullCount);
            RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionFlowFull);
            return false;
        }

        entry.ChargedBytes += length;
        entry.LastWriteUtc = now;
        return true;
    }

    private static RetainedPacket Retained(in CapturedFlowPacket packet, NativeLease lease, int length) => new()
    {
        Frame = lease,
        Length = length,
        Metadata = packet.Metadata,
        PacketSequence = packet.PacketSequence,
        FlowGeneration = packet.FlowGeneration,
        Layout = packet.Layout,
    };

    private void EnqueueUnderGate(PendingFlowAttribution entry)
    {
        if (!_decided.TryGetValue(entry.AdapterHandle, out var queue))
        {
            queue = new DecidedQueue();
            _decided.Add(entry.AdapterHandle, queue);
        }

        queue.Entries.Enqueue(entry);
        _decidedCount++;
    }

    private void RemoveUnderGate(PendingFlowAttribution entry)
    {
        if (!_pending.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry)) return;
        _pending.Remove(entry.Key);
        CreditBytes(entry.ChargedBytes);
        entry.ChargedBytes = 0;
        entry.DisposeRingContents();
    }

    private bool IsInCooldownUnderGate(FlowKey key, DateTimeOffset now)
    {
        if (!_cooldowns.TryGetValue(key, out var retryAt)) return false;
        if (now < retryAt) return true;
        _cooldowns.Remove(key);
        return false;
    }

    private void WriteCooldownUnderGate(FlowKey key, DateTimeOffset now)
    {
        // Bounded at the entry capacity: a failing-attribution storm must not become an
        // immediate-retry storm when the cooldown dictionary refuses writes, so the
        // oldest-deadline entry is evicted instead.
        if (_cooldowns.Count >= _capacity && !_cooldowns.ContainsKey(key)) EvictOldestCooldownUnderGate();
        _cooldowns[key] = now + s_failureCooldown;
    }

    private void EvictOldestCooldownUnderGate()
    {
        FlowKey? oldest = null;
        var oldestRetryAt = DateTimeOffset.MaxValue;
        foreach (var pair in _cooldowns)
        {
            if (pair.Value >= oldestRetryAt) continue;
            oldestRetryAt = pair.Value;
            oldest = pair.Key;
        }

        if (oldest is { } evicted) _cooldowns.Remove(evicted);
    }

    /// <summary>
    /// One adapter's decided entries plus the reusable delivery batch. The batch is touched only by
    /// the pump that owns the adapter, which is also the only thread that may deliver its entries.
    /// </summary>
    private sealed class DecidedQueue
    {
        public Queue<PendingFlowAttribution> Entries { get; } = new();

        public List<RetainedPacket> Batch { get; } = [];
    }
}
