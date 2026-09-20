using WinForward.Configuration;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The accept-and-relay loop for a TCP redirect session. A background task per session accepts
/// the first connection from the redirect listener, establishes the upstream SOCKS5 relay, and
/// observes relay completion (tearing the session down when the relay ends). Redundant accepts
/// from retransmitted SYNs are drained and closed instead of starting a second relay. Transient
/// accept errors are retried with a bounded delay so a failing loop never spins flat-out.
/// </summary>
internal sealed class TcpRedirectAcceptor(ITcpProxyRelayFactory relayFactory, IRuntimeLogger logger, ClientResetInjector clientReset, Func<TcpRedirectSession, ITcpRelay, bool> tryAttachRelay, Func<TcpRedirectSession, ValueTask> tearDownSession)
{

    /// <summary>A short bounded back-off between retries of a transient accept error (L3).</summary>
    private static readonly TimeSpan s_boundedAcceptRetryDelay = TimeSpan.FromMilliseconds(100);

    public async Task RunAcceptLoopAsync(TcpRedirectSession session)
    {
        try
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
                    logger.Warn($"TCP redirect accept failed ({acceptEx.GetType().Name}); retrying after a bounded delay.");
                    await BoundedRetryDelayAsync(token).ConfigureAwait(false);
                    continue;
                }

                if (!await TryEstablishRelayAsync(session, accepted).ConfigureAwait(false)) return;
            }
        }
        finally
        {
            // This loop is the only reader of session.Token (directly and through
            // ClientResetInjector's session overload), so it owns the lifetime CTS disposal: the
            // store defers DisposeLifetime until the loop ends and a retire can never pull the
            // CTS out from under a concurrent Token read (R1).
            session.DisposeLifetime();
        }
    }

    /// <summary>
    /// Establishes the relay for an accepted connection whose peer was already validated. Returns
    /// false when the accept loop must stop (relay established or the session is finished); the
    /// unrelated-peer and cancellation cases return true so the loop waits for the next accept.
    /// </summary>
    private async Task<bool> TryEstablishRelayAsync(TcpRedirectSession session, ITcpAcceptedConnection accepted)
    {
        var token = session.Token;
        ITcpRelay? unattachedRelay;
        try
        {
            if (accepted.RemoteEndPoint != session.Association.AcceptedPeerEndpoint)
            {
                if (logger.IsEnabled(RuntimeLogLevel.Warn))
                {
                    logger.Event(RuntimeLogLevel.Warn, "tcp.redirect.unrelatedPeer",
                        new("listener", session.Listener.TranslatedTuple),
                        new("expected", session.Association.AcceptedPeerEndpoint),
                        new("actual", accepted.RemoteEndPoint));
                }
                await accepted.DisposeAsync().ConfigureAwait(false);
                return true;
            }
            var relay = await relayFactory.EstablishAsync(session.Association.OriginalDestination, accepted, session.Server, token).ConfigureAwait(false);
            if (!tryAttachRelay(session, relay))
            {
                unattachedRelay = relay;
            }
            else
            {
                TcpRedirectLogging.LogDebug(logger, "tcp.relay.started", session, "established");
                // Both terminal drains own the session's remaining lifetime: the relay-completion
                // observer runs the teardown, the redundant-accept drain spans until that teardown
                // disposes the listener. Awaiting both (no discarded tasks) makes session.AcceptLoop
                // the store's true quiescence wait (R1/R2).
                var relayCompletion = ObserveRelayCompletionAsync(session, relay, token);
                await DrainRedundantConnectionsAsync(session, token).ConfigureAwait(false);
                await relayCompletion.ConfigureAwait(false);
                return false;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await accepted.DisposeAsync().ConfigureAwait(false);
            return false;
        }
        catch (Exception exception)
        {
            await clientReset.HandleRelaySetupFailureAsync(session, accepted, exception).ConfigureAwait(false);
            return false;
        }

        // The session could no longer own a relay (it was retired, or the store is disposing, in
        // the accept-to-attach window), so the relay and the accepted connection have no owner and
        // the redirect must not be left half-open. This runs outside the setup try: a disposal
        // fault here is not a relay setup failure, and routing it through the reset/fail handler
        // would inject against an already retired session (R7).
        // The relay is discarded without an owner that would await its completion; observe it now
        // so a later fault never surfaces as an unobserved task exception (S3).
        TcpRelayFaultObserver.Observe(unattachedRelay, logger);
        await DiscardUnattachedRelayAsync(unattachedRelay, accepted).ConfigureAwait(false);
        await tearDownSession(session).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Best-effort disposal of a relay that could not be attached and of its accepted connection.
    /// Both disposals are contained so a fault here can never escape into the caller's
    /// setup-failure handling against a retired session.
    /// </summary>
    private async ValueTask DiscardUnattachedRelayAsync(ITcpRelay relay, ITcpAcceptedConnection accepted)
    {
        try
        {
            await relay.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.Warn($"TCP redirect relay disposal after a failed attach failed ({exception.GetType().Name}).");
        }

        try
        {
            await accepted.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.Warn($"TCP redirect accepted-connection disposal after a failed attach failed ({exception.GetType().Name}).");
        }
    }

    /// <summary>
    /// One logical flow owns exactly one relay. A retransmitted SYN can make MSTCP open a second
    /// connection on the same listener; any further accepts are redundant and are closed instead
    /// of starting a second relay. The loop ends when teardown disposes the listener.
    /// </summary>
    private static async Task DrainRedundantConnectionsAsync(TcpRedirectSession session, CancellationToken token)
    {
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

    private static async ValueTask BoundedRetryDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(s_boundedAcceptRetryDelay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation ends the accept loop.
        }
    }

    private async Task ObserveRelayCompletionAsync(TcpRedirectSession session, ITcpRelay relay, CancellationToken token)
    {
        // Best-effort end handling: neither an unobserved task exception nor an error in the
        // teardown that follows may escape. The token bounds the wait so a relay whose completion
        // never settles cannot hold the accept loop open past its own cancellation.
        try
        {
            try
            {
                await relay.Completion.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // A relay that errored or ended removes the flow so a future SYN re-arms setup.
            }
            // A relay that stalled or faulted mid-flow blackholes the client's established
            // connection — the teardown tombstone would eat every subsequent retransmission — so
            // the end is surfaced client-visibly while the association still holds the SYN
            // template and sequence trackers. A clean end already propagated FINs and must not
            // be reset.
            if (relay is ITcpRelayEndInfo { EndKind: not RelayEndKind.CleanEnded })
            {
                try
                {
                    await clientReset.TryInjectClientResetAsync(session.Association, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.Warn($"TCP redirect relay-end client reset failed ({exception.GetType().Name}).");
                }
            }
            TcpRedirectLogging.LogDebug(logger, "tcp.relay.ended", session, "completed");
            await tearDownSession(session).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.Warn($"TCP redirect relay completion handling failed ({exception.GetType().Name}).");
        }
    }
}
