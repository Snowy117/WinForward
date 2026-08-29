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
    private const int MaximumConcurrentSetups = 8;
    private static readonly TimeSpan SetupFailureCooldown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SetupQueueDropLogInterval = TimeSpan.FromSeconds(5);

    private readonly IUdpProxyTransportFactory _transportFactory;
    private readonly IUdpResponseSink _responseSink;
    private readonly UdpAssociationTable _associations;
    private readonly Dictionary<FlowKey, UdpSessionSlot> _sessions = [];
    private readonly Dictionary<FlowKey, DateTimeOffset> _setupTombstones = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _setupLimiter = new(MaximumConcurrentSetups, MaximumConcurrentSetups);
    private readonly int _capacity;
    private readonly TimeProvider _timeProvider;
    private readonly Func<ValueTask>? _beforeExpiryRecheck;
    private readonly IRuntimeLogger _logger;
    private readonly ArrayPool<byte> _receiveBufferPool;
    private readonly int _receiveBufferSize;
    private long _lastSetupQueueDropLogTicks;
    private long _setupQueueDroppedTotal;
    private Task? _disposeTask;
    private bool _disposed;

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
        ArrayPool<byte>? receiveBufferPool = null)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(responseSink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumFrameSize <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFrameSize));
        _transportFactory = transportFactory;
        _responseSink = responseSink;
        _associations = new UdpAssociationTable(capacity);
        _capacity = capacity;
        _timeProvider = timeProvider;
        _beforeExpiryRecheck = beforeExpiryRecheck;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _receiveBufferPool = receiveBufferPool ?? ArrayPool<byte>.Shared;
        _receiveBufferSize = checked(maximumFrameSize + MaximumSocks5UdpHeaderSize + OversizeSentinelSize);
    }

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
            if (_setupTombstones.TryGetValue(flow, out var retryAt))
            {
                if (now < retryAt)
                {
                    if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogTrace("udp.setup.cooldown", flow, new RuntimeLogField("reason", "cooldown"));
                    return ValueTask.FromResult(false);
                }

                _setupTombstones.Remove(flow);
            }

            if (!_sessions.TryGetValue(flow, out var slot))
            {
                if (_sessions.Count >= _capacity)
                {
                    if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogTrace("udp.session.rejected", flow, new RuntimeLogField("reason", "capacity"));
                    return ValueTask.FromResult(false);
                }

                slot = new UdpSessionSlot();
                var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // The client MAC rides in the pump's native batch slot; copy it before the
                // background task can outlive the dispatch. The setup task itself runs off the
                // pump AND off the coordinator gate, so even a synchronously completing factory
                // never blocks other flows' dispatch.
                var capturedClientMac = clientMac.IsEmpty ? null : clientMac.ToArray();
                slot.Completion = Task.Run(() => CreateSessionAsync(flow, server, flowGeneration, capturedClientMac, _shutdown.Token, registered.Task, slot));
                _sessions.Add(flow, slot);
                registered.TrySetResult();
                TrackFailedSetup(flow, slot);
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
                LogTrace("udp.packet.sent", flow, new RuntimeLogField("packet", packetSequence == 0 ? null : packetSequence), new RuntimeLogField("flow", session.FlowGeneration == 0 ? null : session.FlowGeneration), new RuntimeLogField("bytes", payload.Length), new RuntimeLogField("udpAssociation", session.Association.Generation));
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
            await RemoveSlotAsync(flow, slot, writeTombstone: false).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Buffers one datagram while the flow's session is setting up or flushing. The queue is
    /// bounded (packets and bytes); on overflow the oldest datagrams are dropped so the freshest
    /// state survives, with drops surfaced through the trace event and a rate-limited debug
    /// summary. Returns true when the datagram (or a newer one in its place) is retained.
    /// </summary>
    private bool EnqueueSetupDatagram(FlowKey flow, UdpSessionSlot slot, ReadOnlyMemory<byte> payload)
    {
        // The captured frame lives in the pump's native batch slot only for the dispatch; the
        // setup queue copies every datagram so buffered traffic survives the pump moving on.
        var enqueued = slot.SetupQueue.TryEnqueue(payload);
        var dropped = 0;
        while (!enqueued && slot.SetupQueue.TryDequeue(out _))
        {
            dropped++;
            enqueued = slot.SetupQueue.TryEnqueue(payload);
        }

        if (!enqueued) dropped++;
        if (dropped > 0) NoteSetupQueueDrop(flow, dropped);
        return enqueued;
    }

    private void NoteSetupQueueDrop(FlowKey flow, int dropped)
    {
        if (dropped <= 0) return;
        Interlocked.Add(ref _setupQueueDroppedTotal, dropped);
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogTrace("udp.setupqueue.dropped", flow, new RuntimeLogField("dropped", dropped));
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastSetupQueueDropLogTicks);
        if (now - last >= SetupQueueDropLogInterval.Ticks && Interlocked.CompareExchange(ref _lastSetupQueueDropLogTicks, now, last) == last)
        {
            _logger.Debug($"UDP session setup queues dropped {Interlocked.Read(ref _setupQueueDroppedTotal)} datagram(s) total (drop-oldest).");
        }
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
            _setupTombstones.Clear();
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
        _setupLimiter.Dispose();
        _shutdown.Dispose();
    }

    private async Task CreateSessionAsync(FlowKey flow, Socks5Server server, long flowGeneration, byte[]? clientMac, CancellationToken cancellationToken, Task registered, UdpSessionSlot slot)
    {
        // Patient admission: a flash crowd of new flows must queue behind the 8-wide setup
        // gate rather than fail into the cooldown tombstone, because a failed setup's teardown
        // drains the setup queue and drops the already-accepted triggering datagram (that
        // drop, not the steady-state relay, was the entire measured in-window soak loss).
        // Queued setups observe shutdown cancellation here (no tombstone); genuine setup
        // failures below still fail closed with the cooldown tombstone.
        await _setupLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        IUdpProxyTransport? transport = null;
        UdpAssociation? association = null;
        var associationCreated = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            transport = await _transportFactory.CreateAsync(server, cancellationToken).ConfigureAwait(false);
            var relayAlias = new RelayAlias(FlowKey.Create(
                Endpoint.From(transport.LocalEndpoint.Address, checked((ushort)transport.LocalEndpoint.Port)),
                Endpoint.From(transport.RelayEndpoint.Address, checked((ushort)transport.RelayEndpoint.Port)),
                TransportProtocol.Udp,
                flow.Origin));
            if (!_associations.TryClaim(flow, relayAlias, _timeProvider.GetUtcNow(), out association, out associationCreated) || association is null)
            {
                throw new IOException("UDP relay alias collision with another flow; blocking the flow.");
            }

            if (!associationCreated || association.RelayAlias != relayAlias)
            {
                throw new IOException("UDP flow association was already owned by another session; blocking the flow.");
            }

            var session = new UdpProxySession(flow, flowGeneration, association, transport, _responseSink, clientMac, cancellationToken, _timeProvider, OnSessionActivity, _logger, _receiveBufferPool, _receiveBufferSize);
            transport = null;
            lock (_gate) slot.Session = session;
            session.Start(RemoveReceiveFailedSessionAsync, registered);
            LogDebug("udp.session.created", flow, flowGeneration, association, server.Name);
            await FlushSetupQueueAsync(flow, slot, session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (associationCreated && association is not null) _associations.TryRemove(association);
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _setupLimiter.Release();
        }
    }

    /// <summary>
    /// Drains the flow's setup queue in FIFO order through the live session, then flips the slot
    /// to ready. The queue-empty check and the ready transition share one critical section, so a
    /// concurrent send either enqueued before this loop finished (and is drained here) or sees
    /// the ready slot and sends inline: no datagram can bypass the queue and then be followed by
    /// an older queued one. A send failure propagates to the setup task's failure observer.
    /// </summary>
    private async Task FlushSetupQueueAsync(FlowKey flow, UdpSessionSlot slot, UdpProxySession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadOnlyMemory<byte> pending;
            lock (_gate)
            {
                // Another owner (receive failure, expiry, disposal, a replacement setup) took over
                // this slot's teardown and owns the session and the queued datagrams.
                if (!_sessions.TryGetValue(flow, out var current) || !ReferenceEquals(current, slot)) return;
                if (!slot.SetupQueue.TryDequeue(out pending))
                {
                    slot.Ready = true;
                    return;
                }
            }

            await session.SendAsync(flow.Remote, pending, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes sessions whose last send or receive is older than <paramref name="idleTimeout"/>
    /// and releases their associations, so short-lived DNS/QUIC-style flows do not accumulate to
    /// the bounded capacity. Also prunes expired setup-failure cooldown tombstones. Idle expiry
    /// (design §7/§8) runs on a periodic sweep in the runtime.
    /// </summary>
    public async ValueTask<int> RemoveExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout)
    {
        (UdpSessionSlot Slot, UdpProxySession Session)[] idle;
        lock (_gate)
        {
            PruneExpiredTombstones(now);
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
            if (!await RemoveSlotAsync(session.Flow, slot, writeTombstone: false).ConfigureAwait(false))
            {
                session.CancelExpiry();
                continue;
            }
            LogDebug("udp.session.expired", session.Flow, session.FlowGeneration, session.Association, null);
            removed++;
        }

        return removed;
    }

    private void PruneExpiredTombstones(DateTimeOffset now)
    {
        if (_setupTombstones.Count == 0) return;
        List<FlowKey>? expired = null;
        foreach (var pair in _setupTombstones)
        {
            if (pair.Value <= now) (expired ??= []).Add(pair.Key);
        }

        if (expired is null) return;
        foreach (var flow in expired) _setupTombstones.Remove(flow);
    }

    /// <summary>
    /// Shared owns-slot removal protocol for every teardown path: under the gate, the slot mapped
    /// to <paramref name="flow"/> is removed only when it is still the exact slot instance
    /// <paramref name="expected"/> (a newer generation may have replaced it); the resolved
    /// session's association is released and any datagrams still queued for setup are dropped
    /// fail-closed in the same critical section. When <paramref name="writeTombstone"/> is set
    /// and the coordinator is not shutting down, a setup-failure cooldown tombstone is written so
    /// the next datagram does not immediately hammer a dead SOCKS5 server. Session disposal runs
    /// outside the gate. Returns true when this caller owned the removal; per-call-site logging
    /// and expiry bookkeeping stay with callers.
    /// </summary>
    private async Task<bool> RemoveSlotAsync(FlowKey flow, UdpSessionSlot expected, bool writeTombstone)
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
                while (expected.SetupQueue.TryDequeue(out _)) dropped++;
                if (dropped > 0) NoteSetupQueueDrop(flow, dropped);
                if (writeTombstone && !_shutdown.IsCancellationRequested)
                {
                    _setupTombstones[flow] = _timeProvider.GetUtcNow() + SetupFailureCooldown;
                }
            }
        }

        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        return owned;
    }

    private void TrackFailedSetup(FlowKey flow, UdpSessionSlot slot)
    {
        _ = ObserveFailedSetupAsync(flow, slot);
    }

    private async Task ObserveFailedSetupAsync(FlowKey flow, UdpSessionSlot slot)
    {
        try
        {
            await slot.Completion.ConfigureAwait(false);
        }
        catch (Exception exception) when (slot.Completion.IsFaulted || slot.Completion.IsCanceled)
        {
            LogSetupFailure(flow, exception);
            await RemoveSlotAsync(flow, slot, writeTombstone: slot.Completion.IsFaulted).ConfigureAwait(false);
        }
    }

    private void LogSetupFailure(FlowKey flow, Exception exception)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Debug)) return;
        _logger.Event(RuntimeLogLevel.Debug, "udp.setup.failed",
            new("protocol", flow.Protocol),
            new("source", flow.Local),
            new("destination", flow.Remote),
            new("reason", exception.GetType().Name));
    }

    private void OnSessionActivity(UdpAssociation association, DateTimeOffset now) => _associations.TryTouch(association, now);

    private async Task RemoveReceiveFailedSessionAsync(UdpProxySession session)
    {
        UdpSessionSlot? slot = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(session.Flow, out var current) && ReferenceEquals(current.Session, session)) slot = current;
        }
        if (slot is null) return;
        if (!await RemoveSlotAsync(session.Flow, slot, writeTombstone: false).ConfigureAwait(false)) return;
        LogDebug("udp.session.closed", session.Flow, session.FlowGeneration, session.Association, null);
    }

    private void LogDebug(string eventName, FlowKey flow, long flowGeneration, UdpAssociation association, string? serverName)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Debug)) return;
        _logger.Event(RuntimeLogLevel.Debug, eventName,
            new("flow", flowGeneration == 0 ? null : flowGeneration),
            new("udpAssociation", association.Generation), new("protocol", flow.Protocol),
            new("source", flow.Local), new("destination", flow.Remote), new("proxy", serverName));
    }

    private void LogTrace(string eventName, FlowKey flow, params RuntimeLogField[] additional)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Trace)) return;
        var fields = new RuntimeLogField[additional.Length + 3];
        fields[0] = new("protocol", flow.Protocol);
        fields[1] = new("source", flow.Local);
        fields[2] = new("destination", flow.Remote);
        additional.CopyTo(fields, 3);
        _logger.Event(RuntimeLogLevel.Trace, eventName, fields);
    }

    /// <summary>
    /// One flow's session lifecycle slot: the single ownership point in the session dictionary.
    /// The completion task covers the background setup and the FIFO flush of datagrams buffered
    /// while setup was in flight; <see cref="Ready"/> (set under the coordinator gate in the same
    /// critical section as the queue-empty check) marks the transition to inline sends.
    /// </summary>
    private sealed class UdpSessionSlot
    {
        public Task Completion = Task.CompletedTask;
        public readonly BoundedSetupQueue SetupQueue = new(SetupQueueMaximumPackets, SetupQueueMaximumBytes);
        public UdpProxySession? Session;
        public bool Ready;
    }
}
