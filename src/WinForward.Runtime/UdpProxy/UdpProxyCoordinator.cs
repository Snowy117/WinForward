using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.Logging;

namespace WinForward.Runtime.UdpProxy;

public sealed partial class UdpProxyCoordinator : IAsyncDisposable, IUdpSessionSlotHost
{
    private const int MaximumSocks5UdpHeaderSize = 22;
    private const int OversizeSentinelSize = 1;
    private const int SetupQueueMaximumPackets = 32;
    private const int SetupQueueMaximumBytes = 32_768;

    private readonly UdpAssociationTable _associations;
    private readonly Dictionary<FlowKey, UdpSessionSlot> _sessions;

    // Direct-mapped ready-path cache over the gated _sessions authority, allocated once in the
    // constructor and never grown. A slot is validated by the attached session's own flow key, so a
    // collision, an unattached slot or a stale entry falls back to the gated admission path — never to
    // another flow's session. Only _sessions mutations (admission, removal, dispose) write it, always
    // under _gate. A reader that loaded an entry before a removal's clear reaches the session the
    // removal is tearing down: an expiring or faulted session refuses the datagram and the caller
    // counts the fail-closed drop, while the plain-disposal path reaches the transport teardown the
    // removal already started (the pre-existing outstanding-lease window of `udp-relay.md`).
    private readonly UdpSessionSlot?[] _sessionCache;
    private readonly UdpSetupCooldownTable _cooldowns;
    private readonly UdpSetupQueueBudget _budget;
    private readonly UdpSessionSetup _setup;
    private readonly IUdpSessionSlotHost _slotHost;
    private readonly NativeBufferPool _setupQueuePool;
    private readonly ISetupExecutor _setupExecutor;
    private readonly Func<SetupWorkItem, Task> _setupHandler;
    private readonly Lock _gate = new();
    private int _gateEntryCount;

    // Sweep-level single flight held across the whole async method (a Lock cannot span the teardown
    // awaits). Uncontended, WaitAsync() returns a cached completed task, so the no-op tick neither
    // suspends nor allocates. The semaphore — not _gate — is what makes the reused idle scratch sound,
    // because the scan and the teardown loop run outside _gate and RemoveExpiredAsync is public.
    private readonly SemaphoreSlim _sweepGate = new(1, 1);
    private readonly List<(UdpSessionSlot Slot, UdpProxySession Session)> _idleScratch = [];
    private readonly QuiescenceScope _scope = new();
    private readonly TimeProvider _timeProvider;
    private readonly Func<ValueTask>? _beforeExpiryRecheck;
    private readonly ILogger _logger;
    private int _disposeStarted;

    /// <summary>Rate limit for the deprecated-session send drop diagnostic (one line per window).</summary>
    private readonly RuntimeLogThrottle _sessionUnavailableDropLog = new(TimeSpan.FromSeconds(5));

    /// <summary>Rate limit for the session-capacity rejection warning (one line per window).</summary>
    private readonly RuntimeLogThrottle _capacityRejectionLog = new(TimeSpan.FromSeconds(5));

    /// <summary>Upper bound on eagerly seeded dictionary capacity; growth beyond it stays lazy.</summary>
    private const int MaximumPreSeedCapacity = 1_024;

    public UdpProxyCoordinator(
        IUdpProxyTransportFactory transportFactory,
        IUdpResponseSink responseSink,
        NativeBufferPool setupQueuePool,
        NativeBufferPool receiveWindowPool,
        ISetupExecutor setupExecutor,
        UdpProxyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(responseSink);
        ArgumentNullException.ThrowIfNull(setupQueuePool);
        ArgumentNullException.ThrowIfNull(receiveWindowPool);
        ArgumentNullException.ThrowIfNull(setupExecutor);
        options ??= new UdpProxyOptions();
        var capacity = options.Capacity;
        var timeProvider = options.TimeProvider;
        var maximumFrameSize = options.MaximumFrameSize;
        var relayReceiveBufferBytes = options.RelayReceiveBufferBytes;
        var setupQueueGlobalByteBudget = options.SetupQueueGlobalByteBudget ?? UdpSetupQueueBudget.SetupQueueGlobalByteBudget;
        if (timeProvider is null) throw new ArgumentNullException(nameof(options), "The TimeProvider option must not be null.");
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(options), capacity, "Capacity must be positive.");
        if (maximumFrameSize <= 0) throw new ArgumentOutOfRangeException(nameof(options), maximumFrameSize, "Maximum frame size must be positive.");
        if (relayReceiveBufferBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), relayReceiveBufferBytes, "Relay receive buffer bytes must be positive.");
        if (setupQueueGlobalByteBudget <= 0) throw new ArgumentOutOfRangeException(nameof(options), setupQueueGlobalByteBudget, "Setup queue global byte budget must be positive.");
        var preSeed = Math.Min(capacity, MaximumPreSeedCapacity);
        _associations = new UdpAssociationTable(capacity, preSeed);
        _sessions = new Dictionary<FlowKey, UdpSessionSlot>(preSeed);
        var cacheSlots = BitOperations.RoundUpToPowerOf2((uint)Math.Clamp((long)capacity * 8, 1_024, 16_384));
        _sessionCache = new UdpSessionSlot?[cacheSlots];
        _cooldowns = new UdpSetupCooldownTable(capacity);
        Capacity = capacity;
        RelayReceiveBufferBytes = relayReceiveBufferBytes;
        _timeProvider = timeProvider;
        ActivityClock = options.ActivityClock ?? new ActivityBucketClock(timeProvider);
        _beforeExpiryRecheck = options.BeforeExpiryRecheck;
        _logger = options.Logger ?? NullLogger.Instance;
        _budget = new UdpSetupQueueBudget(setupQueueGlobalByteBudget, _logger, _timeProvider);
        _setupQueuePool = setupQueuePool;
        _setupExecutor = setupExecutor;
        var receiveBufferSize = ReceiveWindowSize(maximumFrameSize);
        _setupHandler = RunSessionSetupAsync;
        _slotHost = this;
        _setup = new UdpSessionSetup(
            transportFactory,
            _associations,
            responseSink,
            timeProvider,
            ActivityClock,
            _logger,
            receiveWindowPool,
            receiveBufferSize,
            _slotHost);
    }

    /// <summary>The number of live UDP sessions (heartbeat diagnostics; gate-consistent).</summary>
    public int SessionCount
    {
        get
        {
            lock (_gate)
            {
                NoteGateEntry();
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// The composition's shared activity clock (the same instance the flow table and the redirect table
    /// use). Tests drive it to a bucket edge; the ready path only ever reads <see cref="ActivityBucketClock.Current"/>.
    /// </summary>
    internal ActivityBucketClock ActivityClock { get; }

    /// <summary>
    /// The live session attached to <paramref name="flow"/>, read under the gate (diagnostics only: a
    /// fact attaches the session's own hold probe). Null when the flow has no session yet.
    /// </summary>
    internal UdpProxySession? SessionForDiagnostics(FlowKey flow)
    {
        lock (_gate)
        {
            NoteGateEntry();
            return _sessions.TryGetValue(flow, out var slot) ? slot.Session : null;
        }
    }

    /// <summary>The ready-path cache's slot count (diagnostics only): a collision fact needs the mask.</summary>
    internal int SessionCacheSlotCountForDiagnostics => _sessionCache.Length;

    /// <summary>Whether <paramref name="flow"/>'s session is attached and ready (diagnostics only).</summary>
    internal bool SessionReadyForDiagnostics(FlowKey flow)
    {
        lock (_gate)
        {
            NoteGateEntry();
            return _sessions.TryGetValue(flow, out var slot) && slot is { Ready: true, Session: not null };
        }
    }

    /// <summary>
    /// The gate-entry diagnostics sink, or null in production (diagnostics only). It fires immediately
    /// after every <see cref="_gate"/> acquisition, so a test can park a holder inside the gate or
    /// count the entries a driven packet path takes.
    /// </summary>
    internal Action? GateHoldProbe { get; set; }

    /// <summary>
    /// The number of <see cref="_gate"/> acquisitions recorded since the process started, counted only
    /// while <see cref="GateHoldProbe"/> is attached (diagnostics only, never on the product path).
    /// </summary>
    internal int GateEntryCountForDiagnostics => Volatile.Read(ref _gateEntryCount);

    private void NoteGateEntry()
    {
        var probe = GateHoldProbe;
        if (probe is null) return;
        Interlocked.Increment(ref _gateEntryCount);
        probe();
    }

    /// <summary>The session budget this coordinator was constructed with (heartbeat diagnostics).</summary>
    public int Capacity { get; }

    /// <summary>The configured per-session relay socket receive buffer in bytes (heartbeat diagnostics).</summary>
    public int RelayReceiveBufferBytes { get; }

    /// <summary>
    /// One flow's lifecycle state (see <see cref="UdpSessionState"/>): the slot-level
    /// <see cref="UdpSessionState.SettingUp"/> while the flow has a slot without an attached
    /// session (the relay is dialing), otherwise the attached session's own state. A flow with no
    /// slot at all also reports <see cref="UdpSessionState.SettingUp"/>: its next datagram starts a
    /// fresh setup.
    /// </summary>
    internal UdpSessionState SessionState(FlowKey flow)
    {
        UdpProxySession? session;
        lock (_gate)
        {
            NoteGateEntry();
            session = _sessions.TryGetValue(flow, out var slot) ? slot.Session : null;
        }
        return session?.State ?? UdpSessionState.SettingUp;
    }

    /// <summary>
    /// The coordinator's observable counters as one snapshot: the live setup-failure cooldown
    /// count (bounded by <c>capacity</c>), the aggregate setup-queue bytes charged against the
    /// global budget, and the setup-queue rejection / flush-TTL / dial-start re-stamp totals
    /// for tests and diagnostics.
    /// </summary>
    internal UdpProxyDiagnostics Diagnostics => new(_cooldowns.Count, _budget.PendingBytes, _budget.RejectionCount, _setup.TtlExpiredCount, _setup.StampsRefreshedCount);

    /// <summary>
    /// The single source of truth for the per-session receive window: maximum Ethernet frame,
    /// maximum SOCKS5 UDP header, and the one-byte oversize sentinel. Composition sizes the
    /// shared receive-window pool with this value so the pool and the session window agree.
    /// </summary>
    internal static int ReceiveWindowSize(int maximumFrameSize) =>
        checked(maximumFrameSize + MaximumSocks5UdpHeaderSize + OversizeSentinelSize);

    /// <summary>
    /// The receive-window pool's capacity for a composition admitting <paramref name="sessionCapacity"/>
    /// live sessions: one lease per live session, plus <see cref="ReceiveWindowRetireHeadroom"/>.
    /// <para>
    /// One lease per live session is exact — <see cref="UdpProxySession"/> rents one window for its
    /// whole receive loop and returns it in the loop's <see langword="finally"/> — but the pool must also cover
    /// the admit-while-retiring overlap: slot removal happens under the gate and only then awaits
    /// session disposal outside it, while admission is gated on the slot count, so a newly admitted
    /// session can rent before a retiring one has returned its window. Capacity is a bound, not a
    /// preallocation: <see cref="NativeBufferPool"/> allocates on demand and retains only what was
    /// ever rented, so the allowance costs nothing until it is used.
    /// </para>
    /// </summary>
    internal static int ReceiveWindowPoolCapacity(int sessionCapacity) =>
        checked(sessionCapacity + ReceiveWindowRetireHeadroom(sessionCapacity));

    /// <summary>
    /// The floor of the concurrent-retirement allowance the receive-window pool capacity carries:
    /// sixty-four sessions retiring at once, i.e. four fault bursts of the sixteen concurrent flows
    /// one control connection used to serve — the stampede the allowance was first sized against,
    /// kept as one named number now that a flow owns its association outright.
    /// </summary>
    internal const int ReceiveWindowRetireFloor = 64;

    /// <summary>
    /// The concurrent-retirement allowance the pool capacity carries. The expiry retire is serial (one
    /// slot removal per candidate, awaited by the sweeper), but receive-failure teardowns are
    /// concurrent scope children that never take the sweep gate, and send-path and setup removals
    /// await their own teardown from their own callers, so the overlap has no in-code bound and a
    /// simultaneous multi-session fault is the worst case. The allowance is therefore a documented
    /// policy number rather than a proof: <see cref="ReceiveWindowRetireFloor"/> sessions, and at
    /// least a sixteenth of the capacity so it scales with a raised <c>udpSessionCapacity</c>. The
    /// attribution pool's capacity in composition is the in-tree precedent for sizing a pool so the
    /// steady state cannot overflow; a storm larger than the allowance still allocates fresh leases
    /// transiently, which is bounded by the storm and never a steady state.
    /// </summary>
    internal static int ReceiveWindowRetireHeadroom(int sessionCapacity) =>
        Math.Max(ReceiveWindowRetireFloor, sessionCapacity / 16);

    /// <summary>
    /// The retention a <em>completed one-shot exchange</em> keeps (see
    /// <see cref="UdpProxySession.TryBeginExpiry(DateTimeOffset, TimeSpan, TimeSpan)"/>), the second
    /// of the two composition-owned retention constants. Five seconds is the shortest accepted
    /// <c>udpSessionIdleSeconds</c> and the shortest retention the 500 ms activity quantum was
    /// designed for, so it is the finest class the sweep can honour without changing the quantum. A
    /// configured retention at or below it makes the two classes identical, i.e. uniform retention.
    /// </summary>
    public static readonly TimeSpan OneShotIdleTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts the off-gate setup task for a new flow. Kept out of the send entries because a
    /// captured-parameter lambda makes the compiler hoist its display class to the method
    /// entry, charging every warm datagram the setup closure's allocation even when the
    /// new-flow branch never runs. Routing both entries through this method confines that
    /// allocation to the cold new-flow path.
    /// </summary>
    private bool ScheduleSessionSetup(FlowKey flow, ProxyTarget target, long flowGeneration, MacAddress capturedClientMac, UdpSessionSlot slot)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = _setupExecutor.RentItem(_setupHandler);
        item._completion = completion;
        item._flow = flow;
        item._udp._target = target;
        item._udp._flowGeneration = flowGeneration;
        item._udp._clientMac = capturedClientMac;
        item._udp._slot = slot;
        item._cancellationToken = _scope.Token;
        if (!_setupExecutor.TryEnqueue(item))
        {
            completion.TrySetCanceled(item._cancellationToken);
            return false;
        }

        slot.Completion = completion.Task;
        return true;
    }

    private Task RunSessionSetupAsync(SetupWorkItem item)
        => _setup.CreateSessionAsync(item._flow, item._udp._target, item._udp._flowGeneration, item._udp._clientMac, item._udp._slot!, item._cancellationToken);

    /// <summary>
    /// Buffers one datagram while the flow's session is setting up or flushing. The queue is
    /// bounded (packets and bytes); on overflow the oldest datagrams are dropped so the freshest
    /// state survives, with drops surfaced through the trace event and a rate-limited debug
    /// summary. A datagram that does not fit the global setup byte budget is rejected without
    /// arming a setup cooldown — budget exhaustion is backpressure, not a setup failure. Returns
    /// true when the datagram (or a newer one in its place) is retained.
    /// </summary>
    private bool EnqueueSetupDatagram(FlowKey flow, UdpSessionSlot slot, ReadOnlySpan<byte> payload)
    {
        // R4: charge the global budget before the per-flow enqueue. Every charged byte is
        // credited back exactly once at whichever sink dequeues it: the flush below, the
        // drop-oldest loop here, the slot drain (RemoveSlotAsync), or the dispose drain.
        if (!_budget.TryCharge(payload.Length))
        {
            _budget.NoteDrop(flow, 1);
            return false;
        }

        // The captured datagram lives in the pump's native batch slot only for the dispatch, so
        // it is copied into a pooled udp-datagram lease before the enqueue; the queue owns that
        // lease once it accepts it, and the caller releases it exactly once on refusal.
        var lease = _setupQueuePool.Rent();
        if (payload.Length > lease.Length)
        {
            // A datagram larger than the pinned frame cap cannot occur on the capture path
            // release the rental and fail the enqueue closed rather than copying past the lease.
            lease.Dispose();
            _budget.Credit(payload.Length);
            _budget.NoteDrop(flow, 1);
            return false;
        }
        payload.CopyTo(lease.Span);

        var now = _timeProvider.GetUtcNow();
        var enqueued = slot.SetupQueue.TryEnqueue(lease, payload.Length, now);
        var dropped = 0;
        while (!enqueued && slot.SetupQueue.TryDequeue(out var evicted, out var evictedLength, out _))
        {
            dropped++;
            _budget.Credit(evictedLength);
            evicted.Dispose();
            enqueued = slot.SetupQueue.TryEnqueue(lease, payload.Length, now);
        }

        if (!enqueued)
        {
            // The per-flow bounds refused the datagram outright: release its lease and charge.
            dropped++;
            _budget.Credit(payload.Length);
            lease.Dispose();
        }
        if (dropped > 0) _budget.NoteDrop(flow, dropped);
        return enqueued;
    }

    public ValueTask DisposeAsync()
    {
        // D11: the scope's single-flight covers only the drain, and the seal happens inside it, so a
        // precheck on IsSealed would be TOCTOU. The one-shot claim owns the teardown; every caller
        // joins the drain, which the teardown body reaches last.
        return Interlocked.Exchange(ref _disposeStarted, 1) != 0
            ? new ValueTask(_scope.DrainAsync())
            : new ValueTask(DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _scope.Cancel();
        UdpSessionSlot[] slots;
        lock (_gate)
        {
            NoteGateEntry();
            slots = [.. _sessions.Values];
            _sessions.Clear();
            // Every slot is drained below; the ready-path cache is wiped whole because no entry can
            // survive the drain, so no per-entry ReferenceEquals guard is meaningful here.
            Array.Clear(_sessionCache);
            _cooldowns.Clear();
            // The cleared slots are unreachable for every other drain path (a setup task's
            // RemoveSlotAsync observes the removal first), so the queued datagrams are drained
            // here — the disposal edge must still credit every budget charge back exactly once.
            foreach (var slot in slots)
            {
                var dropped = 0;
                while (slot.SetupQueue.TryDequeue(out var drained, out var drainedLength, out _))
                {
                    _budget.Credit(drainedLength);
                    drained.Dispose();
                    dropped++;
                }

                if (dropped > 0) _budget.AddDroppedTotal(dropped);
            }
        }
        foreach (var slot in slots)
        {
            try
            {
                await slot.Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_scope.IsSealed)
            {
                GC.KeepAlive(slot.Completion);
            }
            catch (Exception) when (slot.Completion.IsFaulted || slot.Completion.IsCanceled)
            {
                GC.KeepAlive(slot.Completion);
            }

            if (slot.Session is { } session) await session.DisposeAsync().ConfigureAwait(false);
        }

        // D-C3-8: seal here — after every in-flight session's setup decision and teardown above, so
        // an already-started receive-failure teardown was admitted, and before the join below, so no
        // new one can start. DrainAsync both seals and joins the scope's children.
        await _scope.DrainAsync().ConfigureAwait(false);

        // Every started setup task was awaited above and released the limiter in its finally
        // tasks that start later observe the cancelled shutdown token before acquiring it.
        _setup.DisposeLimiter();
        // Every queued lease was drained above and every in-flight flush lease was released by
        // the awaited setup tasks. The rented pools and the setup executor are borrowed from
        // composition, which owns and disposes them after this coordinator's drain.
    }

    /// <summary>
    /// Attaches a constructed session to its slot under the coordinator gate; the setup pipeline
    /// calls this at the point where the session becomes visible to dispatch.
    /// </summary>
    void IUdpSessionSlotHost.AttachSession(UdpSessionSlot slot, UdpProxySession session)
    {
        lock (_gate)
        {
            NoteGateEntry();
            slot.Session = session;
        }
    }

    /// <summary>
    /// The setup pipeline's dial-start re-stamp step: re-stamps the slot's queued datagrams only
    /// while this slot is still the flow's registered owner. The pipeline counts the refreshed
    /// entries on its side.
    /// </summary>
    int IUdpSessionSlotHost.RefreshSetupStamps(FlowKey flow, UdpSessionSlot slot)
    {
        lock (_gate)
        {
            NoteGateEntry();
            return _sessions.TryGetValue(flow, out var current) && ReferenceEquals(current, slot)
                ? slot.SetupQueue.RefreshEnqueuedStamps(_timeProvider.GetUtcNow())
                : 0;
        }
    }

    /// <summary>
    /// One flush-dequeue step under the coordinator gate: verifies the slot is still the flow's
    /// registered owner, dequeues the next buffered datagram lease (releasing its budget charge
    /// exactly once; the caller releases the lease), or flips the slot ready when the queue has
    /// drained. The setup pipeline's flush loop calls this through the seam and performs the TTL
    /// check and the send below the gate.
    /// </summary>
    (UdpSessionSetup.FlushStep Step, NativeLease Lease, int Length, DateTimeOffset EnqueuedAt) IUdpSessionSlotHost.DequeueForFlush(FlowKey flow, UdpSessionSlot slot)
    {
        lock (_gate)
        {
            NoteGateEntry();
            // Another owner (receive failure, expiry, disposal, a replacement setup) took over
            // this slot's teardown and owns the session and the queued datagrams.
            if (!_sessions.TryGetValue(flow, out var current) || !ReferenceEquals(current, slot)) return (UdpSessionSetup.FlushStep.NotOwner, default, 0, default);
            if (!slot.SetupQueue.TryDequeue(out var pending, out var length, out var enqueuedAt))
            {
                slot.Ready = true;
                PublishSessionSlot(flow, slot);
                return (UdpSessionSetup.FlushStep.QueueEmpty, default, 0, default);
            }

            // The datagram left the pending set under the coordinator gate: its budget
            // charge is released here, exactly once, regardless of the sink outcome.
            _budget.Credit(length);
            return (UdpSessionSetup.FlushStep.Dequeued, pending, length, enqueuedAt);
        }
    }

    /// <summary>
    /// Shared owns-slot removal protocol for every teardown path: under the gate, the slot mapped
    /// to <paramref name="flow"/> is removed only when it is still the exact slot instance
    /// <paramref name="slot"/> (a newer generation may have replaced it); the resolved
    /// session's association is released and any datagrams still queued for setup are dropped
    /// fail-closed in the same critical section. Only
    /// <see cref="UdpTeardownReason.SetupFailure"/> arms the setup-failure cooldown (and never
    /// during shutdown), so the next datagram does not immediately hammer a dead SOCKS5 server
    /// while an expiry, a transient fault, or a cancellation leaves the flow free to set up
    /// again. Session disposal runs outside the gate. Returns true when this caller owned the
    /// removal; per-call-site logging and expiry bookkeeping stay with callers.
    /// </summary>
    async Task<bool> IUdpSessionSlotHost.RemoveSlotAsync(FlowKey flow, UdpSessionSlot slot, UdpTeardownReason reason)
    {
        UdpProxySession? session = null;
        var owned = false;
        lock (_gate)
        {
            NoteGateEntry();
            if (_sessions.TryGetValue(flow, out var current) && ReferenceEquals(current, slot))
            {
                _sessions.Remove(flow);
                ClearSessionSlot(flow, slot);
                owned = true;
                session = slot.Session;
                if (session is not null) _associations.TryRemove(session.Association);
                var dropped = 0;
                while (slot.SetupQueue.TryDequeue(out var drained, out var drainedLength, out _))
                {
                    _budget.Credit(drainedLength);
                    drained.Dispose();
                    dropped++;
                }
                if (dropped > 0) _budget.NoteDrop(flow, dropped);
                if (reason == UdpTeardownReason.SetupFailure && !_scope.IsSealed)
                {
                    _cooldowns.Write(flow, _timeProvider.GetUtcNow());
                }
            }
        }

        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        return owned;
    }

    /// <summary>
    /// The receive-failure signal (F1): starts the teardown as a child of the coordinator's scope,
    /// which tracks it, joins it in <see cref="DisposeCoreAsync"/>, and records its fault — that is
    /// what replaces the former fire-and-forget list. A sealed scope refuses the child, which is
    /// safe because disposal disposes every session anyway.
    /// </summary>
    void IUdpSessionSlotHost.RemoveReceiveFailedSession(UdpProxySession session)
        => _scope.Run(_ => RemoveReceiveFailedSessionAsync(session), "udp.receive-failure");

    private async Task RemoveReceiveFailedSessionAsync(UdpProxySession session)
    {
        try
        {
            await RemoveReceiveFailedSessionCoreAsync(session).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Run records the child's fault but cannot log this domain-specific warning (D-C3-9)
            // the record keeps the child observed either way.
            var error = exception.GetType().Name;
            var detail = exception.Message;
            UdpProxyLog.UdpReceiveFailureTeardownFaulted(_logger, error, detail);
            throw;
        }
    }

    /// <summary>
    /// Removes the slot of a session whose receive loop recorded a fault, classifying that fault
    /// through the same <see cref="TeardownReasonFor"/> every other removal path uses: a rejected
    /// relay handshake discovered on the receive path tears the flow down as
    /// <see cref="UdpTeardownReason.SetupFailure"/> (the setup cooldown is armed), an association
    /// death as <see cref="UdpTeardownReason.AssociationLost"/>, and every other fault as the generic
    /// <see cref="UdpTeardownReason.Fault"/>.
    /// <para>
    /// The removal is also the classification's owner check: a fault whose slot is already gone — a
    /// send-path failure removed it first — returns before reaching the classifier, so one fault is
    /// never classified (or counted) twice across the two paths.
    /// </para>
    /// </summary>
    private async Task RemoveReceiveFailedSessionCoreAsync(UdpProxySession session)
    {
        UdpSessionSlot? slot = null;
        lock (_gate)
        {
            NoteGateEntry();
            if (_sessions.TryGetValue(session.Flow, out var current) && ReferenceEquals(current.Session, session)) slot = current;
        }
        if (slot is null) return;
        // A null fault cannot reach here (the receive loop signals only after it recorded one); the
        // unconditional removal keeps the generic reason if it ever did.
        var reason = session.RecordedFault is { } fault ? TeardownReasonFor(fault) : UdpTeardownReason.Fault;
        if (!await _slotHost.RemoveSlotAsync(session.Flow, slot, reason).ConfigureAwait(false)) return;
        var flow = session.Flow;
        UdpProxyLog.UdpSessionClosed(_logger, session.FlowGeneration == 0 ? null : session.FlowGeneration, session.Association.Generation, flow.Protocol, flow.Local, flow.Remote);
    }

    /// <summary>
    /// One flow's session lifecycle slot: the single ownership point in the session dictionary.
    /// The completion task covers the background setup and the FIFO flush of datagrams buffered
    /// while setup was in flight; <see cref="Ready"/> (set under the coordinator gate in the same
    /// critical section as the queue-empty check) marks the transition to inline sends. Internal
    /// because the setup pipeline's coordinator-delegate seam carries it.
    /// </summary>
    internal sealed class UdpSessionSlot
    {
        public Task Completion = Task.CompletedTask;
        public readonly BoundedSetupQueue SetupQueue = new(SetupQueueMaximumPackets, SetupQueueMaximumBytes);

        /// <summary>
        /// The attached session, published before <see cref="Ready"/> (both under the coordinator gate), so
        /// a lock-free reader that validates the session's flow key and then reads <see cref="Ready"/> is
        /// guaranteed the pair.
        /// </summary>
        public UdpProxySession? Session
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public bool Ready
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }
    }
}
