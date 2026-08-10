using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime;

/// <summary>
/// Coordinates the transparent TCP redirect data path described in design §8 behind abstraction seams,
/// mirroring <see cref="UdpProxyCoordinator"/>. For each proxy-selected TCP flow it: allocates a local
/// listener, claims the flow exactly once in the redirect table, rewrites the SYN destination toward
/// the listener, registers the listener tuple in the loop-prevention registry, injects the rewritten
/// frame, and runs a background accept-and-relay loop. A proxy-selected flow is never silently passed:
/// every listener-allocation, claim, rewrite, injection, and relay-setup failure fails closed and
/// releases the listener, the table alias, and the self-traffic token.
/// </summary>
public sealed class TcpProxyCoordinator : IAsyncDisposable
{
    private readonly ITcpRedirectListenerFactory _listenerFactory;
    private readonly ITcpProxyRelayFactory _relayFactory;
    private readonly ITcpRedirectInjector _injector;
    private readonly TcpRedirectTable _table;
    private readonly SelfTrafficRegistry _selfTraffic;
    private readonly IRuntimeLogger _logger;
    private readonly int _capacity;
    private readonly Dictionary<FlowKey, TcpRedirectSession> _sessions = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private long _concurrentLoserCount;
    private bool _disposed;

    public TcpProxyCoordinator(
        ITcpRedirectListenerFactory listenerFactory,
        ITcpProxyRelayFactory relayFactory,
        ITcpRedirectInjector injector,
        TcpRedirectTable table,
        SelfTrafficRegistry selfTraffic,
        IRuntimeLogger? logger = null,
        int capacity = 16_384)
    {
        ArgumentNullException.ThrowIfNull(listenerFactory);
        ArgumentNullException.ThrowIfNull(relayFactory);
        ArgumentNullException.ThrowIfNull(injector);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _listenerFactory = listenerFactory;
        _relayFactory = relayFactory;
        _injector = injector;
        _table = table;
        _selfTraffic = selfTraffic;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _capacity = capacity;
    }

    public TcpRedirectTable Table => _table;

    /// <summary>
    /// The number of concurrent SYN callers that arrived after another caller had already claimed
    /// the flow, detected a translated-tuple mismatch, released their redundant listener, and
    /// fallen back to re-inject. A non-zero value after a concurrent burst proves the redirect-table
    /// exactly-once path was exercised under genuine concurrency.
    /// </summary>
    internal long ConcurrentLoserCount => Interlocked.Read(ref _concurrentLoserCount);

    public async ValueTask<TcpRedirectOutcome> HandleSynAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(server);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = packet.Context.Key;
        if (key.Protocol != TransportProtocol.Tcp)
        {
            throw new ArgumentException("TCP coordinator accepts only TCP flow keys.", nameof(packet));
        }

        // A flow already claimed by a prior SYN reuses its decision: touch the association and
        // re-inject the rewritten SYN toward the listener. Policy is evaluated exactly once.
        if (_table.TryResolveByOriginal(key, DateTimeOffset.UtcNow, out var existing) && existing is not null)
        {
            return await ReinjectExistingSynAsync(packet, existing, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_sessions.Count >= _capacity) return TcpRedirectOutcome.Blocked;
        }

        var setup = await SetupNewRedirectAsync(packet, cancellationToken).ConfigureAwait(false);
        if (setup is null) return TcpRedirectOutcome.Blocked;

        // A concurrent caller claimed this flow first; the redundant listener was already released.
        // Re-inject the SYN against the existing association without creating a new session.
        if (setup.Listener is null)
        {
            return await ReinjectExistingSynAsync(packet, setup.Association, cancellationToken).ConfigureAwait(false);
        }

        var session = new TcpRedirectSession(setup.Association, setup.Listener, setup.SelfTrafficToken!, server);
        lock (_gate) _sessions.Add(setup.Association.OriginalKey, session);
        session.AcceptLoop = RunAcceptLoopAsync(session, cancellationToken);

        return TcpRedirectOutcome.Injected;
    }

    /// <summary>
    /// Allocates the listener, claims the association, rewrites the SYN, registers loop prevention,
    /// and injects the rewritten frame. Returns null (fail-closed) when any step fails, after
    /// releasing the listener, the table alias, and the self-traffic token it may have acquired.
    /// </summary>
    private async ValueTask<RedirectSetup?> SetupNewRedirectAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        var key = packet.Context.Key;

        ITcpRedirectListener listener;
        try
        {
            listener = await _listenerFactory.CreateAsync(key.AddressFamily, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _logger.Warn("TCP redirect failed: listener allocation failed, blocking the flow.");
            return null;
        }

        var translatedTuple = listener.TranslatedTuple;
        var originAdapter = new AdapterContext(key.OriginAdapterId, packet.Context.AdapterName, key.OriginAdapterGeneration);
        var originalDestination = key.Remote;

        if (!_table.TryClaim(key, originalDestination, originAdapter, translatedTuple, DateTimeOffset.UtcNow, out var association) || association is null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            _logger.Warn("TCP redirect failed: redirect-table capacity reached or translated-tuple collision, blocking the flow.");
            return null;
        }

        // A concurrent caller may have claimed this same original flow first. TryClaim returns the
        // existing association (whose translated tuple differs from this listener's) rather than a
        // new one. The just-allocated listener is redundant: release it and fall back to the
        // re-inject path against the existing association so only one listener owns the flow.
        if (association.TranslatedListenerTuple != translatedTuple)
        {
            Interlocked.Increment(ref _concurrentLoserCount);
            await listener.DisposeAsync().ConfigureAwait(false);
            return new RedirectSetup(Listener: null, association, SelfTrafficToken: null);
        }

        return await CompleteNewRedirectAsync(packet, listener, association, translatedTuple, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RedirectSetup?> CompleteNewRedirectAsync(CapturedFlowPacket packet, ITcpRedirectListener listener, TcpRedirectAssociation association, Endpoint translatedTuple, CancellationToken cancellationToken)
    {
        var key = packet.Context.Key;
        var rewrittenFrame = packet.Lease.Frame.ToArray();
        var originalClient = key.Local;
        var originalServer = key.Remote;
        // Official WinpkFilter local_redirect pattern: swap MACs and IPs, rewrite the destination
        // port to the proxy port. The SOURCE PORT is kept as the client's original port (the
        // redirector only rewrites th_dport, never th_sport), so the local proxy server's accepted
        // connection has peer = server:client_orig_port, which the per-flow mapping resolves by
        // client source port.
        if (!PacketChecksums.TryRewriteTcpEndpoints(rewrittenFrame, originalServer.Address, originalClient.Port, originalClient.Address, translatedTuple.Port))
        {
            await ReleaseAssociationAsync(listener, association, null).ConfigureAwait(false);
            _logger.Warn("TCP redirect failed: SYN endpoint rewrite failed, blocking the flow.");
            return null;
        }
        SwapEthernetMacs(rewrittenFrame);

        // L4 clarity: this exact self-traffic key cannot be matched by the wildcard registry because
        // the observable reverse leg is (client:orig_port) -> (client_ip:proxy_port), and the
        // per-client source port is unknown until a connection arrives. The listener/reverse leg is
        // therefore guarded by the TCP-only reverse hook (see HandleReverseIfApplicableAsync, gated
        // to TCP by H1 in the dispatcher), which reverses before flow lookup/policy. This
        // registration is retained as writer-intent belt-and-suspenders and because the registry is
        // the natural home for an exact local-loopback listener tuple when one becomes expressible.
        var selfTrafficToken = _selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Tcp, translatedTuple, translatedTuple));

        try
        {
            await _injector.InjectAsync(rewrittenFrame, towardMstcp: true, packet.Metadata.AdapterHandle, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ReleaseAssociationAsync(listener, association, selfTrafficToken).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await ReleaseAssociationAsync(listener, association, selfTrafficToken).ConfigureAwait(false);
            _logger.Warn("TCP redirect failed: rewritten-frame injection failed, blocking the flow.");
            return null;
        }

        return new RedirectSetup(listener, association, selfTrafficToken);
    }

    private async ValueTask<TcpRedirectOutcome> ReinjectExistingSynAsync(CapturedFlowPacket packet, TcpRedirectAssociation association, CancellationToken cancellationToken)
        => await ReinjectExistingFlowDataAsync(packet, association, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Swaps the Ethernet source and destination MAC addresses of a frame. The official WinpkFilter
    /// local-redirect pattern swaps MACs alongside IPs and ports so the redirected frame is accepted
    /// by the local stack as if it arrived from the original server.
    /// </summary>
    private static void SwapEthernetMacs(Span<byte> frame)
    {
        if (frame.Length < 12) return;
        Span<byte> destination = frame.Slice(0, 6);
        Span<byte> source = frame.Slice(6, 6);
        var temp = new byte[6];
        destination.CopyTo(temp);
        source.CopyTo(destination);
        temp.CopyTo(source);
    }

    private async ValueTask<TcpRedirectOutcome> ReinjectExistingFlowDataAsync(CapturedFlowPacket packet, TcpRedirectAssociation association, CancellationToken cancellationToken)
    {
        var rewrittenFrame = packet.Lease.Frame.ToArray();
        var originalClient = packet.Context.Key.Local;
        var originalServer = association.OriginalKey.Remote;
        if (!PacketChecksums.TryRewriteTcpEndpoints(rewrittenFrame, originalServer.Address, originalClient.Port, originalClient.Address, association.TranslatedListenerTuple.Port))
        {
            return TcpRedirectOutcome.Blocked;
        }
        SwapEthernetMacs(rewrittenFrame);
        try
        {
            await _injector.InjectAsync(rewrittenFrame, towardMstcp: true, packet.Metadata.AdapterHandle, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return TcpRedirectOutcome.Blocked;
        }
        return TcpRedirectOutcome.Injected;
    }

    public async ValueTask<TcpRedirectOutcome> HandleReverseAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A reverse packet travels local-proxy-listener -> client. The proxy binds 0.0.0.0 and
        // connects to the client using the client's local IP, so the packet's source is
        // client_ip:proxy_port — the proxy port discriminates, not the full tuple.
        var key = packet.Context.Key;
        // M5 belt-and-suspenders: this coordinator owns TCP redirect table entries only. The
        // dispatcher already gates the reverse handler to TCP (H1), but a non-TCP packet must never
        // be routed into reverse handling regardless of call context.
        if (key.Protocol != TransportProtocol.Tcp) return TcpRedirectOutcome.NotRelevant;
        var now = DateTimeOffset.UtcNow;
        var found = _table.TryResolveByProxyPort(key.Local.Port, now, out var association);
        if (!found) found = _table.TryResolveByProxyPort(key.Remote.Port, now, out association);
        if (!found || association is null) return TcpRedirectOutcome.Blocked;
        // M5: the numeric proxy port is unique per flow, but an address family may share the same
        // port value. An IPv6 reverse frame must not match an IPv4 flow's listener port index (or
        // vice-versa), or a rewritten frame would cross address families. Adapter/generation
        // scoping of the port index is out of scope for release 1.
        if (association.OriginalKey.AddressFamily != key.AddressFamily) return TcpRedirectOutcome.NotRelevant;

        var original = association.OriginalKey;
        var rewrittenFrame = packet.Lease.Frame.ToArray();
        var originalRemote = original.Remote;
        var originalClient = original.Local;
        if (!PacketChecksums.TryRewriteTcpEndpoints(rewrittenFrame, originalRemote.Address, originalRemote.Port, originalClient.Address, originalClient.Port))
        {
            return TcpRedirectOutcome.Blocked;
        }
        SwapEthernetMacs(rewrittenFrame);

        // Host-originated flows terminate on this host (reverse to MSTCP); forwarded flows (client
        // on a VM/remote side) must be sent back to the origin adapter instead.
        var towardMstcp = original.Origin == FlowOriginKind.Host;
        try
        {
            await _injector.InjectAsync(rewrittenFrame, towardMstcp, packet.Metadata.AdapterHandle, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return TcpRedirectOutcome.Blocked;
        }

        return TcpRedirectOutcome.Injected;
    }

    /// <summary>
    /// Routes a proxy-selected TCP packet to the correct redirect phase. The flow dispatcher sends
    /// every packet on a proxy-decided TCP flow here. A SYN starts or re-injects the redirect; a
    /// packet whose source is a known translated listener tuple is a reverse packet; anything else
    /// is mid-flow data on the redirect leg and is passed through.
    /// </summary>
    /// <summary>
    /// Handles a packet that belongs to an active redirect leg (source or destination port is a
    /// proxy listener port) by reversing it back to the original server:client tuple. Returns
    /// <see cref="TcpRedirectOutcome.NotRelevant"/> when the packet has no proxy-port relationship,
    /// so the caller can continue normal flow/policy processing. Runs before flow lookup and policy
    /// so a reverse packet is never re-evaluated as a new client flow.
    /// </summary>
    public async ValueTask<TcpRedirectOutcome> HandleReverseIfApplicableAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // H1/M5 gate before the numeric-port lookup: this handler owns TCP reverse routing. A UDP
        // or other-protocol frame whose local/remote port numerically matches an active TCP
        // listener port must be left to normal flow/policy handling, never dropped here.
        var key = packet.Context.Key;
        if (key.Protocol != TransportProtocol.Tcp) return TcpRedirectOutcome.NotRelevant;
        var now = DateTimeOffset.UtcNow;
        if (!_table.TryResolveByProxyPort(key.Local.Port, now, out _) && !_table.TryResolveByProxyPort(key.Remote.Port, now, out _))
        {
            return TcpRedirectOutcome.NotRelevant;
        }

        return await HandleReverseAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TcpRedirectOutcome> HandlePacketAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(server);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = packet.Context.Key;
        var now = DateTimeOffset.UtcNow;
        var isSyn = IsTcpSyn(packet.Lease.Frame.Span);

        if (_table.TryResolveByProxyPort(key.Local.Port, now, out _) || _table.TryResolveByProxyPort(key.Remote.Port, now, out _))
        {
            return await HandleReverseAsync(packet, cancellationToken).ConfigureAwait(false);
        }

        if (isSyn)
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

        return TcpRedirectOutcome.NotRelevant;
    }

    /// <summary>
    /// Detects a TCP SYN (SYN set, ACK clear) from the raw Ethernet frame. The flags byte is at
    /// the TCP header offset + 13; SYN = 0x02, ACK = 0x10.
    /// </summary>
    private static bool IsTcpSyn(ReadOnlySpan<byte> frame)
    {
        if (!IpTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return false;
        var tcpFlagsOffset = 14 + view.IpHeaderLength + 13;
        if (frame.Length <= tcpFlagsOffset) return false;
        var flags = frame[tcpFlagsOffset];
        const byte Syn = 0x02;
        const byte Ack = 0x10;
        return (flags & Syn) != 0 && (flags & Ack) == 0;
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
    /// the relay completing/ending (see <see cref="ObserveRelayCompletionAsync"/>), and a truly
    /// stalled relay is reclaimed by the read/write timeouts in <see cref="TcpProxyRelay"/>.
    /// </summary>
    public async ValueTask<int> RemoveExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout)
    {
        TcpRedirectSession[] expired;
        lock (_gate)
        {
            expired = _sessions.Values
                .Where(session => session.Association.Phase == RelayPhase.Redirecting && now - session.Association.LastActivityUtc >= idleTimeout)
                .ToArray();
        }

        var removed = 0;
        foreach (var session in expired)
        {
            await TearDownSessionAsync(session).ConfigureAwait(false);
            removed++;
        }
        return removed;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _shutdown.CancelAsync().ConfigureAwait(false);

        TcpRedirectSession[] sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            await ReleaseAssociationAsync(session.Listener, session.Association, session.SelfTrafficToken).ConfigureAwait(false);
            if (session.Relay is not null) await session.Relay.DisposeAsync().ConfigureAwait(false);
            if (session.AcceptLoop is not null)
            {
                try { await session.AcceptLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { /* cancellation is the expected shutdown path */ }
                catch (ObjectDisposedException) { /* the listener was already disposed during shutdown */ }
            }
        }

        _shutdown.Dispose();
    }



    private async Task RunAcceptLoopAsync(TcpRedirectSession session, CancellationToken externalCancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, externalCancellationToken);
        var token = linked.Token;
        while (!token.IsCancellationRequested)
        {
            ITcpAcceptedConnection accepted;
            try
            {
                accepted = await session.Listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // The listener was disposed during shutdown.
                return;
            }
            catch (Exception acceptEx)
            {
                // L3: a transient accept error is retried after a bounded delay, never a tight
                // busy-loop. Cancellation and a disposed listener already break out above.
                _logger.Warn($"TCP redirect accept failed ({acceptEx.GetType().Name}); retrying after a bounded delay.");
                await BoundedRetryDelayAsync(token).ConfigureAwait(false);
                continue;
            }

            try
            {
                var relay = await _relayFactory.EstablishAsync(session.Association.OriginalDestination, accepted, session.Server, token).ConfigureAwait(false);
                session.Relay = relay;
                session.Association.Phase = RelayPhase.Relaying;
                _ = ObserveRelayCompletionAsync(session, relay, token);
                await DrainRedundantConnectionsAsync(session, token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await accepted.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch
            {
                _logger.Warn("TCP redirect relay setup failed; releasing the flow alias.");
                await accepted.DisposeAsync().ConfigureAwait(false);
                await TearDownSessionAsync(session).ConfigureAwait(false);
                return;
            }
        }
    }

    private static async Task DrainRedundantConnectionsAsync(TcpRedirectSession session, CancellationToken token)
    {
        // One logical flow owns exactly one relay. A retransmitted SYN can make MSTCP open a second
        // connection on the same listener; any further accepts are redundant and are closed instead
        // of starting a second relay. The loop ends when teardown disposes the listener.
        while (!token.IsCancellationRequested)
        {
            ITcpAcceptedConnection extra;
            try
            {
                extra = await session.Listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                // L3: a non-cancel, non-disposed accept error must not be a tight busy-loop; back
                // off for a bounded delay before retrying. The loop still ends when teardown
                // disposes the listener.
                await BoundedRetryDelayAsync(token).ConfigureAwait(false);
                continue;
            }
            await extra.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A short bounded back-off between retries of a transient accept error, so a failing accept
    /// loop cannot spin flat-out (L3).
    /// </summary>
    private static readonly TimeSpan BoundedAcceptRetryDelay = TimeSpan.FromMilliseconds(100);

    private static async ValueTask BoundedRetryDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(BoundedAcceptRetryDelay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation ends the accept loop.
        }
    }

    private async Task ObserveRelayCompletionAsync(TcpRedirectSession session, ITcpRelay relay, CancellationToken token)
    {
        try
        {
            await relay.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // A relay that errored or ended removes the flow so a future SYN re-arms setup.
        }
        await TearDownSessionAsync(session).ConfigureAwait(false);
    }

    private async ValueTask TearDownSessionAsync(TcpRedirectSession session)
    {
        lock (_gate) _sessions.Remove(session.Association.OriginalKey);
        await ReleaseAssociationAsync(session.Listener, session.Association, session.SelfTrafficToken).ConfigureAwait(false);
        if (session.Relay is not null)
        {
            await session.Relay.DisposeAsync().ConfigureAwait(false);
            session.Relay = null;
        }
    }

    private async ValueTask ReleaseAssociationAsync(ITcpRedirectListener listener, TcpRedirectAssociation association, SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken)
    {
        _table.TryRemove(association);
        try { await listener.DisposeAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* the listener may already be disposed during teardown */ }
        selfTrafficToken?.Dispose();
    }

    /// <summary>
    /// The resources acquired by a successful new-redirect setup, returned to the caller so it can
    /// register the session and start the accept loop. A null return means fail-closed blocking.
    /// </summary>
    private sealed record RedirectSetup(ITcpRedirectListener? Listener, TcpRedirectAssociation Association, SelfTrafficRegistry.SelfTrafficToken? SelfTrafficToken);

    private sealed class TcpRedirectSession(TcpRedirectAssociation association, ITcpRedirectListener listener, SelfTrafficRegistry.SelfTrafficToken selfTrafficToken, Socks5Server server)
    {
        public TcpRedirectAssociation Association { get; } = association;
        public ITcpRedirectListener Listener { get; } = listener;
        public SelfTrafficRegistry.SelfTrafficToken SelfTrafficToken { get; } = selfTrafficToken;
        public Socks5Server Server { get; } = server;
        public ITcpRelay? Relay { get; set; }
        public Task? AcceptLoop { get; set; }
    }
}
