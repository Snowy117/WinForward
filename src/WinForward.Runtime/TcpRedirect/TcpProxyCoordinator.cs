using WinForward.Configuration;
using WinForward.Core;
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
    private readonly IRuntimeLogger _logger;
    private readonly int _capacity;
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
        IRuntimeLogger? logger = null,
        int? capacity = null)
    {
        ArgumentNullException.ThrowIfNull(listenerFactory);
        ArgumentNullException.ThrowIfNull(relayFactory);
        ArgumentNullException.ThrowIfNull(injector);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentNullException.ThrowIfNull(localAddresses);
        if (capacity is < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        _table = table;
        _injector = injector;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _capacity = capacity ?? 16_384;
        _store = new TcpRedirectSessionStore(table, _logger, _capacity);
        _clientReset = new ClientResetInjector(injector, _logger, _store.TearDownSessionAsync, _store.FailAssociationAsync, _capacity);
        _acceptor = new TcpRedirectAcceptor(relayFactory, _logger, _clientReset, _store.TryAttachRelay, _store.TearDownSessionAsync);
        _setup = new TcpRedirectSetup(listenerFactory, table, selfTraffic, localAddresses, injector, _logger, _store, _clientReset);
    }

    public TcpRedirectTable Table => _table;

    /// <summary>
    /// The number of concurrent SYN callers that arrived after another caller had already claimed
    /// the flow, detected a translated-tuple mismatch, released their redundant listener, and
    /// fallen back to re-inject. A non-zero value after a concurrent burst proves the redirect-table
    /// exactly-once path was exercised under genuine concurrency.
    /// </summary>
    internal long ConcurrentLoserCount => _setup.ConcurrentLoserCount;

    /// <summary>
    /// The total number of SYN arrivals rejected by the capacity gate since construction. Unlike
    /// error-type failures this counts explicit budget management: the flow was blocked because the
    /// concurrent proxied-flow budget was exhausted, not because setup failed.
    /// </summary>
    internal long CapacityRejectionCount => Interlocked.Read(ref _capacityRejectionCount);

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
        if (_table.TryResolveByOriginal(key, DateTimeOffset.UtcNow, out var existing) && existing is not null)
        {
            TcpRedirectLogging.LogTrace(_logger, "tcp.redirect.reused", packet, existing);
            return await ReinjectExistingFlowDataAsync(packet, existing, cancellationToken).ConfigureAwait(false);
        }

        // TIME_WAIT grace: a same-tuple SYN whose redirect was torn down within the grace
        // window is a retransmission of the finished flow's handshake, never a fresh
        // connection — re-arming setup would honor the dead flow (or leak the straggler
        // toward the real server via the NotRelevant fallback), so it is consumed like every
        // other straggler. The next connection claims a new source port and a new key.
        if (_store.Tombstones.TryHit(key, DateTimeOffset.UtcNow)) return TcpRedirectOutcome.Dropped;

        // Setup-failure cooldown (R8): a redirect setup for this flow genuinely failed within
        // the last second — the failure path already logged and released its resources, and a
        // retransmitted SYN inside the window is consumed so a dead setup path is not re-armed
        // at the client's retransmission rate.
        if (_pendingSyn.IsInSetupCooldown(key, DateTimeOffset.UtcNow))
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
        var frameCopy = packet.InspectionSpan.ToArray();
        if (!_pendingSyn.TryRetain(key, frameCopy, packet.Context, packet.Metadata, packet.PacketSequence, packet.FlowGeneration, DateTimeOffset.UtcNow, out var created))
        {
            // Bounded pending index (entry cap or global byte budget): explicit backpressure,
            // the same posture as the session-capacity gate — the client retries on its next
            // retransmission once the window clears.
            Interlocked.Increment(ref _capacityRejectionCount);
            TcpRedirectLogging.LogTrace(_logger, "tcp.setup.pending.dropped", packet, null, "pendingBudget");
            return TcpRedirectOutcome.Blocked;
        }

        if (created is null) return TcpRedirectOutcome.SetupPending;

        // The launch captures the entry's current copy by value: retransmissions only ever
        // REPLACE the entry's array (never mutate it in place), so the task's reference stays a
        // valid frame regardless of later overwrites, and retransmitted SYNs share the client
        // ISN — the injected rewrite is equivalent either way.
        var frame = created.RetainedFrame;
        var setupTask = Task.Run(() => SetupPendingAsync(key, created, frame, server));
        _pendingSyn.AttachSetup(created, setupTask);
        return TcpRedirectOutcome.SetupPending;
    }

    /// <summary>
    /// The background half of new-flow SYN setup (R8): runs off the pump thread under the store's
    /// inflight-setup drain, so dispose waits for it exactly like the historical inline setups.
    /// Claim exactly-once, the concurrent-loser release, and the rewrite/inject tail are the
    /// existing <see cref="TcpRedirectSetup"/> pipeline fed from the retained copy. A genuine
    /// failure fails closed for the flow and arms the per-flow setup cooldown; shutdown
    /// cancellation unwinds without one.
    /// </summary>
    private async Task SetupPendingAsync(FlowKey key, PendingSynSetup entry, byte[] frame, Socks5Server server)
    {
        var writeCooldown = false;
        try
        {
            _store.EnterSetup();
        }
        catch (ObjectDisposedException)
        {
            // Disposal began between the pump-side retain and this task's start: the setup was
            // never observed, and shutdown unwinds without a cooldown.
            _pendingSyn.Complete(key, entry, writeCooldown: false, DateTimeOffset.UtcNow);
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

        _pendingSyn.Complete(key, entry, writeCooldown, DateTimeOffset.UtcNow);
    }

    private async ValueTask<TcpRedirectOutcome> ReinjectExistingFlowDataAsync(CapturedFlowPacket packet, TcpRedirectAssociation association, CancellationToken cancellationToken)
    {
        var frame = packet.Lease.Frame;
        if (!TcpFrameRewriter.TryGetWritableFrame(frame, out var writableFrame))
        {
            await _store.FailAssociationAsync(association).ConfigureAwait(false);
            return TcpRedirectOutcome.Blocked;
        }
        // Read-then-write: advance the client sequence tracker on the original frame before the
        // in-place rewrite mutates it, keeping the reset builder's ack in the client's window.
        TcpSequenceObservation.TrackClientSequence(frame.Span, association);
        var originalClient = packet.Context.Key.Local;
        var originalServer = association.OriginalKey.Remote;
        if (!TcpFrameRewriter.TryRewriteForwardLeg(writableFrame, originalClient, originalServer, association, association.TranslatedListenerTuple.Port))
        {
            await _store.FailAssociationAsync(association).ConfigureAwait(false);
            return TcpRedirectOutcome.Blocked;
        }
        try
        {
            await _injector.InjectAsync(frame, towardMstcp: true, packet.Metadata.AdapterHandle, cancellationToken).ConfigureAwait(false);
            TcpRedirectLogging.LogTrace(_logger, "tcp.redirect.injected", packet, association);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await _store.FailAssociationAsync(association).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await _clientReset.HandleInjectionFailureAsync(association, packet.Metadata.AdapterHandle, towardMstcp: true, exception).ConfigureAwait(false);
            return TcpRedirectOutcome.Blocked;
        }
        return TcpRedirectOutcome.Injected;
    }

    public async ValueTask<TcpRedirectOutcome> HandleReverseAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();
        ObjectDisposedException.ThrowIf(_store.IsDisposed, this);

        var key = packet.Context.Key;
        // M5 belt-and-suspenders: this coordinator owns TCP redirect table entries only. The
        // dispatcher already gates the reverse handler to TCP (H1), but a non-TCP packet must never
        // be routed into reverse handling regardless of call context.
        if (key.Protocol != TransportProtocol.Tcp) return TcpRedirectOutcome.NotRelevant;
        if (!_table.TryResolveByReverse(key.Local, key.Remote, DateTimeOffset.UtcNow, out var association) || association is null) return TcpRedirectOutcome.NotRelevant;

        var original = association.OriginalKey;
        var frame = packet.Lease.Frame;
        // Read-then-write: record the SYN-ACK sequence and advance the server sequence tracker
        // before the in-place rewrite mutates the frame.
        TcpSequenceObservation.RecordServerSynAck(frame.Span, association);
        TcpSequenceObservation.TrackServerSequence(frame.Span, association);

        // Host-originated flows terminate on this host (reverse to MSTCP); forwarded flows (client
        // on a VM/remote side) must be sent back to the origin adapter instead.
        var towardMstcp = original.Origin == FlowOriginKind.Host;
        var targetHandle = towardMstcp ? packet.Metadata.AdapterHandle : association.OriginAdapterHandle;
        if (!TcpFrameRewriter.TryGetWritableFrame(frame, out var writableFrame)
            || !PacketChecksums.TryRewriteTcpEndpoints(writableFrame, original.Remote.Address, original.Remote.Port, original.Local.Address, original.Local.Port))
        {
            await _store.FailAssociationAsync(association).ConfigureAwait(false);
            return TcpRedirectOutcome.Blocked;
        }
        // The MAC swap makes the looped-back frame look inbound from the router for a host flow.
        // A forwarded flow's reversed frame is emitted on the origin adapter toward the client, and
        // its arrival MACs (this host -> client) are already correct.
        if (towardMstcp) TcpFrameRewriter.SwapEthernetMacs(writableFrame);
        try
        {
            if (!towardMstcp && targetHandle == 0)
            {
                await _store.FailAssociationAsync(association).ConfigureAwait(false);
                return TcpRedirectOutcome.Blocked;
            }
            await _injector.InjectAsync(frame, towardMstcp, targetHandle, cancellationToken).ConfigureAwait(false);
            TcpRedirectLogging.LogTrace(_logger, "tcp.reverse.injected", packet, association);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await _store.FailAssociationAsync(association).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await _clientReset.HandleInjectionFailureAsync(association, targetHandle, towardMstcp, exception).ConfigureAwait(false);
            return TcpRedirectOutcome.Blocked;
        }

        return TcpRedirectOutcome.Injected;
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
            return _store.Tombstones.TryHit(key.Local, key.Remote, DateTimeOffset.UtcNow)
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
    public async ValueTask<TcpRedirectOutcome> HandlePacketAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();
        ArgumentNullException.ThrowIfNull(server);
        ObjectDisposedException.ThrowIf(_store.IsDisposed, this);

        var key = packet.Context.Key;
        var now = DateTimeOffset.UtcNow;
        var syn = TcpFrameRewriter.IsTcpSyn(packet.Lease.Frame.Span);

        if (_table.IsReverseCandidate(key.Local, key.Remote))
        {
            return await HandleReverseAsync(packet, cancellationToken).ConfigureAwait(false);
        }

        if (syn)
        {
            // Data-bearing SYNs (TCP Fast Open, RFC 7413) ride the same pipeline as bare SYNs:
            // the forward-leg rewrite only touches addresses/ports, and the local non-TFO
            // listener stack either queues or drops the SYN data — the client retransmits it
            // as a normal segment (graceful degradation), so the flow connects either way.
            return await HandleSynAsync(packet, server, cancellationToken).ConfigureAwait(false);
        }

        // Mid-flow data on the original client->listener leg: rewrite the destination to the proxy
        // listener tuple so the redirected connection receives the client's payload. Only flows with
        // an active redirect association are rewritten; anything else is not ours to handle.
        if (_table.TryResolveByOriginal(key, now, out var existing) && existing is not null)
        {
            return await ReinjectExistingFlowDataAsync(packet, existing, cancellationToken).ConfigureAwait(false);
        }

        // TIME_WAIT grace: the redirect for this flow was torn down within the grace window, so
        // this is a straggler of the finished handshake (retransmitted FIN/ACK, final ACK). Drop it
        // instead of returning NotRelevant, whose executor fallback would reinject toward the real
        // server — which never saw the proxied connection and answers the unknown tuple with a
        // bounced RST.
        if (_store.Tombstones.TryHit(key, now)) return TcpRedirectOutcome.Dropped;

        return TcpRedirectOutcome.NotRelevant;
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
        if (!_table.TryResolveByAddressPair(source, destination, DateTimeOffset.UtcNow, out var association) || association is null) return TcpRedirectOutcome.NotRelevant;

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
        => _store.Holds(key) || _store.Tombstones.TryHit(key, DateTimeOffset.UtcNow);

    /// <summary>The TIME_WAIT-grace tombstone index; internal for tests to advance the grace window.</summary>
    internal TcpRedirectTombstoneTable Tombstones => _store.Tombstones;

    /// <summary>The pending new-flow SYN setups; internal for tests to observe R8 bounds.</summary>
    internal TcpPendingSynSetupIndex PendingSetups => _pendingSyn;

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
    }
}

/// <summary>
/// The per-flow redirect state: the association (redirect table entry), the local listener, the
/// self-traffic token, the SOCKS5 server, a linked lifetime cancellation token, and the optional
/// relay/accept-loop tasks. A top-level internal type (not nested on <see cref="TcpProxyCoordinator"/>)
/// so the accept/reset/relay modules can reference it without a circular dependency on the
/// coordinator itself; it lives in this file to keep the module's file count lean.
/// </summary>
internal sealed class TcpRedirectSession(TcpRedirectAssociation association, ITcpRedirectListener listener, SelfTrafficRegistry.SelfTrafficToken selfTrafficToken, Socks5Server server, CancellationToken shutdown, long flowGeneration = 0)
{
    public TcpRedirectAssociation Association { get; } = association;
    public ITcpRedirectListener Listener { get; } = listener;
    public SelfTrafficRegistry.SelfTrafficToken SelfTrafficToken { get; } = selfTrafficToken;
    public Socks5Server Server { get; } = server;
    public long FlowGeneration { get; } = flowGeneration;
    private CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    private int _retired;
    public ITcpRelay? Relay { get; set; }
    public Task? AcceptLoop { get; set; }
    public CancellationToken Token => Lifetime.Token;
    public bool IsRetired => Volatile.Read(ref _retired) != 0;

    public void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) == 0) Lifetime.Cancel();
    }

    public void DisposeLifetime() => Lifetime.Dispose();
}
