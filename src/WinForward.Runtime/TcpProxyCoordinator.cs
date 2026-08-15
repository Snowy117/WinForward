using System.Net;
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
/// releases the listener, the table alias, and the self-traffic token.
/// </summary>
public sealed class TcpProxyCoordinator : IAsyncDisposable
{
    private readonly ITcpRedirectListenerFactory _listenerFactory;
    private readonly ITcpProxyRelayFactory _relayFactory;
    private readonly ITcpRedirectInjector _injector;
    private readonly TcpRedirectTable _table;
    private readonly SelfTrafficRegistry _selfTraffic;
    private readonly IAdapterLocalAddressProvider _localAddresses;
    private readonly IRuntimeLogger _logger;
    private readonly int _capacity;
    private readonly Dictionary<FlowKey, TcpRedirectSession> _sessions = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource _setupsDrained = CompletedSource();
    private Task? _disposeTask;
    private int _inflightSetups;
    private long _concurrentLoserCount;
    private bool _disposed;

    public TcpProxyCoordinator(
        ITcpRedirectListenerFactory listenerFactory,
        ITcpProxyRelayFactory relayFactory,
        ITcpRedirectInjector injector,
        TcpRedirectTable table,
        SelfTrafficRegistry selfTraffic,
        IAdapterLocalAddressProvider localAddresses,
        IRuntimeLogger? logger = null,
        int capacity = 16_384)
    {
        ArgumentNullException.ThrowIfNull(listenerFactory);
        ArgumentNullException.ThrowIfNull(relayFactory);
        ArgumentNullException.ThrowIfNull(injector);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentNullException.ThrowIfNull(localAddresses);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _listenerFactory = listenerFactory;
        _relayFactory = relayFactory;
        _injector = injector;
        _table = table;
        _selfTraffic = selfTraffic;
        _localAddresses = localAddresses;
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
        EnterSetup();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

            var key = packet.Context.Key;
            if (key.Protocol != TransportProtocol.Tcp)
            {
                throw new ArgumentException("TCP coordinator accepts only TCP flow keys.", nameof(packet));
            }

            // A flow already claimed by a prior SYN reuses its decision: touch the association and
            // re-inject the rewritten SYN toward the listener. Policy is evaluated exactly once.
            if (_table.TryResolveByOriginal(key, DateTimeOffset.UtcNow, out var existing) && existing is not null)
            {
                LogTrace("tcp.redirect.reused", packet, existing);
                return await ReinjectExistingSynAsync(packet, existing, cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                if (_sessions.Count >= _capacity)
                {
                    LogTrace("tcp.redirect.rejected", packet, null, "capacity");
                    return TcpRedirectOutcome.Blocked;
                }
            }

            var setup = await SetupNewRedirectAsync(packet, server, cancellationToken).ConfigureAwait(false);
            if (setup is null) return TcpRedirectOutcome.Blocked;

            // A concurrent caller claimed this flow first; the redundant listener was already released.
            // Re-inject the SYN against the existing association without creating a new session.
            if (setup.Session is null)
            {
                return await ReinjectExistingSynAsync(packet, setup.Association, cancellationToken).ConfigureAwait(false);
            }

            setup.Session.AcceptLoop = RunAcceptLoopAsync(setup.Session);
            LogDebug("tcp.redirect.created", setup.Session, "created");

            return TcpRedirectOutcome.Injected;
        }
        finally
        {
            ExitSetup();
        }
    }

    /// <summary>
    /// Allocates the listener, claims the association, rewrites the SYN, registers loop prevention,
    /// and injects the rewritten frame. Returns null (fail-closed) when any step fails, after
    /// releasing the listener, the table alias, and the self-traffic token it may have acquired.
    /// </summary>
    private async ValueTask<RedirectSetup?> SetupNewRedirectAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
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
            LogTrace("tcp.redirect.rejected", packet, null, "listenerAllocation");
            _logger.Warn("TCP redirect failed: listener allocation failed, blocking the flow.");
            return null;
        }

        var translatedTuple = listener.TranslatedTuple;
        var originAdapter = new AdapterContext(key.OriginAdapterId, packet.Context.AdapterName, key.OriginAdapterGeneration);
        var originalDestination = key.Remote;

        var forwardLocalAddress = ResolveForwardLocalAddress(key);
        if (key.Origin == FlowOriginKind.Forwarded && forwardLocalAddress is null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            LogTrace("tcp.redirect.rejected", packet, null, "localAddress");
            _logger.Warn("TCP redirect failed: no local address available on the origin adapter, blocking the flow.");
            return null;
        }

        if (!_table.TryClaim(key, originalDestination, originAdapter, packet.Metadata.AdapterHandle, translatedTuple, forwardLocalAddress, DateTimeOffset.UtcNow, out var association) || association is null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            LogTrace("tcp.redirect.rejected", packet, null, "claim");
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
            return new RedirectSetup(association, Session: null);
        }

        return await CompleteNewRedirectAsync(packet, listener, association, translatedTuple, server, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the adapter-local redirect destination address for a forwarded flow's SYN
    /// (DNAT-to-local). The host IP-swap shape would send the frame to the client's own address,
    /// which is not local for a forwarded flow, so the listener would never see it. Loopback is
    /// deliberately excluded by the provider: a martian-source reverse reply could be dropped by
    /// the stack before it reaches the capture layer for rewriting. Returns null for host flows
    /// (host shape needs no such address) and when the origin adapter owns no usable address of
    /// the flow's family; the caller fails closed on the latter.
    /// </summary>
    private IPAddress? ResolveForwardLocalAddress(FlowKey key)
    {
        if (key.Origin != FlowOriginKind.Forwarded) return null;
        return key.OriginAdapterId is { } originAdapterId
            ? _localAddresses.SelectLocalAddress(originAdapterId, key.AddressFamily, key.Local.Address)
            : null;
    }

    private async ValueTask<RedirectSetup?> CompleteNewRedirectAsync(CapturedFlowPacket packet, ITcpRedirectListener listener, TcpRedirectAssociation association, Endpoint translatedTuple, Socks5Server server, CancellationToken cancellationToken)
    {
        var session = RegisterSession(listener, association, translatedTuple, server, packet.FlowGeneration);
        if (session is null)
        {
            await ReleaseAssociationAsync(listener, association, null).ConfigureAwait(false);
            return null;
        }

        var key = packet.Context.Key;
        var rewrittenFrame = packet.Lease.Frame.ToArray();
        var originalClient = key.Local;
        var originalServer = key.Remote;

        if (!TryRewriteForwardLeg(rewrittenFrame, originalClient, originalServer, association, translatedTuple.Port))
        {
            await TearDownSessionAsync(session).ConfigureAwait(false);
            LogTrace("tcp.redirect.rejected", packet, association, "rewrite");
            _logger.Warn("TCP redirect failed: SYN endpoint rewrite failed, blocking the flow.");
            return null;
        }

        RecordClientSyn(packet.Lease.Frame.Span, association);

        // L4 clarity: this exact self-traffic key cannot be matched by the wildcard registry because
        // the observable reverse leg is (client:orig_port) -> (client_ip:proxy_port), and the
        // per-client source port is unknown until a connection arrives. The listener/reverse leg is
        // therefore guarded by the TCP-only reverse hook (see HandleReverseIfApplicableAsync, gated
        // to TCP by H1 in the dispatcher), which reverses before flow lookup/policy. This
        // registration is retained as writer-intent belt-and-suspenders and because the registry is
        // the natural home for an exact local-loopback listener tuple when one becomes expressible.
        try
        {
            await _injector.InjectAsync(rewrittenFrame, towardMstcp: true, packet.Metadata.AdapterHandle, cancellationToken).ConfigureAwait(false);
            LogTrace("tcp.redirect.injected", packet, association);
        }
        catch (OperationCanceledException)
        {
            await TearDownSessionAsync(session).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TearDownSessionAsync(session).ConfigureAwait(false);
            LogTrace("tcp.redirect.rejected", packet, association, "injection");
            _logger.Warn("TCP redirect failed: rewritten-frame injection failed, blocking the flow.");
            return null;
        }

        return new RedirectSetup(association, session);
    }

    private TcpRedirectSession? RegisterSession(ITcpRedirectListener listener, TcpRedirectAssociation association, Endpoint translatedTuple, Socks5Server server, long flowGeneration)
    {
        var selfTrafficToken = _selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Tcp, translatedTuple, translatedTuple));
        var session = new TcpRedirectSession(association, listener, selfTrafficToken, server, _shutdown.Token, flowGeneration);
        lock (_gate)
        {
            if (_disposed)
            {
                session.Retire();
                session.DisposeLifetime();
                selfTrafficToken.Dispose();
                return null;
            }

            _sessions.Add(association.OriginalKey, session);
            return session;
        }
    }

    private async ValueTask<TcpRedirectOutcome> ReinjectExistingSynAsync(CapturedFlowPacket packet, TcpRedirectAssociation association, CancellationToken cancellationToken)
        => await ReinjectExistingFlowDataAsync(packet, association, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Rewrites one frame on the original client-to-listener leg toward the redirect listener and
    /// applies the origin-specific MAC handling. The forwarded DNAT shape preserves the client's
    /// source tuple and moves only the destination to the adapter-local listener address, so the
    /// accepted peer is the client itself and the arrival MACs (already addressing this host) are
    /// kept. The host shape follows the official WinpkFilter local_redirect pattern: swap MACs and
    /// IPs, rewrite the destination port to the proxy port; the SOURCE PORT is kept as the client's
    /// original port (the redirector only rewrites th_dport, never th_sport), so the accepted
    /// connection has peer = server:client_orig_port, which the per-flow mapping resolves by client
    /// source port.
    /// </summary>
    private static bool TryRewriteForwardLeg(Span<byte> frame, Endpoint originalClient, Endpoint originalServer, TcpRedirectAssociation association, ushort listenerPort)
    {
        if (association.ForwardLocalAddress is { } forwardLocalAddress)
        {
            return PacketChecksums.TryRewriteTcpEndpoints(frame, originalClient.Address, originalClient.Port, forwardLocalAddress, listenerPort);
        }
        if (!PacketChecksums.TryRewriteTcpEndpoints(frame, originalServer.Address, originalClient.Port, originalClient.Address, listenerPort)) return false;
        SwapEthernetMacs(frame);
        return true;
    }

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

    /// <summary>
    /// Records the client ISN and a bounded copy of the original SYN frame on the association.
    /// Together with the server ISN captured by <see cref="RecordServerSynAck"/> this is everything
    /// a relay setup failure needs to abort the client-visible connection with an in-window RST.
    /// </summary>
    private static void RecordClientSyn(ReadOnlySpan<byte> frame, TcpRedirectAssociation association)
    {
        if (!IpTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return;
        var sequenceOffset = 14 + view.IpHeaderLength + 4;
        if (frame.Length < sequenceOffset + 4) return;
        association.ClientInitialSeq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(sequenceOffset, 4));
        association.OriginalSynFrameCopy = frame.Slice(0, Math.Min(frame.Length, 128)).ToArray();
    }

    /// <summary>
    /// Captures the listener-side ISN when the reverse leg's SYN-ACK passes through, so a later
    /// relay setup failure can craft a reset the client's stack accepts as in-window.
    /// </summary>
    private static void RecordServerSynAck(ReadOnlySpan<byte> frame, TcpRedirectAssociation association)
    {
        if (!IpTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return;
        var flagsOffset = 14 + view.IpHeaderLength + 13;
        if (frame.Length <= flagsOffset || (frame[flagsOffset] & 0x12) != 0x12) return;
        var sequenceOffset = 14 + view.IpHeaderLength + 4;
        if (frame.Length < sequenceOffset + 4) return;
        association.ServerInitialSeq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(sequenceOffset, 4));
    }

    private async ValueTask<TcpRedirectOutcome> ReinjectExistingFlowDataAsync(CapturedFlowPacket packet, TcpRedirectAssociation association, CancellationToken cancellationToken)
    {
        var rewrittenFrame = packet.Lease.Frame.ToArray();
        var originalClient = packet.Context.Key.Local;
        var originalServer = association.OriginalKey.Remote;
        if (!TryRewriteForwardLeg(rewrittenFrame, originalClient, originalServer, association, association.TranslatedListenerTuple.Port))
        {
            await FailAssociationAsync(association).ConfigureAwait(false);
            return TcpRedirectOutcome.Blocked;
        }
        try
        {
            await _injector.InjectAsync(rewrittenFrame, towardMstcp: true, packet.Metadata.AdapterHandle, cancellationToken).ConfigureAwait(false);
            LogTrace("tcp.redirect.injected", packet, association);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await FailAssociationAsync(association).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await FailAssociationAsync(association).ConfigureAwait(false);
            return TcpRedirectOutcome.Blocked;
        }
        return TcpRedirectOutcome.Injected;
    }

    public async ValueTask<TcpRedirectOutcome> HandleReverseAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        var key = packet.Context.Key;
        // M5 belt-and-suspenders: this coordinator owns TCP redirect table entries only. The
        // dispatcher already gates the reverse handler to TCP (H1), but a non-TCP packet must never
        // be routed into reverse handling regardless of call context.
        if (key.Protocol != TransportProtocol.Tcp) return TcpRedirectOutcome.NotRelevant;
        var now = DateTimeOffset.UtcNow;
        if (!_table.TryResolveByReverse(key.Local, key.Remote, now, out var association) || association is null) return TcpRedirectOutcome.NotRelevant;

        var original = association.OriginalKey;
        var rewrittenFrame = packet.Lease.Frame.ToArray();
        var originalRemote = original.Remote;
        var originalClient = original.Local;
        RecordServerSynAck(packet.Lease.Frame.Span, association);

        // Host-originated flows terminate on this host (reverse to MSTCP); forwarded flows (client
        // on a VM/remote side) must be sent back to the origin adapter instead.
        var towardMstcp = original.Origin == FlowOriginKind.Host;
        if (!PacketChecksums.TryRewriteTcpEndpoints(rewrittenFrame, originalRemote.Address, originalRemote.Port, originalClient.Address, originalClient.Port))
        {
            await FailAssociationAsync(association).ConfigureAwait(false);
            return TcpRedirectOutcome.Blocked;
        }
        // The MAC swap makes the looped-back frame look inbound from the router for a host flow.
        // A forwarded flow's reversed frame is emitted on the origin adapter toward the client, and
        // its arrival MACs (this host -> client) are already correct.
        if (towardMstcp) SwapEthernetMacs(rewrittenFrame);
        try
        {
            if (!towardMstcp && association.OriginAdapterHandle == 0)
            {
                await FailAssociationAsync(association).ConfigureAwait(false);
                return TcpRedirectOutcome.Blocked;
            }
            var targetHandle = towardMstcp ? packet.Metadata.AdapterHandle : association.OriginAdapterHandle;
            await _injector.InjectAsync(rewrittenFrame, towardMstcp, targetHandle, cancellationToken).ConfigureAwait(false);
            LogTrace("tcp.reverse.injected", packet, association);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await FailAssociationAsync(association).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await FailAssociationAsync(association).ConfigureAwait(false);
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        // H1/M5 gate before the numeric-port lookup: this handler owns TCP reverse routing. A UDP
        // or other-protocol frame whose local/remote port numerically matches an active TCP
        // listener port must be left to normal flow/policy handling, never dropped here.
        var key = packet.Context.Key;
        if (key.Protocol != TransportProtocol.Tcp) return TcpRedirectOutcome.NotRelevant;
        if (!_table.IsReverseCandidate(key.Local, key.Remote)) return TcpRedirectOutcome.NotRelevant;

        return await HandleReverseAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TcpRedirectOutcome> HandlePacketAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(server);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        var key = packet.Context.Key;
        var now = DateTimeOffset.UtcNow;
        var syn = ClassifyTcpSyn(packet.Lease.Frame.Span);

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

        return TcpRedirectOutcome.NotRelevant;
    }

    /// <summary>
    /// Detects a TCP SYN (SYN set, ACK clear) from the raw Ethernet frame. The flags byte is at
    /// the TCP header offset + 13; SYN = 0x02, ACK = 0x10.
    /// </summary>
    private static TcpSynKind ClassifyTcpSyn(ReadOnlySpan<byte> frame)
    {
        if (!IpTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return TcpSynKind.None;
        var tcpFlagsOffset = 14 + view.IpHeaderLength + 13;
        if (frame.Length <= tcpFlagsOffset) return TcpSynKind.None;
        var flags = frame[tcpFlagsOffset];
        const byte Syn = 0x02;
        const byte Ack = 0x10;
        if ((flags & Syn) == 0 || (flags & Ack) != 0) return TcpSynKind.None;

        var etherType = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        var transportLength = etherType == 0x0800
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(16, 2)) - view.IpHeaderLength
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(18, 2)) - (view.IpHeaderLength - 40);
        return transportLength == view.TransportHeaderLength ? TcpSynKind.Empty : TcpSynKind.WithPayload;
    }

    private enum TcpSynKind
    {
        None,
        Empty,
        WithPayload,
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
        RetiredSession[] expired;
        lock (_gate)
        {
            expired = _sessions.Values
                .Where(session => session.Association.Phase == RelayPhase.Redirecting && now - session.Association.LastActivityUtc >= idleTimeout)
                .Select(RetireSessionUnderGate)
                .ToArray();
        }

        foreach (var retired in expired)
        {
            await ReleaseRetiredSessionAsync(retired).ConfigureAwait(false);
        }
        return expired.Length;
    }

    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        TaskCompletionSource? start = null;
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                disposeTask = _disposeTask;
            }
            else
            {
                _disposed = true;
                start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                disposeTask = _disposeTask = start.Task;
            }
        }

        if (start is not null) _ = RunDisposeAsync(start);
        await disposeTask.ConfigureAwait(false);
    }

    private async Task RunDisposeAsync(TaskCompletionSource completion)
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

    private async Task DisposeCoreAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Task setupsDrained;
        lock (_gate) setupsDrained = _setupsDrained.Task;
        await setupsDrained.ConfigureAwait(false);

        RetiredSession[] sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.Select(RetireSessionUnderGate).ToArray();
        }

        foreach (var retired in sessions)
        {
            await ReleaseRetiredSessionAsync(retired).ConfigureAwait(false);
            var session = retired.Session;
            if (session.AcceptLoop is not null)
            {
                try { await session.AcceptLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { /* cancellation is the expected shutdown path */ }
                catch (ObjectDisposedException) { /* the listener was already disposed during shutdown */ }
            }
        }

        _shutdown.Dispose();
    }



    /// <summary>
    /// Surfaces a relay setup failure to the client as a protocol-correct RST|ACK from the original
    /// server endpoint instead of leaving its established connection hanging. The reset is crafted
    /// from the recorded SYN template and both initial sequence numbers, so it stays valid even
    /// though the redirect-table alias is torn down right after. Degrades to plain teardown when
    /// either sequence number was never observed.
    /// </summary>
    private async ValueTask TryInjectClientResetAsync(TcpRedirectSession session)
    {
        var association = session.Association;
        if (association.OriginalSynFrameCopy is not { } synTemplate || association.ClientInitialSeq is not uint clientInitialSeq || association.ServerInitialSeq is not uint serverInitialSeq) return;
        var reset = TcpResetBuilder.BuildReset(synTemplate, association.OriginalDestination.Address, association.OriginalDestination.Port,
            association.OriginalKey.Local.Address, association.OriginalKey.Local.Port, serverInitialSeq + 1, clientInitialSeq + 1);
        if (reset is null) return;
        try
        {
            await _injector.InjectAsync(reset, association.OriginalKey.Origin != FlowOriginKind.Forwarded, association.OriginAdapterHandle, session.Token).ConfigureAwait(false);
            LogDebug("tcp.redirect.clientReset", session, "injected");
        }
        catch (OperationCanceledException)
        {
            // Shutdown or session teardown cancelled the best-effort reset.
        }
        catch (Exception exception)
        {
            _logger.Warn($"TCP redirect client reset injection failed ({exception.GetType().Name}).");
        }
    }

    /// <summary>
    /// Releases a flow whose upstream relay could not be established: closes the accepted socket,
    /// best-effort resets the client-visible connection, then tears the session down.
    /// </summary>
    private async ValueTask HandleRelaySetupFailureAsync(TcpRedirectSession session, ITcpAcceptedConnection accepted)
    {
        _logger.Warn("TCP redirect relay setup failed; resetting the client connection and releasing the flow alias.");
        await accepted.DisposeAsync().ConfigureAwait(false);
        await TryInjectClientResetAsync(session).ConfigureAwait(false);
        await TearDownSessionAsync(session).ConfigureAwait(false);
    }

    private async Task RunAcceptLoopAsync(TcpRedirectSession session)
    {
        var token = session.Token;
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
                if (accepted.RemoteEndPoint != session.Association.AcceptedPeerEndpoint)
                {
                    _logger.Warn("TCP redirect accepted an unrelated peer; closing it.");
                    await accepted.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                var relay = await _relayFactory.EstablishAsync(session.Association.OriginalDestination, accepted, session.Server, token).ConfigureAwait(false);
                if (!TryAttachRelay(session, relay))
                {
                    await relay.DisposeAsync().ConfigureAwait(false);
                    await accepted.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                LogDebug("tcp.relay.started", session, "established");
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
                await HandleRelaySetupFailureAsync(session, accepted).ConfigureAwait(false);
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
        LogDebug("tcp.relay.ended", session, "completed");
        await TearDownSessionAsync(session).ConfigureAwait(false);
    }

    private async ValueTask TearDownSessionAsync(TcpRedirectSession session)
    {
        RetiredSession? retired;
        lock (_gate)
        {
            retired = TryRetireSessionUnderGate(session);
        }
        if (retired is not null) await ReleaseRetiredSessionAsync(retired).ConfigureAwait(false);
    }

    private RetiredSession? TryRetireSessionUnderGate(TcpRedirectSession session)
    {
        if (!_sessions.TryGetValue(session.Association.OriginalKey, out var current) || !ReferenceEquals(current, session)) return null;
        return RetireSessionUnderGate(session);
    }

    private RetiredSession RetireSessionUnderGate(TcpRedirectSession session)
    {
        _sessions.Remove(session.Association.OriginalKey);
        session.Association.Phase = RelayPhase.Closing;
        session.Retire();
        var relay = session.Relay;
        session.Relay = null;
        return new RetiredSession(session, relay);
    }

    private async ValueTask ReleaseRetiredSessionAsync(RetiredSession retired)
    {
        var session = retired.Session;
        LogDebug("tcp.redirect.closed", session, "closed");
        try
        {
            await ReleaseAssociationAsync(session.Listener, session.Association, session.SelfTrafficToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Warn($"TCP redirect listener disposal failed ({exception.GetType().Name}).");
        }
        if (retired.Relay is not null)
        {
            try { await retired.Relay.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { _logger.Warn($"TCP redirect relay disposal failed ({exception.GetType().Name})."); }
        }
        session.DisposeLifetime();
    }

    private bool TryAttachRelay(TcpRedirectSession session, ITcpRelay relay)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.Association.OriginalKey, out var current) || !ReferenceEquals(current, session) || session.IsRetired || session.Association.Phase != RelayPhase.Redirecting || session.Relay is not null) return false;
            session.Relay = relay;
            session.Association.Phase = RelayPhase.Relaying;
            return true;
        }
    }

    private async ValueTask FailAssociationAsync(TcpRedirectAssociation association)
    {
        TcpRedirectSession? session;
        lock (_gate) _sessions.TryGetValue(association.OriginalKey, out session);
        if (session is not null) await TearDownSessionAsync(session).ConfigureAwait(false);
        else _table.TryRemove(association);
    }

    private void EnterSetup()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inflightSetups++ == 0) _setupsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void ExitSetup()
    {
        lock (_gate)
        {
            if (--_inflightSetups == 0) _setupsDrained.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private async ValueTask ReleaseAssociationAsync(ITcpRedirectListener listener, TcpRedirectAssociation association, SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken)
    {
        _table.TryRemove(association);
        try
        {
            try { await listener.DisposeAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* the listener may already be disposed during teardown */ }
        }
        finally
        {
            selfTrafficToken?.Dispose();
        }
    }

    private void LogDebug(string eventName, TcpRedirectSession session, string outcome)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Debug)) return;
        var association = session.Association;
        _logger.Event(RuntimeLogLevel.Debug, eventName,
            new("flow", session.FlowGeneration == 0 ? null : session.FlowGeneration),
            new("tcpAssociation", association.Generation), new("source", association.OriginalKey.Local),
            new("destination", association.OriginalKey.Remote), new("translated", association.TranslatedListenerTuple),
            new("proxy", session.Server.Name), new("outcome", outcome));
    }

    private void LogTrace(string eventName, CapturedFlowPacket packet, TcpRedirectAssociation? association, string? reason = null)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Trace)) return;
        _logger.Event(RuntimeLogLevel.Trace, eventName,
            new("packet", packet.PacketSequence == 0 ? null : packet.PacketSequence),
            new("flow", packet.FlowGeneration == 0 ? null : packet.FlowGeneration),
            new("tcpAssociation", association?.Generation), new("source", packet.Context.Key.Local),
            new("destination", packet.Context.Key.Remote), new("reason", reason));
    }

    /// <summary>
    /// The resources acquired by a successful new-redirect setup, returned to the caller so it can
    /// register the session and start the accept loop. A null return means fail-closed blocking.
    /// </summary>
    private sealed record RedirectSetup(TcpRedirectAssociation Association, TcpRedirectSession? Session);

    private sealed record RetiredSession(TcpRedirectSession Session, ITcpRelay? Relay);

    private sealed class TcpRedirectSession(TcpRedirectAssociation association, ITcpRedirectListener listener, SelfTrafficRegistry.SelfTrafficToken selfTrafficToken, Socks5Server server, CancellationToken shutdown, long flowGeneration = 0)
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
}
