using WinForward.Configuration;
using WinForward.Core;
using WinForward.Windows;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The resources acquired by a successful new-redirect setup, returned to the caller so it can
/// start the accept loop. A null Session with a non-null Association means a concurrent caller
/// claimed the flow first (re-inject against the existing association); a null return means
/// fail-closed blocking.
/// </summary>
internal sealed record RedirectSetup(TcpRedirectAssociation Association, TcpRedirectSession? Session);

/// <summary>
/// The new-flow setup pipeline: allocates the local listener, resolves the forwarded-flow local
/// address, claims the flow exactly once in the redirect table, rewrites the SYN destination
/// toward the listener, registers the listener tuple in the loop-prevention registry, and injects
/// the rewritten frame. Any step failing returns null (fail-closed) after releasing the listener,
/// the table alias, and the self-traffic token it may have acquired.
/// </summary>
internal sealed class TcpRedirectSetup(ITcpRedirectListenerFactory listenerFactory, TcpRedirectTable table, SelfTrafficRegistry selfTraffic, IAdapterLocalAddressProvider localAddresses, ITcpRedirectInjector injector, IRuntimeLogger logger, TcpRedirectSessionStore store, ClientResetInjector clientReset, NativeBufferPool synCopyPool, TimeProvider timeProvider)
{
    private long _concurrentLoserCount;

    /// <summary>
    /// The number of concurrent SYN callers that arrived after another caller had already claimed
    /// the flow, detected a translated-tuple mismatch, released their redundant listener, and
    /// fallen back to re-inject. A non-zero value after a concurrent burst proves the redirect-table
    /// exactly-once path was exercised under genuine concurrency.
    /// </summary>
    internal long ConcurrentLoserCount => Interlocked.Read(ref _concurrentLoserCount);

    public async ValueTask<RedirectSetup?> SetupNewRedirectAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        var key = packet.Context.Key;

        ITcpRedirectListener listener;
        try
        {
            listener = await listenerFactory.CreateAsync(key.AddressFamily, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            TcpRedirectLogging.LogTrace(logger, "tcp.redirect.rejected", packet, association: null, "listenerAllocation");
            logger.Warn("TCP redirect failed: listener allocation failed, blocking the flow.");
            return null;
        }

        var translatedTuple = listener.TranslatedTuple;
        var originalDestination = key.Remote;

        var forwardLocalAddress = ResolveForwardLocalAddress(key);
        if (key.Origin == FlowOriginKind.Forwarded && forwardLocalAddress is null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            TcpRedirectLogging.LogTrace(logger, "tcp.redirect.rejected", packet, association: null, "localAddress");
            logger.Warn("TCP redirect failed: no local address available on the origin adapter, blocking the flow.");
            return null;
        }

        if (!table.TryClaim(key, originalDestination, packet.Metadata.AdapterHandle, translatedTuple, forwardLocalAddress, timeProvider.GetUtcNow(), out var association) || association is null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            TcpRedirectLogging.LogTrace(logger, "tcp.redirect.rejected", packet, association: null, "claim");
            logger.Warn("TCP redirect failed: redirect-table capacity reached or translated-tuple collision, blocking the flow.");
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
    private IPAddressValue? ResolveForwardLocalAddress(FlowKey key)
    {
        if (key.Origin != FlowOriginKind.Forwarded) return null;
        if (key.OriginAdapterId is not { } originAdapterId) return null;
        // Hot-path contract 1: this cold edge performs the flow's only From(IPAddress)
        // conversion; the per-packet rewrite reads the stored raw address instead.
        var address = localAddresses.SelectLocalAddress(originAdapterId, key.AddressFamily, key.Local.Address.ToIPAddress());
        return address is not null ? IPAddressValue.From(address) : (IPAddressValue?)null;
    }

    private async ValueTask<RedirectSetup?> CompleteNewRedirectAsync(CapturedFlowPacket packet, ITcpRedirectListener listener, TcpRedirectAssociation association, Endpoint translatedTuple, Socks5Server server, CancellationToken cancellationToken)
    {
        var session = RegisterSession(listener, association, translatedTuple, server, packet.FlowGeneration);
        if (session is null)
        {
            await store.ReleaseAssociationAsync(listener, association, selfTrafficToken: null).ConfigureAwait(false);
            return null;
        }

        // The rewrite below mutates the lease's pooled frame in place, so the original-SYN
        // template must be recorded first; afterwards the frame only holds the rewritten form.
        var frame = packet.Lease.Frame;
        var (_, _, originalClient, originalServer, _, _, _) = packet.Context.Key;

        if (!TcpFrameRewriter.TryGetWritableFrame(frame, out var writableFrame))
        {
            await store.TearDownSessionAsync(session).ConfigureAwait(false);
            TcpRedirectLogging.LogTrace(logger, "tcp.redirect.rejected", packet, association, "rewrite");
            logger.Warn("TCP redirect failed: the captured frame is not writable in place, blocking the flow.");
            return null;
        }

        TcpSequenceObservation.RecordClientSyn(frame.Span, association, synCopyPool);

        if (!TcpFrameRewriter.TryRewriteForwardLeg(writableFrame, originalClient, originalServer, association, translatedTuple.Port))
        {
            await store.TearDownSessionAsync(session).ConfigureAwait(false);
            TcpRedirectLogging.LogTrace(logger, "tcp.redirect.rejected", packet, association, "rewrite");
            logger.Warn("TCP redirect failed: SYN endpoint rewrite failed, blocking the flow.");
            return null;
        }

        // L4 clarity: the wildcard registry cannot match this key — the observable reverse leg is
        // (client:orig_port) -> (client_ip:proxy_port), whose per-client source port is unknown
        // until a connection arrives. The TCP-only reverse hook guards that leg; this registration
        // stays as writer-intent belt-and-suspenders for an exact listener tuple.
        try
        {
            await injector.InjectAsync(frame, towardMstcp: true, packet.Metadata.AdapterHandle, cancellationToken).ConfigureAwait(false);
            TcpRedirectLogging.LogTrace(logger, "tcp.redirect.injected", packet, association);
        }
        catch (OperationCanceledException)
        {
            await store.TearDownSessionAsync(session).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            // The single observable exit for injection failures; the reset leg is inert for a
            // first SYN (no server ISN yet) and the fail path tears the session down.
            await clientReset.HandleInjectionFailureAsync(association, packet.Metadata.AdapterHandle, towardMstcp: true, exception).ConfigureAwait(false);
            TcpRedirectLogging.LogTrace(logger, "tcp.redirect.rejected", packet, association, "injection");
            return null;
        }

        return new RedirectSetup(association, session);
    }

    private TcpRedirectSession? RegisterSession(ITcpRedirectListener listener, TcpRedirectAssociation association, Endpoint translatedTuple, Socks5Server server, long flowGeneration)
    {
        var selfTrafficToken = selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Tcp, translatedTuple, translatedTuple));
        var session = new TcpRedirectSession(association, listener, selfTrafficToken, server, flowGeneration, store.ShutdownToken);
        return store.TryRegister(session);
    }
}
