using System.ComponentModel;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The client-visible failure surface of the TCP redirect data path. When an upstream relay
/// cannot be established or a rewritten frame cannot be injected, this module surfaces the
/// failure to the client as a protocol-correct RST|ACK from the original server endpoint instead
/// of leaving its established connection hanging. The reset is crafted from the recorded SYN
/// template and the tracked next-expected sequences (degrading to the initial sequence numbers
/// when no data was observed), so it stays valid even though the redirect-table alias is torn
/// down right after. Degrades to plain teardown when either initial sequence number was never
/// observed.
/// </summary>
internal sealed class ClientResetInjector
{
    private readonly ITcpRedirectInjector _injector;
    private readonly IRuntimeLogger _logger;
    private readonly Func<TcpRedirectSession, ValueTask> _tearDownSession;
    private readonly Func<TcpRedirectAssociation, ValueTask> _failAssociation;

    public ClientResetInjector(ITcpRedirectInjector injector, IRuntimeLogger logger, Func<TcpRedirectSession, ValueTask> tearDownSession, Func<TcpRedirectAssociation, ValueTask> failAssociation)
    {
        _injector = injector;
        _logger = logger;
        _tearDownSession = tearDownSession;
        _failAssociation = failAssociation;
    }

    public ValueTask TryInjectClientResetAsync(TcpRedirectSession session)
        => TryInjectClientResetAsync(session.Association, session.Token);

    /// <summary>The association-level core, usable from teardown paths that hold no session
    /// (e.g. an injection failure on the data path).</summary>
    public async ValueTask TryInjectClientResetAsync(TcpRedirectAssociation association, CancellationToken cancellationToken)
    {
        if (association.OriginalSynFrameCopy is not { } synTemplate || association.ClientInitialSeq is not uint clientInitialSeq || association.ServerInitialSeq is not uint serverInitialSeq) return;
        // The tracked advancement covers data the client already sent, so the reset's ack stays in
        // its window instead of being dropped as out-of-window (RFC 5961) after a slow relay setup.
        var serverSequenceNext = association.ServerNextSeq ?? serverInitialSeq + 1;
        var clientSequenceNext = association.ClientNextSeq ?? clientInitialSeq + 1;
        var reset = TcpResetBuilder.BuildReset(synTemplate, association.OriginalDestination.Address, association.OriginalDestination.Port,
            association.OriginalKey.Local.Address, association.OriginalKey.Local.Port, serverSequenceNext, clientSequenceNext);
        if (reset is null) return;
        try
        {
            await _injector.InjectAsync(reset, association.OriginalKey.Origin != FlowOriginKind.Forwarded, association.OriginAdapterHandle, cancellationToken).ConfigureAwait(false);
            if (_logger.IsEnabled(RuntimeLogLevel.Debug))
            {
                _logger.Event(RuntimeLogLevel.Debug, "tcp.redirect.clientReset",
                    new("tcpAssociation", association.Generation), new("source", association.OriginalKey.Local),
                    new("destination", association.OriginalKey.Remote), new("outcome", "injected"));
            }
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
    public async ValueTask HandleRelaySetupFailureAsync(TcpRedirectSession session, ITcpAcceptedConnection accepted)
    {
        _logger.Warn("TCP redirect relay setup failed; resetting the client connection and releasing the flow alias.");
        await accepted.DisposeAsync().ConfigureAwait(false);
        await TryInjectClientResetAsync(session).ConfigureAwait(false);
        await _tearDownSession(session).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a data-path injection failure explicit and observable instead of a silent passive
    /// teardown: warns with <c>reason=injectionFailure</c> plus the native error, target adapter
    /// handle, and flow key, then best-effort resets the client-visible connection (when both
    /// sequence trackers are known) and fails the association through the single tombstone write
    /// point. The canonical trigger is a forwarded flow's <c>OriginAdapterHandle</c> going stale
    /// after a Hyper-V vSwitch/adapter rebuild.
    /// </summary>
    public async ValueTask HandleInjectionFailureAsync(TcpRedirectAssociation association, nint adapterHandle, bool towardMstcp, Exception exception)
    {
        if (_logger.IsEnabled(RuntimeLogLevel.Warn))
        {
            _logger.Event(RuntimeLogLevel.Warn, "tcp.redirect.failed",
                new("reason", "injectionFailure"),
                new("nativeError", (exception as Win32Exception)?.NativeErrorCode),
                new("error", exception.GetType().Name),
                new("adapterHandle", (long)adapterHandle),
                new("towardMstcp", towardMstcp),
                new("source", association.OriginalKey.Local),
                new("destination", association.OriginalKey.Remote));
        }
        await TryInjectClientResetAsync(association, CancellationToken.None).ConfigureAwait(false);
        await _failAssociation(association).ConfigureAwait(false);
    }
}
