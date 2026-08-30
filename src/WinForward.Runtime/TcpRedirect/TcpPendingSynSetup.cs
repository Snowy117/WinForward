using WinForward.Core;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// One genuinely new SYN awaiting its background redirect setup (R8): a materialized copy of the
/// frame (the pump's native batch slot is recycled the moment the dispatch returns), the packet
/// metadata the setup tail needs (flow context, capture metadata for injection, sequence and
/// generation stamps for logging and session registration), and the owning setup task slot.
/// Instances are mutated only under the owning index's gate.
/// </summary>
internal sealed class PendingSynSetup
{
    public required byte[] RetainedFrame { get; set; }
    public required FlowContext Context { get; init; }
    public required PacketCaptureMetadata Metadata { get; init; }
    public long PacketSequence { get; init; }
    public long FlowGeneration { get; init; }
    public DateTimeOffset LastWriteUtc { get; set; }
    public Task SetupTask { get; set; } = Task.CompletedTask;
}

/// <summary>
/// The coordinator-owned index of pending TCP SYN setups (R8), the TCP counterpart of the UDP
/// bounded setup queue: the pump-side handler retains a copy of each new-flow SYN and returns
/// immediately, and the background task performs the listener allocation, claim, rewrite, and
/// injection. Bounds mirror the UDP shape: a fixed entry cap (distinct original flow keys), a
/// global Interlocked byte budget charged on retain and credited exactly once at every sink (a
/// retransmission's overwrite, the completing setup's removal, TTL expiry, and the dispose
/// drain), a retention TTL enforced by the idle sweep, and a per-flow setup-failure cooldown so
/// a retransmitting client cannot hammer a failing setup path at SYN rate. The gate is a leaf
/// lock: it is never taken while holding the store, table, or tombstone gates.
/// </summary>
internal sealed class TcpPendingSynSetupIndex
{
    internal const int DefaultCapacity = 1024;

    /// <summary>
    /// The default cross-flow bound on retained-SYN memory. A frame is at most ~1514 B, so the
    /// entry cap binds first under standard MTUs; the byte budget exists for symmetry with the
    /// UDP bounded-setup-memory pattern and future-proofing against jumbo capture frames.
    /// </summary>
    internal const long DefaultGlobalByteBudget = 1024 * 1024;

    /// <summary>
    /// How long a retained SYN stays deliverable while its background setup has not completed.
    /// A normal setup (bind + claim + inject) completes in well under a second; an entry still
    /// pending after this window belongs to a stuck bind and is reclaimed by the idle sweep.
    /// </summary>
    private static readonly TimeSpan RetentionTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The per-flow cooldown after a genuine setup failure (bind throws, claim fails, injection
    /// fails): the client's SYN retransmissions land inside this window are consumed silently so
    /// a dead setup path is not re-armed at retransmission rate. Mirrors the UDP setup-failure
    /// tombstone (<c>udp.setup.cooldown</c>).
    /// </summary>
    private static readonly TimeSpan SetupFailureCooldown = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<FlowKey, PendingSynSetup> _pending = [];
    private readonly Dictionary<FlowKey, DateTimeOffset> _setupCooldowns = [];
    private readonly List<Task> _setupTasks = [];
    private readonly int _capacity;
    private readonly long _byteBudget;
    private long _chargedBytes;
    private long _budgetRejectionCount;
    private long _capacityRejectionCount;
    private long _ttlExpiredCount;

    public TcpPendingSynSetupIndex(int? capacity = null, long? byteBudget = null)
    {
        _capacity = capacity ?? DefaultCapacity;
        _byteBudget = byteBudget ?? DefaultGlobalByteBudget;
        if (_capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (_byteBudget <= 0) throw new ArgumentOutOfRangeException(nameof(byteBudget));
    }

    /// <summary>The live pending entries; for tests and diagnostics.</summary>
    internal int ActiveCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>The retained-frame bytes currently charged against the global budget; for tests and diagnostics.</summary>
    internal long ChargedBytes => Interlocked.Read(ref _chargedBytes);

    /// <summary>Total SYNs rejected because the entry cap or byte budget was exhausted; for tests and diagnostics.</summary>
    internal long RejectionCount => Interlocked.Read(ref _capacityRejectionCount) + Interlocked.Read(ref _budgetRejectionCount);

    /// <summary>Total pending entries reclaimed at the retention TTL; for tests and diagnostics.</summary>
    internal long TtlExpiredCount => Interlocked.Read(ref _ttlExpiredCount);

    /// <summary>The live setup-failure cooldown entries; for tests and diagnostics.</summary>
    internal int CooldownCount
    {
        get { lock (_gate) return _setupCooldowns.Count; }
    }

    /// <summary>Whether the flow is inside its setup-failure cooldown window (the caller consumes silently).</summary>
    public bool IsInSetupCooldown(FlowKey key, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_setupCooldowns.TryGetValue(key, out var retryAt)) return false;
            if (now < retryAt) return true;
            _setupCooldowns.Remove(key);
            return false;
        }
    }

    /// <summary>
    /// Retains the newest copy of a new-flow SYN. An existing pending entry (a retransmission
    /// inside the setup window) is overwritten in place: the previous copy's budget charge is
    /// credited and <c>created</c> comes back null — the caller must NOT launch a second setup
    /// task, because retransmissions share the client ISN and the running task injects the
    /// equivalent frame. A genuinely new entry charges the global byte budget, installs the
    /// copy, and returns the entry for the caller to attach its setup task. Returns false when
    /// the entry cap or the byte budget refuses the retain (fail-closed backpressure, never a
    /// cooldown).
    /// </summary>
    public bool TryRetain(FlowKey key, byte[] frameCopy, FlowContext context, PacketCaptureMetadata metadata, long packetSequence, long flowGeneration, DateTimeOffset now, out PendingSynSetup? created)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var existing))
            {
                var overwritten = existing.RetainedFrame;
                if (!TryChargeBytes(frameCopy.Length))
                {
                    // Keep the older copy: the newer one did not fit the budget either way, and the
                    // entry plus its running setup task stay intact.
                    created = null;
                    return false;
                }

                existing.RetainedFrame = frameCopy;
                existing.LastWriteUtc = now;
                CreditBytes(overwritten.Length);
                created = null;
                return true;
            }

            if (_pending.Count >= _capacity || !TryChargeBytes(frameCopy.Length))
            {
                Interlocked.Increment(ref _capacityRejectionCount);
                created = null;
                return false;
            }

            var entry = new PendingSynSetup
            {
                RetainedFrame = frameCopy,
                Context = context,
                Metadata = metadata,
                PacketSequence = packetSequence,
                FlowGeneration = flowGeneration,
                LastWriteUtc = now,
            };
            _pending.Add(key, entry);
            created = entry;
            return true;
        }
    }

    /// <summary>
    /// Attaches the background setup task to a freshly created entry so a drain (dispose, tests)
    /// can await it. Only valid for entries returned by <see cref="TryRetain"/> as created.
    /// </summary>
    public void AttachSetup(PendingSynSetup entry, Task setupTask)
    {
        lock (_gate)
        {
            entry.SetupTask = setupTask;
            // Completed task references are pruned on the next attach, so the list stays bounded
            // by the concurrently in-flight setups instead of total setups.
            _setupTasks.RemoveAll(static task => task.IsCompleted);
            _setupTasks.Add(setupTask);
        }
    }

    /// <summary>
    /// Removes a completed entry when it is still the live instance for its key (ReferenceEquals
    /// guard: a TTL expiry or a replacement generation may have removed it first — the credit for
    /// whatever copy the entry held landed exactly once at that earlier removal). A genuine
    /// failure arms the per-flow cooldown; shutdown cancellation passes false so a disposing
    /// coordinator never leaves cooldowns behind.
    /// </summary>
    public void Complete(FlowKey key, PendingSynSetup entry, bool writeCooldown, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _pending.Remove(key);
                CreditBytes(entry.RetainedFrame.Length);
            }

            if (writeCooldown) WriteCooldownUnderGate(key, now);
        }
    }

    /// <summary>
    /// Reclaims entries whose background setup never completed within the retention TTL and
    /// prunes elapsed setup cooldowns; rides the coordinator's existing idle-sweep tick (no
    /// dedicated timer). The still-running setup task is unaffected: it captured its own frame
    /// reference at launch, and the redirect table's exactly-once claim absorbs a replacement
    /// generation as an ordinary concurrent loser.
    /// </summary>
    public void RemoveExpired(DateTimeOffset now)
    {
        List<FlowKey>? expired = null;
        lock (_gate)
        {
            foreach (var pair in _pending)
            {
                if (now - pair.Value.LastWriteUtc > RetentionTtl) (expired ??= []).Add(pair.Key);
            }

            if (expired is not null)
            {
                foreach (var key in expired)
                {
                    // Removal only: the entry's copy leaves the pending set here, and the completing
                    // task's ReferenceEquals guard makes its later Complete a no-op for the credit.
                    if (_pending.Remove(key, out var entry)) CreditBytes(entry.RetainedFrame.Length);
                }

                Interlocked.Add(ref _ttlExpiredCount, expired.Count);
            }

            if (_setupCooldowns.Count == 0) return;
            List<FlowKey>? elapsed = null;
            foreach (var pair in _setupCooldowns)
            {
                if (pair.Value <= now) (elapsed ??= []).Add(pair.Key);
            }

            if (elapsed is not null)
            {
                foreach (var key in elapsed) _setupCooldowns.Remove(key);
            }
        }
    }

    /// <summary>
    /// Drops every pending entry (dispose drain): the store's setup drain already awaited every
    /// started background task, so this closes the tiny window between a task's final
    /// <c>ExitSetup</c> and its entry removal, and credits every retained copy exactly once.
    /// </summary>
    public void RemoveAll()
    {
        lock (_gate)
        {
            foreach (var entry in _pending.Values) CreditBytes(entry.RetainedFrame.Length);
            _pending.Clear();
            _setupCooldowns.Clear();
        }
    }

    /// <summary>
    /// Awaits every setup task launched so far, including tasks whose entries a TTL expiry
    /// already reclaimed (their in-flight work is unaffected by the entry's removal, so a drain
    /// must not lose them). Completed references drop off at the snapshot.
    /// </summary>
    public Task DrainAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            tasks = _setupTasks.Where(task => !task.IsCompleted).ToArray();
            _setupTasks.Clear();
        }

        return tasks.Length == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    private void WriteCooldownUnderGate(FlowKey key, DateTimeOffset now)
    {
        // Bounded at the entry capacity: a failing-setup storm must not turn into an
        // immediate-retry storm when the cooldown dictionary refuses writes, so the
        // oldest-deadline entry is evicted instead (the timestamp doubles as the age order —
        // the same cold-path tradeoff as the UDP coordinator's cooldown table).
        if (_setupCooldowns.Count >= _capacity && !_setupCooldowns.ContainsKey(key)) EvictOldestCooldownUnderGate();
        _setupCooldowns[key] = now + SetupFailureCooldown;
    }

    private void EvictOldestCooldownUnderGate()
    {
        FlowKey? oldest = null;
        var oldestRetryAt = DateTimeOffset.MaxValue;
        foreach (var pair in _setupCooldowns)
        {
            if (pair.Value >= oldestRetryAt) continue;
            oldestRetryAt = pair.Value;
            oldest = pair.Key;
        }

        if (oldest is { } evicted) _setupCooldowns.Remove(evicted);
    }

    /// <summary>
    /// Charges bytes against the global budget; on exhaustion the charge is rolled back and the
    /// caller must not retain the copy. A transient overshoot from racing adders is accepted
    /// (bounded by the in-flight adders), mirroring the UDP budget.
    /// </summary>
    private bool TryChargeBytes(int length)
    {
        if (Interlocked.Add(ref _chargedBytes, length) > _byteBudget)
        {
            Interlocked.Add(ref _chargedBytes, -length);
            Interlocked.Increment(ref _budgetRejectionCount);
            return false;
        }

        return true;
    }

    private void CreditBytes(int length) => Interlocked.Add(ref _chargedBytes, -length);
}
