namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The accept-and-relay loop for a TCP redirect session. A background task per session accepts
/// the first connection from the redirect listener, establishes the upstream SOCKS5 relay, and
/// observes relay completion (tearing the session down when the relay ends). Redundant accepts
/// from retransmitted SYNs are drained and closed instead of starting a second relay. Transient
/// accept errors are retried with a bounded delay so a failing loop never spins flat-out.
/// </summary>
internal sealed class TcpRedirectAcceptor
{
    private readonly ITcpProxyRelayFactory _relayFactory;
    private readonly IRuntimeLogger _logger;
    private readonly ClientResetInjector _clientReset;
    private readonly Func<TcpRedirectSession, ITcpRelay, bool> _tryAttachRelay;
    private readonly Func<TcpRedirectSession, ValueTask> _tearDownSession;

    /// <summary>A short bounded back-off between retries of a transient accept error (L3).</summary>
    private static readonly TimeSpan BoundedAcceptRetryDelay = TimeSpan.FromMilliseconds(100);

    public TcpRedirectAcceptor(ITcpProxyRelayFactory relayFactory, IRuntimeLogger logger, ClientResetInjector clientReset, Func<TcpRedirectSession, ITcpRelay, bool> tryAttachRelay, Func<TcpRedirectSession, ValueTask> tearDownSession)
    {
        _relayFactory = relayFactory;
        _logger = logger;
        _clientReset = clientReset;
        _tryAttachRelay = tryAttachRelay;
        _tearDownSession = tearDownSession;
    }

    public async Task RunAcceptLoopAsync(TcpRedirectSession session)
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

            if (!await TryEstablishRelayAsync(session, accepted).ConfigureAwait(false)) return;
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
        try
        {
            if (accepted.RemoteEndPoint != session.Association.AcceptedPeerEndpoint)
            {
                _logger.Warn("TCP redirect accepted an unrelated peer; closing it.");
                await accepted.DisposeAsync().ConfigureAwait(false);
                return true;
            }
            var relay = await _relayFactory.EstablishAsync(session.Association.OriginalDestination, accepted, session.Server, token).ConfigureAwait(false);
            if (!_tryAttachRelay(session, relay))
            {
                // The relay is discarded without an owner that would await its completion;
                // observe it now so a later fault never surfaces as an unobserved task
                // exception (S3).
                TcpRelayFaultObserver.Observe(relay, _logger);
                await relay.DisposeAsync().ConfigureAwait(false);
                await accepted.DisposeAsync().ConfigureAwait(false);
                return false;
            }
            TcpRedirectLogging.LogDebug(_logger, "tcp.relay.started", session, "established");
            _ = ObserveRelayCompletionAsync(session, relay, token);
            await DrainRedundantConnectionsAsync(session, token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await accepted.DisposeAsync().ConfigureAwait(false);
            return false;
        }
        catch
        {
            await _clientReset.HandleRelaySetupFailureAsync(session, accepted).ConfigureAwait(false);
            return false;
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
            await Task.Delay(BoundedAcceptRetryDelay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation ends the accept loop.
        }
    }

    private async Task ObserveRelayCompletionAsync(TcpRedirectSession session, ITcpRelay relay, CancellationToken token)
    {
        // Fire-and-forget (S5): nothing awaits this task, so a throw here would surface as an
        // unobserved task exception. The end handling itself is best-effort and must never block
        // the teardown that follows.
        try
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
            // A relay that stalled or faulted mid-flow blackholes the client's established
            // connection — the teardown tombstone would eat every subsequent retransmission — so
            // the end is surfaced client-visibly while the association still holds the SYN
            // template and sequence trackers. A clean end already propagated FINs and must not
            // be reset.
            if (relay is ITcpRelayEndInfo { EndKind: not RelayEndKind.CleanEnded })
            {
                try
                {
                    await _clientReset.TryInjectClientResetAsync(session.Association, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.Warn($"TCP redirect relay-end client reset failed ({exception.GetType().Name}).");
                }
            }
            TcpRedirectLogging.LogDebug(_logger, "tcp.relay.ended", session, "completed");
            await _tearDownSession(session).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Warn($"TCP redirect relay completion handling failed ({exception.GetType().Name}).");
        }
    }
}
