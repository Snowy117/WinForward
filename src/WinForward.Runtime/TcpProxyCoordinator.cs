using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Windows;

namespace WinForward.Runtime;

/// <summary>
/// Coordinates the transparent TCP redirect data path described in design §8 behind abstraction seams,
/// mirroring <see cref="UdpProxyCoordinator"/>. For each proxy-selected TCP flow it: allocates a local
/// listener, claims the flow exactly once in the redirect table, rewrites the SYN destination toward
/// the listener, registers the listener tuple in the loop-prevention registry, injects the rewritten
/// frame, and runs a background accept-and-relay loop. A proxy-selected flow is never silently passed:
/// every listener-allocation, claim, rewrite, injection, and relay-setup failure fails closed and
/// releases the listener, the table alias, and the self-traffic token. The data path is delegated to
/// focused modules: <see cref="TcpRedirectSetup"/> (new-flow pipeline), <see cref="TcpRedirectSessionStore"/>
/// (session lifecycle under one gate), <see cref="TcpRedirectAcceptor"/> (accept/relay loop), and
/// <see cref="ClientResetInjector"/> (client-visible failure surface); this class owns entry routing.
/// </summary>
public sealed class TcpProxyCoordinator : IAsyncDisposable
{
    private readonly TcpRedirectTable _table;
    private readonly ITcpRedirectInjector _injector;
    private readonly IRuntimeLogger _logger;
    private readonly int _capacity;
    private readonly TcpRedirectSessionStore _store;
    private readonly TcpRedirectSetup _setup;
    private readonly ClientResetInjector _clientReset;
    private readonly TcpRedirectAcceptor _acceptor;
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
        _clientReset = new ClientResetInjector(injector, _logger, _store.TearDownSessionAsync, _store.FailAssociationAsync);
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
        if (packet.Lease is null) throw new ArgumentNullException(nameof(packet));
        ArgumentNullException.ThrowIfNull(server);
        _store.EnterSetup();
        try
        {
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

            if (_store.SessionCount >= _capacity)
            {
                Interlocked.Increment(ref _capacityRejectionCount);
                TcpRedirectLogging.LogTrace(_logger, "tcp.redirect.rejected", packet, null, "capacity");
                return TcpRedirectOutcome.Blocked;
            }

            var setup = await _setup.SetupNewRedirectAsync(packet, server, cancellationToken).ConfigureAwait(false);
            if (setup is null) return TcpRedirectOutcome.Blocked;

            // A concurrent caller claimed this flow first; the redundant listener was already released.
            // Re-inject the SYN against the existing association without creating a new session.
            if (setup.Session is null)
            {
                return await ReinjectExistingFlowDataAsync(packet, setup.Association, cancellationToken).ConfigureAwait(false);
            }

            setup.Session.AcceptLoop = _acceptor.RunAcceptLoopAsync(setup.Session);
            TcpRedirectLogging.LogDebug(_logger, "tcp.redirect.created", setup.Session, "created");

            return TcpRedirectOutcome.Injected;
        }
        finally
        {
            _store.ExitSetup();
        }
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
        if (packet.Lease is null) throw new ArgumentNullException(nameof(packet));
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
    /// Handles a packet that belongs to an active redirect leg (source or destination port is a
    /// proxy listener port) by reversing it back to the original server:client tuple. Returns
    /// <see cref="TcpRedirectOutcome.NotRelevant"/> when the packet has no proxy-port relationship,
    /// so the caller can continue normal flow/policy processing. Runs before flow lookup and policy
    /// so a reverse packet is never re-evaluated as a new client flow.
    /// </summary>
    public async ValueTask<TcpRedirectOutcome> HandleReverseIfApplicableAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (packet.Lease is null) throw new ArgumentNullException(nameof(packet));
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
        if (packet.Lease is null) throw new ArgumentNullException(nameof(packet));
        ArgumentNullException.ThrowIfNull(server);
        ObjectDisposedException.ThrowIf(_store.IsDisposed, this);

        var key = packet.Context.Key;
        var now = DateTimeOffset.UtcNow;
        var syn = TcpFrameRewriter.ClassifyTcpSyn(packet.Lease.Frame.Span);

        if (_table.IsReverseCandidate(key.Local, key.Remote))
        {
            return await HandleReverseAsync(packet, cancellationToken).ConfigureAwait(false);
        }

        if (syn == TcpSynKind.WithPayload) return TcpRedirectOutcome.Blocked;

        if (syn == TcpSynKind.Empty)
        {
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
        => _store.RemoveExpiredAsync(now, idleTimeout);

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

    public ValueTask DisposeAsync()
        => _store.DisposeAsync();
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
