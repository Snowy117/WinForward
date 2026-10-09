using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WinForward.Runtime.Logging;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The accept-and-relay loop for a TCP redirect session. A background task per session accepts
/// the first connection from the redirect listener, establishes the upstream SOCKS5 relay, and
/// observes relay completion (tearing the session down when the relay ends). Redundant accepts
/// from retransmitted SYNs are drained and closed instead of starting a second relay. Transient
/// accept errors are retried with a bounded delay so a failing loop never spins flat-out.
/// </summary>
internal sealed class TcpRedirectAcceptor(ITcpProxyRelayFactory relayFactory, ILogger logger, ClientResetInjector clientReset, Func<TcpRedirectSession, ITcpRelay, bool> tryAttachRelay, Func<TcpRedirectSession, ValueTask> tearDownSession, TimeSpan? drainDeadline = null)
{

    /// <summary>A short bounded back-off between retries of a transient accept error.</summary>
    private static readonly TimeSpan s_boundedAcceptRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long a clean end waits for the client's acknowledgement of the close before retiring
    /// anyway. It must exceed the RTO floor: a FIN or a tail segment lost on the way is recovered
    /// only by MSTCP's retransmission timer, so a shorter window would give up on the alias before
    /// the retransmission that justifies the drain.
    /// </summary>
    private static readonly TimeSpan s_defaultDrainDeadline = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _drainDeadline = ResolveDrainDeadline(drainDeadline);

    private static TimeSpan ResolveDrainDeadline(TimeSpan? drainDeadline)
    {
        if (drainDeadline is not { } deadline) return s_defaultDrainDeadline;
        // ReSharper disable once ConvertIfStatementToReturnStatement // Guard-clause throw reads failure-first and names the constructor parameter in ParamName; the ternary-throw form has no precedent in this repo.
        if (deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(drainDeadline), deadline, "The drain deadline must be positive.");
        return deadline;
    }

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
                    // A transient accept error is retried after a bounded delay, never a tight
                    // busy-loop. Cancellation and a disposed listener already break out above.
                    var error = acceptEx.GetType().Name;
                    TcpRedirectLog.TcpRedirectAcceptFailed(logger, error);
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
            // store defers DisposeLifetimeAsync until the loop ends and a retire can never pull the
            // CTS out from under a concurrent Token read. Disposal is single-flight, so the
            // store's own drain after it awaits this loop is a no-op.
            await session.DisposeLifetimeAsync().ConfigureAwait(false);
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
            if (!accepted.RemoteEndPoint.MatchesPeerIgnoringScope(session.Association.AcceptedPeerEndpoint))
            {
                TcpRedirectLog.TcpRedirectUnrelatedPeer(logger, session.Listener.TranslatedTuple, session.Association.AcceptedPeerEndpoint, accepted.RemoteEndPoint);
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
                TcpRedirectLog.TcpRelayStarted(
                    logger,
                    session.FlowGeneration == 0 ? null : session.FlowGeneration,
                    session.Association.Generation,
                    session.Association.OriginalKey.Local,
                    session.Association.OriginalKey.Remote,
                    session.Association.TranslatedListenerTuple,
                    session.Server.Name,
                    "established");
                // Both terminal drains own the session's remaining lifetime: the relay-completion
                // observer runs the teardown, the redundant-accept drain spans until that teardown
                // disposes the listener. Awaiting both (no discarded tasks) makes session.AcceptLoop
                // the store's true quiescence wait.
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
        // would inject against an already retired session.
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
            TcpRedirectLog.TcpRedirectRelayDisposalAfterFailedAttach(logger, exception);
        }

        try
        {
            await accepted.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TcpRedirectLog.TcpRedirectAcceptedConnectionDisposalFailed(logger, exception);
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
                // A non-cancel, non-disposed accept error must not be a tight busy-loop; back off
                // for a bounded delay before retrying. The loop still ends when teardown disposes
                // the listener.
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
            var endKind = relay is ITcpRelayEndInfo endInfo ? endInfo.EndKind : RelayEndKind.CleanEnded;
            if (endKind != RelayEndKind.CleanEnded)
            {
                await InjectAbnormalEndResetAsync(session).ConfigureAwait(false);
            }
            var endKindName = EndKindName(endKind);
            TcpRedirectLog.TcpRelayEnded(
                logger,
                session.FlowGeneration == 0 ? null : session.FlowGeneration,
                session.Association.Generation,
                session.Association.OriginalKey.Local,
                session.Association.OriginalKey.Remote,
                session.Association.TranslatedListenerTuple,
                session.Server.Name,
                endKindName);
            if (endKind == RelayEndKind.CleanEnded)
            {
                // A clean end is not a completed close: the client-visible connection lives through
                // the close handshake, and the alias is what carries it. Hold the session until the
                // client acknowledges, then retire as usual.
                await DrainCleanEndAsync(session, relay, token).ConfigureAwait(false);
            }
            await tearDownSession(session).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TcpRedirectLog.TcpRedirectRelayCompletionFailed(logger, exception);
        }
    }

    /// <summary>
    /// Keeps the session — and therefore the reverse alias — alive through the close handshake after
    /// a clean relay end: the retire waits for the client's acknowledgement, the deadline, or
    /// another teardown path. Holding the alias is what lets MSTCP's own FIN — emitted by the
    /// client-facing socket close inside RunPumpAsync — and any unacknowledged tail data before it
    /// reach the client, which the retire would cut off. The relay is disposed here rather than at
    /// the retire so the upstream budget is released at the drain's start. With no computable target
    /// there is no drain and the caller retires immediately.
    /// </summary>
    private async ValueTask DrainCleanEndAsync(TcpRedirectSession session, ITcpRelay relay, CancellationToken token)
    {
        var association = session.Association;
        if (!TryComputeDrainTargetAck(association, relay, out var targetAck)) return;
        try
        {
            await relay.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A disposal fault must not pin the session: the drain still runs, bounded by its deadline.
            TcpRedirectLog.TcpRedirectRelayDisposalFailed(logger, exception);
        }

        var completion = association.ArmDrainAsync(targetAck);
        var startedTicks = Stopwatch.GetTimestamp();
        var outcome = "acknowledged";
        try
        {
            await completion.WaitAsync(_drainDeadline, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            outcome = "deadline";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another retire path (an injection failure, a fragment, capacity, the sweep, shutdown)
            // won and cancelled the session; its teardown already owns the alias.
            outcome = "retired";
        }

        var elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds;
        TcpRedirectLog.TcpRedirectDrain(logger, association.Generation, association.OriginalKey.Local, association.OriginalKey.Remote, outcome, elapsedMilliseconds);
    }

    /// <summary>
    /// The acknowledgement a client sends once it has received the close: the listener's SYN-ACK and
    /// the FIN each consume one sequence number on top of the bytes the relay delivered to the
    /// client-facing socket. Any unknown input — no recorded SYN template, no listener-side ISN, or a
    /// relay without the end-info capability — means no computable target.
    /// </summary>
    private static bool TryComputeDrainTargetAck(TcpRedirectAssociation association, ITcpRelay relay, out uint targetAck)
    {
        targetAck = 0;
        if (!association.HasOriginalSynTemplate) return false;
        if (association.ServerInitialSeq is not { } serverInitialSeq) return false;
        if (relay is not ITcpRelayEndInfo endInfo) return false;
        var delivered = endInfo.ServerStreamBytes;
        if (delivered < 0) return false;
        targetAck = unchecked((uint)((long)serverInitialSeq + 2 + delivered));
        return true;
    }

    private async ValueTask InjectAbnormalEndResetAsync(TcpRedirectSession session)
    {
        try
        {
            await clientReset.TryInjectClientResetAsync(session.Association, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TcpRedirectLog.TcpRedirectRelayEndResetFailed(logger, exception);
        }
    }

    private static string EndKindName(RelayEndKind endKind) => endKind switch
    {
        RelayEndKind.CleanEnded => "cleanEnded",
        RelayEndKind.Stalled => "stalled",
        _ => "faulted",
    };
}
