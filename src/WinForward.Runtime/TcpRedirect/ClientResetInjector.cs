using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime.Logging;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The client-visible close surface of the TCP redirect data path. When an upstream relay cannot
/// be established or a rewritten frame cannot be injected, this module surfaces the failure to the
/// client as a protocol-correct RST|ACK from the original server endpoint instead of leaving its
/// established connection hanging; a relay that ends cleanly uses the same path for the FIN|ACK
/// that closes the connection. Both are crafted from the recorded SYN template and the tracked
/// next-expected sequences (degrading to the initial sequence numbers when no data was observed),
/// so they stay valid even though the redirect-table alias is torn down right after. Degrades to
/// plain teardown when either initial sequence number was never observed.
/// </summary>
internal sealed class ClientResetInjector(ITcpRedirectInjector injector, ILogger logger, Func<TcpRedirectSession, ValueTask> tearDownSession, Func<TcpRedirectAssociation, ValueTask> failAssociation, int? capacity = null, IInterceptionHealthSignal? healthSignal = null, NdisPacketBufferPool? bufferPool = null, TimeProvider? timeProvider = null)
{
    /// <summary>
    /// The per-tuple cooldown window for capacity-rejection resets: at most one RST|ACK per
    /// 4-tuple per second (the UDP setup-cooldown precedent) bounds reflection amplification
    /// from spoofed sources while still failing well-behaved clients fast.
    /// </summary>
    private static readonly TimeSpan s_capacityResetCooldownWindow = TimeSpan.FromSeconds(1);
    private readonly IInterceptionHealthSignal _healthSignal = healthSignal ?? InterceptionHealthMonitor.Noop;
    private readonly NdisPacketBufferPool _bufferPool = bufferPool ?? NdisPacketBufferPool.Shared;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>The capacity-reset cooldown index; surfaced so tests can advance the window.</summary>
    internal TcpResetCooldownTable CapacityResets { get; } = new(capacity ?? 16_384);

    private ValueTask TryInjectClientResetAsync(TcpRedirectSession session) => TryInjectClientResetAsync(session.Association, session.Token);

    /// <summary>The association-level core, usable from teardown paths that hold no session
    /// (e.g. an injection failure on the data path).</summary>
    public ValueTask TryInjectClientResetAsync(TcpRedirectAssociation association, CancellationToken cancellationToken)
        => TryInjectClientCloseCoreAsync(association, reset: true, serverStreamBytes: null, cancellationToken);

    public ValueTask TryInjectClientCloseAsync(TcpRedirectAssociation association, long? serverStreamBytes, CancellationToken cancellationToken)
        => TryInjectClientCloseCoreAsync(association, reset: false, serverStreamBytes, cancellationToken);

    private ValueTask TryInjectClientCloseCoreAsync(TcpRedirectAssociation association, bool reset, long? serverStreamBytes, CancellationToken cancellationToken)
    {
        if (!association.HasOriginalSynTemplate || association.ClientInitialSeq is not { } clientInitialSeq || association.ServerInitialSeq is not { } serverInitialSeq) return ValueTask.CompletedTask;
        var synTemplate = association.OriginalSynTemplate;
        // The tracked advancement covers data the client already sent, so the close frame's ack stays
        // in its window instead of being dropped as out-of-window (RFC 5961) after a slow relay setup.
        // The delivered byte count wins for the sequence: the tracker can lag the capture pipeline and
        // can already have counted the client-facing socket's own FIN.
        var serverSequenceNext = serverStreamBytes is { } delivered
            ? (uint)(serverInitialSeq + 1 + delivered)
            : association.ServerNextSeq ?? serverInitialSeq + 1;
        var clientSequenceNext = association.ClientNextSeq ?? clientInitialSeq + 1;
        var towardMstcp = association.OriginalKey.Origin != FlowOriginKind.Forwarded;
        try
        {
            using var buffer = _bufferPool.Rent();
            var built = reset
                ? TcpResetBuilder.TryBuildReset(synTemplate, association.OriginalDestination.Address, association.OriginalDestination.Port,
                    association.OriginalKey.Local.Address, association.OriginalKey.Local.Port, serverSequenceNext, clientSequenceNext, buffer.GetFrameStorage(), out var written)
                : TcpResetBuilder.TryBuildFin(synTemplate, association.OriginalDestination.Address, association.OriginalDestination.Port,
                    association.OriginalKey.Local.Address, association.OriginalKey.Local.Port, serverSequenceNext, clientSequenceNext, buffer.GetFrameStorage(), out written);
            if (!built)
            {
                return ValueTask.CompletedTask;
            }

            buffer.CompleteFrame(written, towardMstcp ? NdisApiAbi.PacketFlagOnReceive : NdisApiAbi.PacketFlagOnSend, association.OriginAdapterHandle);
            injector.Inject(buffer, towardMstcp, association.OriginAdapterHandle, cancellationToken);
            if (reset)
            {
                TcpRedirectLog.TcpRedirectClientReset(logger, association.Generation, association.OriginalKey.Local, association.OriginalKey.Remote, "injected");
            }
            else
            {
                TcpRedirectLog.TcpRedirectClientClose(logger, association.Generation, association.OriginalKey.Local, association.OriginalKey.Remote, "injected");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown or session teardown cancelled the best-effort close.
        }
        catch (Exception exception)
        {
            var error = exception.GetType().Name;
            if (reset)
            {
                TcpRedirectLog.TcpRedirectClientResetInjectionFailed(logger, error);
            }
            else
            {
                TcpRedirectLog.TcpRedirectClientCloseInjectionFailed(logger, error);
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Surfaces a capacity-gate rejection to the client as an immediate RST|ACK from the server
    /// tuple it dialed (S4). The client is still in SYN_SENT, so <c>ack = ISN + 1</c> aborts the
    /// connect with ECONNREFUSED instead of a 20-60s retransmission timeout. Guarded by the
    /// per-tuple cooldown so retransmitted SYNs inside the window stay silently dropped; host
    /// shape injects toward MSTCP, forwarded shape toward the origin adapter (the capture
    /// handle), matching the redirect direction matrix. Never throws: a failed best-effort
    /// reset is warned and the Blocked rejection outcome stands unchanged.
    /// </summary>
    public ValueTask InjectCapacityRejectedResetAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        var key = packet.Context.Key;
        if (!CapacityResets.TryClaim(key, _timeProvider.GetUtcNow(), s_capacityResetCooldownWindow)) return ValueTask.CompletedTask;
        var towardMstcp = key.Origin != FlowOriginKind.Forwarded;
        try
        {
            using var buffer = _bufferPool.Rent();
            if (!TcpResetBuilder.TryBuildResetFromSyn(packet.InspectionSpan, key.Remote.Address, key.Remote.Port, key.Local.Address, key.Local.Port, buffer.GetFrameStorage(), out var written)) return ValueTask.CompletedTask;
            buffer.CompleteFrame(written, towardMstcp ? NdisApiAbi.PacketFlagOnReceive : NdisApiAbi.PacketFlagOnSend, packet.Metadata.AdapterHandle);
            injector.Inject(buffer, towardMstcp, packet.Metadata.AdapterHandle, cancellationToken);
            TcpRedirectLog.TcpRedirectCapacityReset(logger, key.Local, key.Remote, "injected");
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancelled the best-effort reset; the rejection itself already stands.
        }
        catch (Exception exception)
        {
            var error = exception.GetType().Name;
            TcpRedirectLog.TcpRedirectCapacityResetInjectionFailed(logger, error);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Releases a flow whose association was hit by an IP fragment it can never rewrite or relay
    /// (S1). The teardown is client-visible whenever the tracked sequences allow an in-window
    /// RST|ACK; otherwise it degrades to a warned silent teardown (the client then observes the
    /// connection failing on its own retransmission timeout). The removal still funnels through
    /// the single tombstone write point.
    /// </summary>
    public async ValueTask HandleFragmentTeardownAsync(TcpRedirectAssociation association)
    {
        if (!association.HasOriginalSynTemplate || association.ClientInitialSeq is null || association.ServerInitialSeq is null)
        {
            TcpRedirectLog.TcpRedirectFragmentTeardownWithoutSequences(logger, association.OriginalKey.Local, association.OriginalKey.Remote);
        }
        await TryInjectClientResetAsync(association, CancellationToken.None).ConfigureAwait(false);
        await failAssociation(association).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases a flow whose upstream relay could not be established: closes the accepted socket,
    /// best-effort resets the client-visible connection, then tears the session down. The failure
    /// itself is surfaced as a structured warn (<c>tcp.redirect.relaySetupFailed</c>) carrying the
    /// error type, the socket/Win32 error code when available, the SOCKS5 upstream endpoint, the
    /// connect-attempt budget, and the original flow key. Teardown/alias-release behavior is
    /// unchanged.
    /// </summary>
    public async ValueTask HandleRelaySetupFailureAsync(TcpRedirectSession session, ITcpAcceptedConnection accepted, Exception exception)
    {
        RuntimeCounters.Shared.Increment(RuntimeCounters.RelaySetupFailed);
        _healthSignal.ReportFailure(RuntimeCounters.RelaySetupFailed);
        var association = session.Association;
        // SocketException derives from Win32Exception, so the Win32 probe covers both shapes;
        // the socket error name only exists on SocketException. An IPv6-literal host gets the
        // bracket convention the log formatter applies to Endpoint values.
#pragma warning disable CA1416 // Reading the const inlines a literal from the windows-gated relay factory; the value (the dial budget this event reports) is inert on every platform.
        const int connectAttempts = TcpProxyRelayFactory.RelayConnectMaxAttempts;
#pragma warning restore CA1416
        var upstream = session.Server.Host.Contains(':', StringComparison.Ordinal)
            ? $"[{session.Server.Host}]:{session.Server.Port.ToString(CultureInfo.InvariantCulture)}"
            : $"{session.Server.Host}:{session.Server.Port.ToString(CultureInfo.InvariantCulture)}";
        var error = exception.GetType().Name;
        var socketError = (exception as SocketException)?.SocketErrorCode;
        var nativeError = (exception as Win32Exception)?.NativeErrorCode;
        TcpRedirectLog.TcpRedirectRelaySetupFailed(logger, error, socketError, nativeError, upstream, session.Server.Name, association.OriginalKey.Local, association.OriginalDestination, connectAttempts);
        await accepted.DisposeAsync().ConfigureAwait(false);
        await TryInjectClientResetAsync(session).ConfigureAwait(false);
        await tearDownSession(session).ConfigureAwait(false);
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
        var nativeError = (exception as Win32Exception)?.NativeErrorCode;
        var error = exception.GetType().Name;
        TcpRedirectLog.TcpRedirectFailed(logger, "injectionFailure", nativeError, error, adapterHandle, towardMstcp, association.OriginalKey.Local, association.OriginalKey.Remote);
        await TryInjectClientResetAsync(association, CancellationToken.None).ConfigureAwait(false);
        await failAssociation(association).ConfigureAwait(false);
    }
}
