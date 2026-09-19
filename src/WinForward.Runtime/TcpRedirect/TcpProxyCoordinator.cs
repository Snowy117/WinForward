using System.Runtime.ExceptionServices;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Windows;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// Coordinates the transparent TCP redirect data path described in design §8 behind abstraction seams,
/// mirroring <see cref="UdpProxyCoordinator"/>. For each proxy-selected TCP flow it: allocates a local
/// listener, claims the flow exactly once in the redirect table, rewrites the SYN destination toward
/// the listener, registers the listener tuple in the loop-prevention registry, injects the rewritten
/// frame, and runs a background accept-and-relay loop. A proxy-selected flow is never silently passed:
/// every listener-allocation, claim, rewrite, injection, and relay-setup failure fails closed and
/// releases the listener, the table alias, and the self-traffic token. New-flow SYN setup never blocks
/// the capture pump (R8, mirroring the UDP contract): the pump-side handler retains a bounded copy of
/// the SYN in <see cref="TcpPendingSynSetupIndex"/> and returns <see cref="TcpRedirectOutcome.SetupPending"/>,
/// and a background task performs the allocation/claim/rewrite/injection under the store's setup gate.
/// The data path is delegated to focused modules: <see cref="TcpRedirectSetup"/> (new-flow pipeline),
/// <see cref="TcpRedirectSessionStore"/> (session lifecycle under one gate), <see cref="TcpRedirectAcceptor"/>
/// (accept/relay loop), and <see cref="ClientResetInjector"/> (client-visible failure surface); this
/// class owns entry routing.
/// </summary>
public sealed class TcpProxyCoordinator : IAsyncDisposable, ITcpReverseHandler
{
    private readonly TcpRedirectTable _table;
    private readonly ITcpRedirectInjector _injector;
    private readonly NdisPacketBufferPool _framePool;
    private readonly NativeBufferPool _synCopyPool;
    private readonly bool _ownsSynCopyPool;
    private readonly ISetupExecutor _setupExecutor;
    private readonly bool _ownsSetupExecutor;
    private readonly Func<SetupWorkItem, Task> _setupHandler;
    private readonly IRuntimeLogger _logger;
    private readonly int _capacity;
    private readonly TimeProvider _timeProvider;
    private readonly TcpRedirectSessionStore _store;
    private readonly TcpRedirectSetup _setup;
    private readonly ClientResetInjector _clientReset;
    private readonly TcpRedirectAcceptor _acceptor;
    private readonly TcpPendingSynSetupIndex _pendingSyn = new();
    private long _capacityRejectionCount;
    private long _reportedCapacityRejectionCount;

    public TcpProxyCoordinator(
        ITcpRedirectListenerFactory listenerFactory,
        ITcpProxyRelayFactory relayFactory,
        ITcpRedirectInjector injector,
        TcpRedirectTable table,
        SelfTrafficRegistry selfTraffic,
        IAdapterLocalAddressProvider localAddresses,
        TcpRedirectOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listenerFactory);
        ArgumentNullException.ThrowIfNull(relayFactory);
        ArgumentNullException.ThrowIfNull(injector);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentNullException.ThrowIfNull(localAddresses);
        options ??= new TcpRedirectOptions();
        var capacity = options.Capacity ?? 16_384;
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(options), capacity, "Capacity must be positive.");
        _table = table;
        _injector = injector;
        _framePool = NdisPacketBufferPool.Shared;
        _synCopyPool = options.SynCopyPool ?? new NativeBufferPool(NdisApiAbi.MaximumEthernetFrame);
        _ownsSynCopyPool = options.SynCopyPool is null;
        _setupExecutor = options.SetupExecutor ?? new SetupExecutor();
        _ownsSetupExecutor = options.SetupExecutor is null;
        _setupHandler = SetupPendingAsync;
        _logger = options.Logger ?? NullRuntimeLogger.Instance;
        _capacity = capacity;
        _timeProvider = options.TimeProvider;
        _store = new TcpRedirectSessionStore(table, _logger, _capacity, _timeProvider);
        _clientReset = new ClientResetInjector(injector, _logger, _store.TearDownSessionAsync, _store.FailAssociationAsync, _capacity, healthSignal: options.HealthSignal, timeProvider: _timeProvider);
        _acceptor = new TcpRedirectAcceptor(relayFactory, _logger, _clientReset, _store.TryAttachRelay, _store.TearDownSessionAsync);
        _setup = new TcpRedirectSetup(listenerFactory, table, selfTraffic, localAddresses, injector, _logger, _store, _clientReset, _synCopyPool, _timeProvider);
    }

    internal TcpRedirectTable Table => _table;

    /// <summary>The number of live redirect sessions (heartbeat diagnostics; gate-consistent).</summary>
    public int SessionCount => _store.SessionCount;

    /// <summary>The concurrent proxied-flow budget this coordinator was constructed with (heartbeat diagnostics).</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// The coordinator's observable counters as one snapshot: the concurrent-loser total from the
    /// redirect-table exactly-once path (a non-zero value after a concurrent burst proves that path
    /// was exercised under genuine concurrency), the capacity-gate rejection total (explicit budget
    /// management, not setup failure), and the pending-SYN-setup counts (live entries, charged
    /// bytes, retention-TTL expiries, setup-failure cooldowns); for tests and diagnostics.
    /// </summary>
    internal TcpRedirectDiagnostics Diagnostics => new(_setup.ConcurrentLoserCount, Interlocked.Read(ref _capacityRejectionCount), _pendingSyn.ActiveCount, _pendingSyn.ChargedBytes, _pendingSyn.TtlExpiredCount, _pendingSyn.CooldownCount);

    /// <summary>
    /// Emits an info-level summary of capacity-gate rejections, but only when the count advanced
    /// since the previous call. The idle-expiry sweeper invokes this on its existing periodic tick
    /// so no dedicated timer is introduced. Per-rejection trace events already exist
    /// (<c>tcp.redirect.rejected reason=capacity</c>); this is the info-level aggregate.
    /// </summary>
    internal void LogCapacitySummary()
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Info)) return;
        var total = Interlocked.Read(ref _capacityRejectionCount);
        var previouslyReported = Interlocked.Exchange(ref _reportedCapacityRejectionCount, total);
        if (total == previouslyReported) return;
        _logger.Event(RuntimeLogLevel.Info, "tcp.redirect.capacity",
            new("budget", _capacity),
            new("rejectedTotal", total),
            new("rejectedSinceLastSummary", total - previouslyReported));
    }

    public async ValueTask<TcpRedirectOutcome> HandleSynAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();
        ArgumentNullException.ThrowIfNull(server);
        ObjectDisposedException.ThrowIf(_store.IsDisposed, this);

        var key = packet.Context.Key;
        if (key.Protocol != TransportProtocol.Tcp)
        {
            throw new ArgumentException("TCP coordinator accepts only TCP flow keys.", nameof(packet));
        }

        // A flow already claimed by a prior SYN reuses its decision: touch the association and
        // re-inject the rewritten SYN toward the listener. Policy is evaluated exactly once.
        if (_table.TryResolveByOriginal(key, _timeProvider.GetUtcNow(), out var existing) && existing is not null)
        {
            TcpRedirectLogging.LogTrace(_logger, "tcp.redirect.reused", packet, existing);
            return await ReinjectExistingFlowDataAsync(packet, existing, cancellationToken).ConfigureAwait(false);
        }

        // TIME_WAIT grace: a same-tuple SYN whose redirect was torn down within the grace
        // window is a retransmission of the finished flow's handshake, never a fresh
        // connection — re-arming setup would honor the dead flow (or leak the straggler
        // toward the real server via the NotRelevant fallback), so it is consumed like every
        // other straggler. The next connection claims a new source port and a new key.
        if (_store.Tombstones.TryHit(key, _timeProvider.GetUtcNow())) return TcpRedirectOutcome.Dropped;

        // Setup-failure cooldown (R8): a redirect setup for this flow genuinely failed within
        // the last second — the failure path already logged and released its resources, and a
        // retransmitted SYN inside the window is consumed so a dead setup path is not re-armed
        // at the client's retransmission rate.
        if (_pendingSyn.IsInSetupCooldown(key, _timeProvider.GetUtcNow()))
        {
            TcpRedirectLogging.LogTrace(_logger, "tcp.setup.cooldown", packet, null, "cooldown");
            return TcpRedirectOutcome.Dropped;
        }

        // The capacity gate counts pending SYN setups alongside live sessions: each retained
        // entry becomes at most one session, so the budget holds even while setups are in
        // flight (the RST fast-fail below must not depend on background registration timing).
        if (_store.SessionCount + _pendingSyn.ActiveCount >= _capacity)
        {
            Interlocked.Increment(ref _capacityRejectionCount);
            TcpRedirectLogging.LogTrace(_logger, "tcp.redirect.rejected", packet, null, "capacity");
            // The client is still in SYN_SENT: an immediate RST|ACK fails its connect fast
            // (ECONNREFUSED) instead of a 20-60s retransmission timeout, and the per-tuple
            // cooldown keeps the guard amplification-free (S4).
            await _clientReset.InjectCapacityRejectedResetAsync(packet, cancellationToken).ConfigureAwait(false);
            return TcpRedirectOutcome.Blocked;
        }

        // New flow (R8): retain a materialized copy of the SYN and hand setup to a background
        // task, so the pump's strictly-ordered handler chain never waits on listener bind. The
        // copy is synchronous and inside the dispatch window (the pump's native batch slot is
        // recycled the moment this handler returns); every later step reads the retained copy.
        return StartPendingSetup(packet, key, server);
    }

    /// <summary>
    /// Retains the SYN copy and launches the background setup (R8). A retransmission inside the
    /// pending window overwrites the retained copy and never starts a second task. Returns
    /// <see cref="TcpRedirectOutcome.SetupPending"/> on accept or <see cref="TcpRedirectOutcome.Blocked"/>
    /// when the bounded pending index refuses the retain.
    /// </summary>
    private TcpRedirectOutcome StartPendingSetup(CapturedFlowPacket packet, FlowKey key, Socks5Server server)
    {
        var source = packet.InspectionSpan;
        var lease = _synCopyPool.Rent();
        source.CopyTo(lease.Span);
        if (!_pendingSyn.TryRetain(key, lease, source.Length, packet.Context, packet.Metadata, packet.PacketSequence, packet.FlowGeneration, _timeProvider.GetUtcNow(), out var created))
        {
            // The index refused the retain and kept ownership of nothing new: release the rental.
            // Bounded pending index (entry cap or global byte budget) is explicit backpressure,
            // the same posture as the session-capacity gate — the client retries on its next
            // retransmission once the window clears.
            lease.Dispose();
            Interlocked.Increment(ref _capacityRejectionCount);
            TcpRedirectLogging.LogTrace(_logger, "tcp.setup.pending.dropped", packet, null, "pendingBudget");
            return TcpRedirectOutcome.Blocked;
        }

        if (created is null) return TcpRedirectOutcome.SetupPending;

        // The async setup pipeline needs a stable frame across its awaits, which a native lease
        // cannot provide (a NativeLease.Span must not cross an await), so the new-flow path takes
        // one bounded managed copy here — the cold setup boundary. The retention itself is
        // alloc-free (the retransmission overwrite never copies); only the first SYN of a flow
        // pays this, and later retransmissions keep reusing the running task's launch-time copy.
        // Source the copy from the dispatch-valid capture view: TryRetain has transferred the
        // lease to the index, where a concurrent same-flow retransmission may overwrite and
        // dispose it before this line runs.
        var frame = source.ToArray();
        if (!LaunchSetup(key, created, frame, server))
        {
            Interlocked.Increment(ref _capacityRejectionCount);
            return TcpRedirectOutcome.Blocked;
        }

        return TcpRedirectOutcome.SetupPending;
    }

    /// <summary>
    /// Hands the freshly retained entry to the pooled setup executor (R8/B5). Returns false when the
    /// bounded setup ring is full: the retained entry is released and the flow fails closed, the
    /// same backpressure posture as the pending-index cap.
    /// </summary>
    private bool LaunchSetup(FlowKey key, PendingSynSetup entry, byte[] frame, Socks5Server server)
    {
        var item = _setupExecutor.RentItem(_setupHandler);
        item.Completion = entry.SetupCompletionSource;
        item.Flow = key;
        item.Server = server;
        item.Tcp.Entry = entry;
        item.Tcp.Frame = frame;
        if (!_setupExecutor.TryEnqueue(item))
        {
            entry.SetupCompletionSource.TrySetCanceled();
            _pendingSyn.Complete(key, entry, writeCooldown: false, _timeProvider.GetUtcNow());
            _logger.Warn("TCP setup executor ring is full; blocking the redirect flow.");
            return false;
        }

        _pendingSyn.AttachSetup(entry, entry.SetupCompletionSource.Task);
        return true;
    }

    /// <summary>
    /// The background half of new-flow SYN setup (R8), invoked on a pooled setup worker: runs off
    /// the pump thread under the store's inflight-setup drain, so dispose waits for it exactly like
    /// the historical inline setups. Claim exactly-once, the concurrent-loser release, and the
    /// rewrite/inject tail are the existing <see cref="TcpRedirectSetup"/> pipeline fed from the
    /// retained copy. A genuine failure fails closed for the flow and arms the per-flow setup
    /// cooldown; shutdown cancellation unwinds without one.
    /// </summary>
    private async Task SetupPendingAsync(SetupWorkItem item)
    {
        var key = item.Flow;
        var entry = item.Tcp.Entry!;
        var frame = item.Tcp.Frame!;
        var server = item.Server!;
        var writeCooldown = false;
        try
        {
            _store.EnterSetup();
        }
        catch (ObjectDisposedException)
        {
            // Disposal began between the pump-side retain and this task's start: the setup was
            // never observed, and shutdown unwinds without a cooldown.
            _pendingSyn.Complete(key, entry, writeCooldown: false, _timeProvider.GetUtcNow());
            return;
        }

        try
        {
            var packet = new CapturedFlowPacket(new PacketLease(frame), entry.Context, entry.Metadata, entry.PacketSequence, entry.FlowGeneration);
            var setup = await _setup.SetupNewRedirectAsync(packet, server, _store.ShutdownToken).ConfigureAwait(false);
            if (setup is null)
            {
                // Fail-closed null return: the pipeline already logged, released its listener
                // and alias, and wrote the grace tombstone where one applies.
                writeCooldown = !_store.ShutdownToken.IsCancellationRequested;
            }
            else if (setup.Session is null)
            {
                // A concurrent caller claimed this flow first; the redundant listener was already
                // released. Re-inject the retained copy against the existing association without
                // creating a new session.
                await ReinjectExistingFlowDataAsync(packet, setup.Association, _store.ShutdownToken).ConfigureAwait(false);
            }
            else
            {
                setup.Session.AcceptLoop = _acceptor.RunAcceptLoopAsync(setup.Session);
                TcpRedirectLogging.LogDebug(_logger, "tcp.redirect.created", setup.Session, "created");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancellation: the store's teardown released whatever the pipeline had
            // acquired; no cooldown (mirrors the UDP setup contract).
        }
        catch (Exception exception)
        {
            _logger.Warn($"TCP redirect setup failed: {exception.GetType().Name}: {exception.Message}");
            writeCooldown = !_store.ShutdownToken.IsCancellationRequested;
        }
        finally
        {
            _store.ExitSetup();
        }

        _pendingSyn.Complete(key, entry, writeCooldown, _timeProvider.GetUtcNow());
    }

    private ValueTask<TcpRedirectOutcome> ReinjectExistingFlowDataAsync(CapturedFlowPacket packet, TcpRedirectAssociation association, CancellationToken cancellationToken)
    {
        // Stage the frame into a pooled native buffer (A3): one copy out of the synchronous
        // capture view, the sequence tracker reads it pre-rewrite, the forward-leg rewrite runs
        // in place on the native span, and the injector sends the same buffer — the rewrite
        // scratch and the send buffer are a single rental, so the managed ArrayPool
        // materialization and the injector's second copy both disappear. Frames are bounded by
        // the pinned ABI (the capture path itself rejects longer frames), so the staging copy
        // always fits the native storage.
        var source = packet.InspectionSpan;
        var buffer = _framePool.Rent();
        try
        {
            source.CopyTo(buffer.GetFrameStorage());
            buffer.CompleteFrame(source.Length, NdisApiAbi.PacketFlagOnReceive, packet.Metadata.AdapterHandle);
            var frame = buffer.GetFrame();
            // Read-then-write: advance the client sequence tracker on the pre-rewrite copy,
            // keeping the reset builder's ack in the client's window.
            TcpSequenceObservation.TrackClientSequence(frame, association);
            var originalClient = packet.Context.Key.Local;
            var originalServer = association.OriginalKey.Remote;
            if (!TcpFrameRewriter.TryRewriteForwardLeg(frame, originalClient, originalServer, association, association.TranslatedListenerTuple.Port))
            {
                return FailAssociationAndBlockAsync(association);
            }
            try
            {
                _injector.Inject(buffer, towardMstcp: true, packet.Metadata.AdapterHandle, cancellationToken);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromException<TcpRedirectOutcome>(exception);
            }
            catch (OperationCanceledException exception)
            {
                return FailAssociationAndRethrowAsync(association, exception);
            }
            catch (Exception exception)
            {
                return HandleInjectionFailureAndBlockAsync(association, packet.Metadata.AdapterHandle, towardMstcp: true, exception);
            }
            TcpRedirectLogging.LogTrace(_logger, "tcp.redirect.injected", packet, association);
            return ValueTask.FromResult(TcpRedirectOutcome.Injected);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>Fails the association and reports the fail-closed outcome (cold tail).</summary>
    private async ValueTask<TcpRedirectOutcome> FailAssociationAndBlockAsync(TcpRedirectAssociation association)
    {
        await _store.FailAssociationAsync(association).ConfigureAwait(false);
        return TcpRedirectOutcome.Blocked;
    }

    /// <summary>Fails the association, then rethrows the original foreign cancellation (cold tail).</summary>
    private async ValueTask<TcpRedirectOutcome> FailAssociationAndRethrowAsync(TcpRedirectAssociation association, OperationCanceledException exception)
    {
        await _store.FailAssociationAsync(association).ConfigureAwait(false);
        ExceptionDispatchInfo.Capture(exception).Throw();
        return TcpRedirectOutcome.Blocked;
    }

    /// <summary>Surfaces the injection failure to the client, then reports the fail-closed outcome (cold tail).</summary>
    private async ValueTask<TcpRedirectOutcome> HandleInjectionFailureAndBlockAsync(TcpRedirectAssociation association, nint adapterHandle, bool towardMstcp, Exception exception)
    {
        await _clientReset.HandleInjectionFailureAsync(association, adapterHandle, towardMstcp, exception).ConfigureAwait(false);
        return TcpRedirectOutcome.Blocked;
    }

    public ValueTask<TcpRedirectOutcome> HandleReverseAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();
        ObjectDisposedException.ThrowIf(_store.IsDisposed, this);

        var key = packet.Context.Key;
        // M5 belt-and-suspenders: this coordinator owns TCP redirect table entries only. The
        // dispatcher already gates the reverse handler to TCP (H1), but a non-TCP packet must never
        // be routed into reverse handling regardless of call context.
        if (key.Protocol != TransportProtocol.Tcp) return ValueTask.FromResult(TcpRedirectOutcome.NotRelevant);
        if (!_table.TryResolveByReverse(key.Local, key.Remote, _timeProvider.GetUtcNow(), out var association) || association is null) return ValueTask.FromResult(TcpRedirectOutcome.NotRelevant);

        var original = association.OriginalKey;
        // Host-originated flows terminate on this host (reverse to MSTCP); forwarded flows (client
        // on a VM/remote side) must be sent back to the origin adapter instead.
        var towardMstcp = original.Origin == FlowOriginKind.Host;
        var targetHandle = towardMstcp ? packet.Metadata.AdapterHandle : association.OriginAdapterHandle;

        // Stage the frame into a pooled native buffer (A3): the trackers read the pre-rewrite
        // copy, the endpoint rewrite and MAC swap run in place on the native span, and the
        // injector sends the same buffer — no managed materialization, no second copy.
        var source = packet.InspectionSpan;
        var buffer = _framePool.Rent();
        try
        {
            source.CopyTo(buffer.GetFrameStorage());
            buffer.CompleteFrame(source.Length, towardMstcp ? NdisApiAbi.PacketFlagOnReceive : NdisApiAbi.PacketFlagOnSend, targetHandle);
            var frame = buffer.GetFrame();
            // Read-then-write: record the SYN-ACK sequence and advance the server sequence tracker
            // on the pre-rewrite copy before the in-place rewrite mutates the frame.
            TcpSequenceObservation.RecordServerSynAck(frame, association);
            TcpSequenceObservation.TrackServerSequence(frame, association);
            if (!PacketChecksums.TryRewriteTcpEndpoints(frame, original.Remote.Address, original.Remote.Port, original.Local.Address, original.Local.Port))
            {
                return FailAssociationAndBlockAsync(association);
            }
            // The MAC swap makes the looped-back frame look inbound from the router for a host flow.
            // A forwarded flow's reversed frame is emitted on the origin adapter toward the client, and
            // its arrival MACs (this host -> client) are already correct.
            if (towardMstcp) TcpFrameRewriter.SwapEthernetMacs(frame);
            return InjectReverseFrameAsync(packet, association, buffer, towardMstcp, targetHandle, cancellationToken);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Sends the rewritten reverse frame and maps every failure to the established reverse-path
    /// posture: caller cancellation propagates untouched, a foreign OCE fails the association, any
    /// other injection failure surfaces to the client through <see cref="ClientResetInjector"/>
    /// before failing closed. Non-async so the per-packet reverse path never boxes a state
    /// machine; the cold failure tails run in their own async helpers.
    /// </summary>
    private ValueTask<TcpRedirectOutcome> InjectReverseFrameAsync(CapturedFlowPacket packet, TcpRedirectAssociation association, NdisPacketBuffer buffer, bool towardMstcp, nint targetHandle, CancellationToken cancellationToken)
    {
        try
        {
            if (!towardMstcp && targetHandle == 0)
            {
                return FailAssociationAndBlockAsync(association);
            }
            _injector.Inject(buffer, towardMstcp, targetHandle, cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromException<TcpRedirectOutcome>(exception);
        }
        catch (OperationCanceledException exception)
        {
            return FailAssociationAndRethrowAsync(association, exception);
        }
        catch (Exception exception)
        {
            return HandleInjectionFailureAndBlockAsync(association, targetHandle, towardMstcp, exception);
        }

        TcpRedirectLogging.LogTrace(_logger, "tcp.reverse.injected", packet, association);
        return ValueTask.FromResult(TcpRedirectOutcome.Injected);
    }

    /// <summary>
    /// The warm-entry diversion predicate (X1). TCP-only, then the listener-port prefilter: a
    /// reverse candidate's source port is always a live listener port, so a miss cannot match the
    /// reverse index and the packet falls through to the slow path only when the flow table also
    /// misses it — where the full handler still runs, so tombstone stragglers of a torn-down
    /// redirect keep their grace-drop behavior (the fall-through theorem, design D3). Diversion
    /// knowledge lives here so the dispatcher stays free of reverse-handler internals.
    /// </summary>
    public bool WantsPacket(in CapturedFlowPacket packet)
    {
        var key = packet.Context.Key;
        return key.Protocol == TransportProtocol.Tcp && _table.IsReverseCandidatePort(key.Local.Port);
    }

    /// <summary>
    /// Handles a packet that belongs to an active redirect leg (source or destination port is a
    /// proxy listener port) by reversing it back to the original server:client tuple. Returns
    /// <see cref="TcpRedirectOutcome.NotRelevant"/> when the packet has no proxy-port relationship,
    /// so the caller can continue normal flow/policy processing. Runs before flow lookup and policy
    /// so a reverse packet is never re-evaluated as a new client flow.
    /// </summary>
    public async ValueTask<TcpRedirectOutcome> HandleReverseIfApplicableAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();
        ObjectDisposedException.ThrowIf(_store.IsDisposed, this);

        // H1/M5 gate before the numeric-port lookup: this handler owns TCP reverse routing. A UDP
        // or other-protocol frame whose local/remote port numerically matches an active TCP
        // listener port must be left to normal flow/policy handling, never dropped here.
        var key = packet.Context.Key;
        if (key.Protocol != TransportProtocol.Tcp) return TcpRedirectOutcome.NotRelevant;
        if (!_table.IsReverseCandidate(key.Local, key.Remote))
        {
            // TIME_WAIT grace: the reverse leg of a redirect torn down within the grace window still
            // resolves here, so listener-side stragglers of the finished handshake are consumed
            // instead of falling through to flow/policy handling as a fresh flow.
            return _store.Tombstones.TryHit(key.Local, key.Remote, _timeProvider.GetUtcNow())
                ? TcpRedirectOutcome.Dropped
                : TcpRedirectOutcome.NotRelevant;
        }

        return await HandleReverseAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Routes a proxy-selected TCP packet to the correct redirect phase. The flow dispatcher sends
    /// every packet on a proxy-decided TCP flow here. A SYN starts or re-injects the redirect; a
    /// packet whose source is a known translated listener tuple is a reverse packet; anything else
    /// is mid-flow data on the redirect leg and is passed through.
    /// </summary>
    public ValueTask<TcpRedirectOutcome> HandlePacketAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();
        ArgumentNullException.ThrowIfNull(server);
        ObjectDisposedException.ThrowIf(_store.IsDisposed, this);

        var key = packet.Context.Key;
        var now = _timeProvider.GetUtcNow();
        // A2: the SYN bit-test reads the synchronous frame view (the native capture buffer while
        // the lease is unmaterialized) — a pure span read that never forces a pooled managed copy.
        var syn = TcpFrameRewriter.IsTcpSyn(packet.InspectionSpan);

        if (_table.IsReverseCandidate(key.Local, key.Remote))
        {
            return HandleReverseAsync(packet, cancellationToken);
        }

        if (syn)
        {
            // Data-bearing SYNs (TCP Fast Open, RFC 7413) ride the same pipeline as bare SYNs:
            // the forward-leg rewrite only touches addresses/ports, and the local non-TFO
            // listener stack either queues or drops the SYN data — the client retransmits it
            // as a normal segment (graceful degradation), so the flow connects either way.
            return HandleSynAsync(packet, server, cancellationToken);
        }

        // Mid-flow data on the original client->listener leg: rewrite the destination to the proxy
        // listener tuple so the redirected connection receives the client's payload. Only flows with
        // an active redirect association are rewritten; anything else is not ours to handle.
        if (_table.TryResolveByOriginal(key, now, out var existing) && existing is not null)
        {
            return ReinjectExistingFlowDataAsync(packet, existing, cancellationToken);
        }

        // TIME_WAIT grace: the redirect for this flow was torn down within the grace window, so
        // this is a straggler of the finished handshake (retransmitted FIN/ACK, final ACK). Drop it
        // instead of returning NotRelevant, whose executor fallback would reinject toward the real
        // server — which never saw the proxied connection and answers the unknown tuple with a
        // bounced RST.
        if (_store.Tombstones.TryHit(key, now)) return ValueTask.FromResult(TcpRedirectOutcome.Dropped);

        return ValueTask.FromResult(TcpRedirectOutcome.NotRelevant);
    }

    /// <summary>
    /// Handles an IP-fragment frame (IPv4 fragment bits or an IPv6 fragment header) whose IP
    /// address pair matches an active redirect association in either orientation (S1). Such a
    /// frame can never be rewritten or relayed, and passing it toward the real server would
    /// cross-talk an unknown tuple onto a proxied connection, so the association is torn down
    /// client-visibly and the fragment is consumed. Frames that match no association are not
    /// ours to attribute and keep the unconditional non-flow pass.
    /// </summary>
    public async ValueTask<TcpRedirectOutcome> HandleFragmentAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();
        ObjectDisposedException.ThrowIf(_store.IsDisposed, this);

        if (!IPFragment.TryReadAddressPair(packet.InspectionSpan, out var source, out var destination)) return TcpRedirectOutcome.NotRelevant;
        if (!_table.TryResolveByAddressPair(source, destination, _timeProvider.GetUtcNow(), out var association) || association is null) return TcpRedirectOutcome.NotRelevant;

        TcpRedirectLogging.LogTrace(_logger, "tcp.redirect.fragment", packet, association, "fragment");
        await _clientReset.HandleFragmentTeardownAsync(association).ConfigureAwait(false);
        return TcpRedirectOutcome.Dropped;
    }

    /// <summary>
    /// Removes half-open redirect associations still in <see cref="RelayPhase.Redirecting"/> that
    /// have observed no activity for <paramref name="idleTimeout"/> and tears down their sessions
    /// (listener, relay, self-traffic token, table alias). A session is only removed while it is
    /// still <see cref="RelayPhase.Redirecting"/> — half-open and never relayed — so a genuinely
    /// abandoned flow is released instead of occupying the bounded redirect table (design §7/§8).
    /// An established flow whose relay is <see cref="RelayPhase.Relaying"/> is NOT expired by this
    /// wall-clock sweep (M4): a live connection silent at the packet level (e.g. SSH without
    /// keepalive) must not be force-torn-down. Teardown of a relaying session is instead tied to
    /// the relay completing/ending (see <see cref="TcpRedirectAcceptor"/>), and a truly
    /// stalled relay is reclaimed by the read/write timeouts in <see cref="TcpProxyRelay"/>.
    /// </summary>
    public ValueTask<int> RemoveExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout)
        => _store.RemoveExpiredAsync(now, idleTimeout, () => _pendingSyn.RemoveExpired(now));

    /// <summary>
    /// Whether this coordinator still holds state for the flow: an active session exists (a
    /// half-open redirect or a relaying connection) or the flow is inside its post-teardown grace
    /// tombstone. The idle sweeper passes this as the flow-expiry hold predicate so a silently
    /// relaying connection's flow-table decision is not expired out from under a live connection —
    /// the flow-table counterpart of the M4 relaying exemption in <see cref="RemoveExpiredAsync"/>.
    /// Generation is deliberately not compared: a new flow reusing the tuple claims a new table
    /// generation, so the hold naturally lapses.
    /// </summary>
    internal bool HoldsFlow(FlowKey key)
        => _store.Holds(key) || _store.Tombstones.TryHit(key, _timeProvider.GetUtcNow());

    /// <summary>The TIME_WAIT-grace tombstone index; internal for tests to advance the grace window.</summary>
    internal TcpRedirectTombstoneTable Tombstones => _store.Tombstones;

    /// <summary>
    /// Awaits every pending background setup launched so far (internal test/diagnostic seam):
    /// the pump-side SYN handler returns <see cref="TcpRedirectOutcome.SetupPending"/> long
    /// before the listener exists, so callers that need the settled state (listener created,
    /// SYN injected, failure logged) await this after dispatching the SYN.
    /// </summary>
    internal Task DrainPendingSetupsAsync() => _pendingSyn.DrainAsync();

    /// <summary>The capacity-reset cooldown index; internal for tests to advance the window.</summary>
    internal TcpResetCooldownTable CapacityResetCooldowns => _clientReset.CapacityResets;

    public ValueTask DisposeAsync()
        => DisposeAsyncCore();

    private async ValueTask DisposeAsyncCore()
    {
        // The store's dispose drains every started background setup (R8 moved EnterSetup into
        // the task, so the inflight counter covers them); the pending drain afterwards closes the
        // window between a task's final ExitSetup and its entry removal, crediting every
        // retained copy exactly once and dropping cooldowns so a disposed coordinator leaves no
        // per-flow state behind.
        await _store.DisposeAsync().ConfigureAwait(false);
        _pendingSyn.RemoveAll();
        if (_ownsSynCopyPool) _synCopyPool.Dispose();
        if (_ownsSetupExecutor) _setupExecutor.Dispose();
    }
}
