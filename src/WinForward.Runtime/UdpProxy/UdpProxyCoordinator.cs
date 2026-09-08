using System.Buffers;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;

namespace WinForward.Runtime.UdpProxy;

public sealed class UdpProxyCoordinator : IAsyncDisposable
{
    private const int MaximumSocks5UdpHeaderSize = 22;
    private const int OversizeSentinelSize = 1;
    private const int SetupQueueMaximumPackets = 32;
    private const int SetupQueueMaximumBytes = 32_768;

    private readonly UdpAssociationTable _associations;
    private readonly Dictionary<FlowKey, UdpSessionSlot> _sessions;
    private readonly UdpSetupCooldownTable _cooldowns;
    private readonly UdpSetupQueueBudget _budget;
    private readonly UdpSessionSetup _setup;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _capacity;
    private readonly TimeProvider _timeProvider;
    private readonly Func<ValueTask>? _beforeExpiryRecheck;
    private readonly IRuntimeLogger _logger;
    private Task? _disposeTask;
    private bool _disposed;

    /// <summary>Upper bound on eagerly seeded dictionary capacity; growth beyond it stays lazy.</summary>
    private const int MaximumPreSeedCapacity = 1_024;

    public UdpProxyCoordinator(
        IUdpProxyTransportFactory transportFactory,
        IUdpResponseSink responseSink,
        int capacity = 16_384,
        IRuntimeLogger? logger = null,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame)
        : this(transportFactory, responseSink, capacity, TimeProvider.System, null, logger, maximumFrameSize)
    {
    }

    internal UdpProxyCoordinator(
        IUdpProxyTransportFactory transportFactory,
        IUdpResponseSink responseSink,
        int capacity,
        TimeProvider timeProvider,
        Func<ValueTask>? beforeExpiryRecheck,
        IRuntimeLogger? logger = null,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame,
        ArrayPool<byte>? receiveBufferPool = null,
        long setupQueueGlobalByteBudget = UdpSetupQueueBudget.SetupQueueGlobalByteBudget)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(responseSink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumFrameSize <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFrameSize));
        if (setupQueueGlobalByteBudget <= 0) throw new ArgumentOutOfRangeException(nameof(setupQueueGlobalByteBudget));
        var preSeed = Math.Min(capacity, MaximumPreSeedCapacity);
        _associations = new UdpAssociationTable(capacity, preSeed);
        _sessions = new Dictionary<FlowKey, UdpSessionSlot>(preSeed);
        _cooldowns = new UdpSetupCooldownTable(capacity);
        _capacity = capacity;
        _timeProvider = timeProvider;
        _beforeExpiryRecheck = beforeExpiryRecheck;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _budget = new UdpSetupQueueBudget(setupQueueGlobalByteBudget, _logger);
        _setup = new UdpSessionSetup(
            transportFactory,
            _associations,
            responseSink,
            timeProvider,
            _logger,
            receiveBufferPool ?? ArrayPool<byte>.Shared,
            checked(maximumFrameSize + MaximumSocks5UdpHeaderSize + OversizeSentinelSize),
            RefreshSetupStampsUnderGate,
            AttachSessionUnderGate,
            DequeueForFlush,
            RemoveSlotAsync,
            RemoveReceiveFailedSessionAsync);
    }

    /// <summary>The live setup-failure cooldown count (bounded by <c>capacity</c>); for tests and diagnostics.</summary>
    internal int SetupCooldownCountForDiagnostics => _cooldowns.Count;

    /// <summary>The aggregate setup-queue bytes currently charged against the global budget; for tests and diagnostics.</summary>
    internal long PendingSetupBytesForDiagnostics => _budget.PendingBytes;

    /// <summary>The total datagrams rejected because the global setup byte budget was exhausted; for tests and diagnostics.</summary>
    internal long SetupBudgetRejectionCount => _budget.RejectionCount;

    /// <summary>The total buffered datagrams dropped at flush for exceeding the setup TTL; for tests and diagnostics.</summary>
    internal long SetupTtlExpiredCount => _setup.TtlExpiredCount;

    /// <summary>The total queue entries re-stamped at setup dial start (limiter queue-wait does not age a datagram); for tests and diagnostics.</summary>
    internal long SetupStampsRefreshedCount => _setup.StampsRefreshedCount;

    /// <summary>
    /// Hands a datagram to the flow's relay session without ever awaiting session setup network
    /// I/O (R1): the first datagram of a flow starts the SOCKS5 handshake in the background and
    /// is buffered in a bounded drop-oldest setup queue; datagrams on a ready session are sent
    /// inline on the caller's await, preserving the steady-state dispatch shape. Returns true
    /// when the datagram was accepted (sent or buffered); false means capacity, cooldown, or an
    /// unbufferable datagram rejected it (fail-closed, traced).
    /// </summary>
    public ValueTask<bool> TrySendAsync(FlowKey flow, Socks5Server server, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, long packetSequence = 0, long flowGeneration = 0, ReadOnlyMemory<byte> clientMac = default)
    {
        if (flow.Protocol != TransportProtocol.Udp) throw new ArgumentException("UDP coordinator accepts only UDP flow keys.", nameof(flow));

        UdpSessionSlot? readySlot = null;
        UdpProxySession? readySession = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            if (_cooldowns.TryHit(flow, now))
            {
                if (_logger.IsEnabled(RuntimeLogLevel.Trace)) UdpProxyLogging.LogTrace(_logger, "udp.setup.cooldown", flow, new RuntimeLogField("reason", "cooldown"));
                return ValueTask.FromResult(false);
            }

            if (!_sessions.TryGetValue(flow, out var slot))
            {
                if (_sessions.Count >= _capacity)
                {
                    if (_logger.IsEnabled(RuntimeLogLevel.Trace)) UdpProxyLogging.LogTrace(_logger, "udp.session.rejected", flow, new RuntimeLogField("reason", "capacity"));
                    return ValueTask.FromResult(false);
                }

                slot = new UdpSessionSlot();
                // The client MAC rides in the pump's native batch slot; copy it before the
                // background task can outlive the dispatch. The setup task itself runs off the
                // pump AND off the coordinator gate, so even a synchronously completing factory
                // never blocks other flows' dispatch. Registration ordering (the slot must be
                // in _sessions before the session's receive-failure handler can run) is carried
                // by this gate: the setup pipeline's attach step (which takes this gate via its
                // delegate) can only be acquired after this critical section (including the Add
                // below) has released it.
                var capturedClientMac = clientMac.IsEmpty ? null : clientMac.ToArray();
                slot.Completion = Task.Run(() => _setup.CreateSessionAsync(flow, server, flowGeneration, capturedClientMac, _shutdown.Token, slot));
                _sessions.Add(flow, slot);
            }

            if (slot.Ready)
            {
                readySlot = slot;
                readySession = slot.Session!;
            }
            else
            {
                return ValueTask.FromResult(EnqueueSetupDatagram(flow, slot, payload));
            }
        }

        return SendOnReadySessionAsync(flow, readySlot!, readySession!, payload, cancellationToken, packetSequence);
    }

    private async ValueTask<bool> SendOnReadySessionAsync(FlowKey flow, UdpSessionSlot slot, UdpProxySession session, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, long packetSequence)
    {
        try
        {
            await session.SendAsync(flow.Remote, payload, cancellationToken).ConfigureAwait(false);
            if (_logger.IsEnabled(RuntimeLogLevel.Trace))
            {
                UdpProxyLogging.LogTrace(_logger, "udp.packet.sent", flow, new RuntimeLogField("packet", packetSequence == 0 ? null : packetSequence), new RuntimeLogField("flow", session.FlowGeneration == 0 ? null : session.FlowGeneration), new RuntimeLogField("bytes", payload.Length), new RuntimeLogField("udpAssociation", session.Association.Generation));
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            // Cancellation from this individual caller is not evidence that the
            // shared UDP transport failed.
            throw;
        }
        catch
        {
            await RemoveSlotAsync(flow, slot, writeCooldown: false).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Buffers one datagram while the flow's session is setting up or flushing. The queue is
    /// bounded (packets and bytes); on overflow the oldest datagrams are dropped so the freshest
    /// state survives, with drops surfaced through the trace event and a rate-limited debug
    /// summary. A datagram that does not fit the global setup byte budget is rejected without
    /// arming a setup cooldown — budget exhaustion is backpressure, not a setup failure. Returns
    /// true when the datagram (or a newer one in its place) is retained.
    /// </summary>
    private bool EnqueueSetupDatagram(FlowKey flow, UdpSessionSlot slot, ReadOnlyMemory<byte> payload)
    {
        // R4: charge the global budget before the per-flow enqueue. Every charged byte is
        // credited back exactly once at whichever sink dequeues it: the flush below, the
        // drop-oldest loop here, the slot drain (RemoveSlotAsync), or the dispose drain.
        if (!_budget.TryCharge(payload.Length))
        {
            _budget.NoteDrop(flow, 1);
            return false;
        }

        // The captured frame lives in the pump's native batch slot only for the dispatch; the
        // setup queue copies every datagram so buffered traffic survives the pump moving on.
        var enqueued = slot.SetupQueue.TryEnqueue(payload, _timeProvider.GetUtcNow());
        var dropped = 0;
        while (!enqueued && slot.SetupQueue.TryDequeue(out var evicted, out _))
        {
            dropped++;
            _budget.Credit(evicted.Length);
            enqueued = slot.SetupQueue.TryEnqueue(payload, _timeProvider.GetUtcNow());
        }

        if (!enqueued)
        {
            // The per-flow bounds refused the datagram outright: release its charge.
            dropped++;
            _budget.Credit(payload.Length);
        }
        if (dropped > 0) _budget.NoteDrop(flow, dropped);
        return enqueued;
    }

    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        TaskCompletionSource? completion = null;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
            }
            disposeTask = _disposeTask;
        }

        if (completion is not null)
        {
            try
            {
                await DisposeCoreAsync().ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        await disposeTask.ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        UdpSessionSlot[] slots;
        lock (_gate)
        {
            slots = _sessions.Values.ToArray();
            _sessions.Clear();
            _cooldowns.Clear();
            // The cleared slots are unreachable for every other drain path (a setup task's
            // RemoveSlotAsync observes the removal first), so the queued datagrams are drained
            // here — the disposal edge must still credit every budget charge back exactly once.
            foreach (var slot in slots)
            {
                var dropped = 0;
                while (slot.SetupQueue.TryDequeue(out var drained, out _))
                {
                    _budget.Credit(drained.Length);
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
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                GC.KeepAlive(slot.Completion);
            }
            catch (Exception) when (slot.Completion.IsFaulted || slot.Completion.IsCanceled)
            {
                GC.KeepAlive(slot.Completion);
            }

            if (slot.Session is { } session) await session.DisposeAsync().ConfigureAwait(false);
        }

        // Every started setup task was awaited above and released the limiter in its finally;
        // tasks that start later observe the cancelled shutdown token before acquiring it.
        _setup.DisposeLimiter();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Attaches a constructed session to its slot under the coordinator gate; the delegate the
    /// setup pipeline calls at the point where the session becomes visible to dispatch.
    /// </summary>
    private void AttachSessionUnderGate(UdpSessionSlot slot, UdpProxySession session)
    {
        lock (_gate) slot.Session = session;
    }

    /// <summary>
    /// The setup pipeline's dial-start re-stamp step: re-stamps the slot's queued datagrams only
    /// while this slot is still the flow's registered owner. The pipeline counts the refreshed
    /// entries on its side.
    /// </summary>
    private int RefreshSetupStampsUnderGate(FlowKey flow, UdpSessionSlot slot)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(flow, out var current) && ReferenceEquals(current, slot)
                ? slot.SetupQueue.RefreshEnqueuedStamps(_timeProvider.GetUtcNow())
                : 0;
        }
    }

    /// <summary>
    /// One flush-dequeue step under the coordinator gate: verifies the slot is still the flow's
    /// registered owner, dequeues the next buffered datagram (releasing its budget charge exactly
    /// once), or flips the slot ready when the queue has drained. The setup pipeline's flush loop
    /// calls this via delegate and performs the TTL check and the send below the gate.
    /// </summary>
    private (UdpSessionSetup.FlushStep Step, ReadOnlyMemory<byte> Pending, DateTimeOffset EnqueuedAt) DequeueForFlush(FlowKey flow, UdpSessionSlot slot)
    {
        lock (_gate)
        {
            // Another owner (receive failure, expiry, disposal, a replacement setup) took over
            // this slot's teardown and owns the session and the queued datagrams.
            if (!_sessions.TryGetValue(flow, out var current) || !ReferenceEquals(current, slot)) return (UdpSessionSetup.FlushStep.NotOwner, default, default);
            if (!slot.SetupQueue.TryDequeue(out var pending, out var enqueuedAt))
            {
                slot.Ready = true;
                return (UdpSessionSetup.FlushStep.QueueEmpty, default, default);
            }

            // The datagram left the pending set under the coordinator gate: its budget
            // charge is released here, exactly once, regardless of the sink outcome.
            _budget.Credit(pending.Length);
            return (UdpSessionSetup.FlushStep.Dequeued, pending, enqueuedAt);
        }
    }

    /// <summary>
    /// Removes sessions whose last send or receive is older than <paramref name="idleTimeout"/>
    /// and releases their associations, so short-lived DNS/QUIC-style flows do not accumulate to
    /// the bounded capacity. Also prunes expired setup cooldowns. Idle expiry
    /// (design §7/§8) runs on a periodic sweep in the runtime.
    /// </summary>
    public async ValueTask<int> RemoveExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout)
    {
        (UdpSessionSlot Slot, UdpProxySession Session)[] idle;
        lock (_gate)
        {
            _cooldowns.PruneExpired(now);
            idle = _sessions.Values
                .Where(slot => slot.Session is { } session && now - session.LastActivityUtc >= idleTimeout)
                .Select(slot => (slot, slot.Session!))
                .ToArray();
        }

        if (idle.Length > 0 && _beforeExpiryRecheck is not null) await _beforeExpiryRecheck().ConfigureAwait(false);

        var removed = 0;
        foreach (var (slot, session) in idle)
        {
            if (!session.TryBeginExpiry(now, idleTimeout)) continue;
            if (!await RemoveSlotAsync(session.Flow, slot, writeCooldown: false).ConfigureAwait(false))
            {
                session.CancelExpiry();
                continue;
            }
            UdpProxyLogging.LogDebug(_logger, "udp.session.expired", session.Flow, session.FlowGeneration, session.Association, null);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Shared owns-slot removal protocol for every teardown path: under the gate, the slot mapped
    /// to <paramref name="flow"/> is removed only when it is still the exact slot instance
    /// <paramref name="expected"/> (a newer generation may have replaced it); the resolved
    /// session's association is released and any datagrams still queued for setup are dropped
    /// fail-closed in the same critical section. When <paramref name="writeCooldown"/> is set
    /// and the coordinator is not shutting down, a setup-failure cooldown is armed so
    /// the next datagram does not immediately hammer a dead SOCKS5 server. Session disposal runs
    /// outside the gate. Returns true when this caller owned the removal; per-call-site logging
    /// and expiry bookkeeping stay with callers.
    /// </summary>
    private async Task<bool> RemoveSlotAsync(FlowKey flow, UdpSessionSlot expected, bool writeCooldown)
    {
        UdpProxySession? session = null;
        var owned = false;
        lock (_gate)
        {
            if (_sessions.TryGetValue(flow, out var current) && ReferenceEquals(current, expected))
            {
                _sessions.Remove(flow);
                owned = true;
                session = expected.Session;
                if (session is not null) _associations.TryRemove(session.Association);
                var dropped = 0;
                while (expected.SetupQueue.TryDequeue(out var drained, out _))
                {
                    _budget.Credit(drained.Length);
                    dropped++;
                }
                if (dropped > 0) _budget.NoteDrop(flow, dropped);
                if (writeCooldown && !_shutdown.IsCancellationRequested)
                {
                    _cooldowns.Write(flow, _timeProvider.GetUtcNow());
                }
            }
        }

        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        return owned;
    }

    private async Task RemoveReceiveFailedSessionAsync(UdpProxySession session)
    {
        UdpSessionSlot? slot = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(session.Flow, out var current) && ReferenceEquals(current.Session, session)) slot = current;
        }
        if (slot is null) return;
        if (!await RemoveSlotAsync(session.Flow, slot, writeCooldown: false).ConfigureAwait(false)) return;
        UdpProxyLogging.LogDebug(_logger, "udp.session.closed", session.Flow, session.FlowGeneration, session.Association, null);
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
        public UdpProxySession? Session;
        public bool Ready;
    }
}
