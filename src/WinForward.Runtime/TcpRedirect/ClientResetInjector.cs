using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
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
    /// <summary>
    /// The per-tuple cooldown window for capacity-rejection resets: at most one RST|ACK per
    /// 4-tuple per second (the UDP setup-cooldown precedent) bounds reflection amplification
    /// from spoofed sources while still failing well-behaved clients fast.
    /// </summary>
    internal static readonly TimeSpan CapacityResetCooldownWindow = TimeSpan.FromSeconds(1);

    private readonly ITcpRedirectInjector _injector;
    private readonly IRuntimeLogger _logger;
    private readonly IInterceptionHealthSignal _healthSignal;
    private readonly Func<TcpRedirectSession, ValueTask> _tearDownSession;
    private readonly Func<TcpRedirectAssociation, ValueTask> _failAssociation;
    private readonly TcpResetCooldownTable _capacityResets;
    private readonly NdisPacketBufferPool _bufferPool;
    private readonly TimeProvider _timeProvider;

    public ClientResetInjector(ITcpRedirectInjector injector, IRuntimeLogger logger, Func<TcpRedirectSession, ValueTask> tearDownSession, Func<TcpRedirectAssociation, ValueTask> failAssociation, int? capacity = null, IInterceptionHealthSignal? healthSignal = null, NdisPacketBufferPool? bufferPool = null, TimeProvider? timeProvider = null)
    {
        _injector = injector;
        _logger = logger;
        _tearDownSession = tearDownSession;
        _failAssociation = failAssociation;
        _capacityResets = new TcpResetCooldownTable(capacity ?? 16_384);
        _healthSignal = healthSignal ?? InterceptionHealthMonitor.Noop;
        _bufferPool = bufferPool ?? NdisPacketBufferPool.Shared;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The capacity-reset cooldown index; surfaced so tests can advance the window.</summary>
    internal TcpResetCooldownTable CapacityResets => _capacityResets;

    public ValueTask TryInjectClientResetAsync(TcpRedirectSession session)
        => TryInjectClientResetAsync(session.Association, session.Token);

    /// <summary>The association-level core, usable from teardown paths that hold no session
    /// (e.g. an injection failure on the data path).</summary>
    public ValueTask TryInjectClientResetAsync(TcpRedirectAssociation association, CancellationToken cancellationToken)
    {
        if (!association.HasOriginalSynTemplate || association.ClientInitialSeq is not uint clientInitialSeq || association.ServerInitialSeq is not uint serverInitialSeq) return ValueTask.CompletedTask;
        var synTemplate = association.OriginalSynTemplate;
        // The tracked advancement covers data the client already sent, so the reset's ack stays in
        // its window instead of being dropped as out-of-window (RFC 5961) after a slow relay setup.
        var serverSequenceNext = association.ServerNextSeq ?? serverInitialSeq + 1;
        var clientSequenceNext = association.ClientNextSeq ?? clientInitialSeq + 1;
        var towardMstcp = association.OriginalKey.Origin != FlowOriginKind.Forwarded;
        try
        {
            using var buffer = _bufferPool.Rent();
            if (!TcpResetBuilder.TryBuildReset(synTemplate, association.OriginalDestination.Address, association.OriginalDestination.Port,
                association.OriginalKey.Local.Address, association.OriginalKey.Local.Port, serverSequenceNext, clientSequenceNext, buffer.GetFrameStorage(), out var written)) return ValueTask.CompletedTask;
            buffer.CompleteFrame(written, towardMstcp ? NdisApiAbi.PacketFlagOnReceive : NdisApiAbi.PacketFlagOnSend, association.OriginAdapterHandle);
            _injector.Inject(buffer, towardMstcp, association.OriginAdapterHandle, cancellationToken);
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
        if (!_capacityResets.TryClaim(key, _timeProvider.GetUtcNow(), CapacityResetCooldownWindow)) return ValueTask.CompletedTask;
        var towardMstcp = key.Origin != FlowOriginKind.Forwarded;
        try
        {
            using var buffer = _bufferPool.Rent();
            if (!TcpResetBuilder.TryBuildResetFromSyn(packet.InspectionSpan, key.Remote.Address, key.Remote.Port, key.Local.Address, key.Local.Port, buffer.GetFrameStorage(), out var written)) return ValueTask.CompletedTask;
            buffer.CompleteFrame(written, towardMstcp ? NdisApiAbi.PacketFlagOnReceive : NdisApiAbi.PacketFlagOnSend, packet.Metadata.AdapterHandle);
            _injector.Inject(buffer, towardMstcp, packet.Metadata.AdapterHandle, cancellationToken);
            if (_logger.IsEnabled(RuntimeLogLevel.Debug))
            {
                _logger.Event(RuntimeLogLevel.Debug, "tcp.redirect.capacityReset",
                    new("source", key.Local), new("destination", key.Remote), new("outcome", "injected"));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancelled the best-effort reset; the rejection itself already stands.
        }
        catch (Exception exception)
        {
            _logger.Warn($"TCP redirect capacity reset injection failed ({exception.GetType().Name}).");
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
        if (!association.HasOriginalSynTemplate || association.ClientInitialSeq is not uint || association.ServerInitialSeq is not uint)
        {
            _logger.Warn($"TCP redirect torn down by an IP fragment without observed sequences ({association.OriginalKey.Local} -> {association.OriginalKey.Remote}); no client reset is possible.");
        }
        await TryInjectClientResetAsync(association, CancellationToken.None).ConfigureAwait(false);
        await _failAssociation(association).ConfigureAwait(false);
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
        if (_logger.IsEnabled(RuntimeLogLevel.Warn))
        {
            var association = session.Association;
            // SocketException derives from Win32Exception, so the Win32 probe covers both shapes;
            // the socket error name only exists on SocketException. An IPv6-literal host gets the
            // bracket convention the log formatter applies to Endpoint values.
#pragma warning disable CA1416 // Reading the const inlines a literal from the windows-gated relay factory; the value (the dial budget this event reports) is inert on every platform.
            var connectAttempts = TcpProxyRelayFactory.RelayConnectMaxAttempts;
#pragma warning restore CA1416
            _logger.Event(RuntimeLogLevel.Warn, "tcp.redirect.relaySetupFailed",
                new("error", exception.GetType().Name),
                new("socketError", (exception as SocketException)?.SocketErrorCode),
                new("nativeError", (exception as Win32Exception)?.NativeErrorCode),
                new("upstream", session.Server.Host.Contains(':')
                    ? $"[{session.Server.Host}]:{session.Server.Port.ToString(CultureInfo.InvariantCulture)}"
                    : $"{session.Server.Host}:{session.Server.Port.ToString(CultureInfo.InvariantCulture)}"),
                new("proxy", session.Server.Name),
                new("source", association.OriginalKey.Local),
                new("destination", association.OriginalDestination),
                new("attempts", connectAttempts));
        }
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
