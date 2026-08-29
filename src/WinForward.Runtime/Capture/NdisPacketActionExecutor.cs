using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Runtime.Capture;

/// <summary>
/// Executes pass/block/proxy packet dispositions. A pass copies the captured frame into a pooled
/// native buffer and reinjects it exactly once in its captured direction through the <see cref="IPacketReinjector"/>;
/// a block consumes the frame without reinjection. A proxy decision routes TCP packets through the
/// <see cref="TcpProxyCoordinator"/> and UDP datagrams through the <see cref="UdpProxyCoordinator"/> when
/// one is configured; if no matching coordinator is provided the flow fails closed with a rate-limited
/// structured log.
/// </summary>
public sealed class NdisPacketActionExecutor : IPacketActionExecutor
{
    private static readonly TimeSpan ProxyUnavailableLogInterval = TimeSpan.FromSeconds(5);

    private readonly IPacketReinjector _reinjector;
    private readonly TcpProxyCoordinator? _tcpProxy;
    private readonly UdpProxyCoordinator? _udpProxy;
    private readonly IRuntimeLogger _logger;
    private readonly NdisPacketBufferPool _bufferPool;
    private long _lastProxyUnavailableLogTicks;
    private long _lastUdpFailureLogTicks;

    public NdisPacketActionExecutor(IPacketReinjector reinjector, IRuntimeLogger? logger = null, TcpProxyCoordinator? tcpProxy = null, UdpProxyCoordinator? udpProxy = null, NdisPacketBufferPool? bufferPool = null)
    {
        ArgumentNullException.ThrowIfNull(reinjector);
        _reinjector = reinjector;
        _tcpProxy = tcpProxy;
        _udpProxy = udpProxy;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _bufferPool = bufferPool ?? NdisPacketBufferPool.Shared;
    }

    public ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (packet.Lease is null) throw new ArgumentNullException(nameof(packet));
        var metadata = packet.Metadata;
        if (packet.NativeFrame.Buffer is { } captureBuffer && !packet.Lease.IsMaterialized)
        {
            // Unmodified frame still sitting in its capture buffer: reinject it in place (only the
            // enumeration handle is retargeted; direction, length, flags, and payload stay as
            // captured). The synchronous send consumes the frame before the pump can reuse the
            // batch slot, and no managed copy ever happens.
            captureBuffer.PrepareForReinjection(metadata.AdapterHandle);
            if (metadata.IsOnSend) _reinjector.SendToAdapter(metadata.AdapterHandle, captureBuffer);
            else _reinjector.SendToMstcp(metadata.AdapterHandle, captureBuffer);
        }
        else
        {
            using var buffer = _bufferPool.Rent();
            buffer.SetFrame(packet.Lease.Frame.Span, metadata.DeviceFlags, metadata.AdapterHandle, metadata.Flags);
            if (metadata.IsOnSend) _reinjector.SendToAdapter(metadata.AdapterHandle, buffer);
            else _reinjector.SendToMstcp(metadata.AdapterHandle, buffer);
        }
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("packet.reinjected", packet, new RuntimeLogField("target", metadata.IsOnSend ? "adapter" : "mstcp"));
        return ValueTask.CompletedTask;
    }

    public ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        // Consumed: the lease is already completed by the dispatcher; no reinjection occurs.
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("packet.dropped", packet, new RuntimeLogField("reason", "policy"));
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        if (_udpProxy is { } udpProxy && packet.Context.Key.Protocol == TransportProtocol.Udp)
        {
            await HandleUdpProxyAsync(udpProxy, packet, server, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_tcpProxy is null)
        {
            LogProxyNotInitialized();
            return;
        }

        try
        {
            var outcome = await _tcpProxy.HandlePacketAsync(packet, server, cancellationToken).ConfigureAwait(false);
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("tcp.packet.handled", packet, new RuntimeLogField("outcome", outcome));
            if (outcome == TcpRedirectOutcome.Dropped)
            {
                // TIME_WAIT-grace tombstone hit: a straggler of a redirect torn down moments ago
                // (final ACK, retransmitted FIN/ACK). It is consumed silently — reinjecting toward
                // the original server would bounce an RST back at the client's finished connection,
                // and Blocked would emit a misleading proxy-unavailable warning. The lease is
                // already completed by the dispatcher. The trace follows the packet.dropped family
                // (BlockAsync logs reason=policy); packet.completed belongs to the dispatcher, which
                // emits it exactly once per packet.
                if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("packet.dropped", packet, new RuntimeLogField("reason", "grace"));
            }
            else if (outcome == TcpRedirectOutcome.NotRelevant)
            {
                // The flow is proxy-decided but this packet was never the coordinator's to handle
                // (typically a connection established before capture started, so its SYN was never
                // observed). It cannot join a relay retroactively; pass it so the pre-existing
                // connection stays alive instead of hanging until timeout.
                await PassAsync(packet, cancellationToken).ConfigureAwait(false);
            }
            else if (outcome == TcpRedirectOutcome.Blocked)
            {
                LogProxyBlocked("redirect");
            }
            // Injected: the coordinator rewrote and reinjected the frame itself; the lease is
            // consumed.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"TCP proxy handling failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Hands a UDP datagram on a proxy-decided UDP flow to the SOCKS5 UDP relay coordinator. The
    /// original datagram is consumed (the relay transport owns forwarding, including any buffered
    /// setup traffic); it is never reinjected. A parse failure or an unsent datagram fails closed
    /// without a pass downgrade.
    /// </summary>
    private async ValueTask HandleUdpProxyAsync(UdpProxyCoordinator udpProxy, CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        if (!IPUdpPacket.TryParse(packet.Lease.Frame, out var udpView))
        {
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("udp.packet.rejected", packet, new RuntimeLogField("reason", "parse"));
            LogProxyBlocked("parse");
            return;
        }

        // The Ethernet source MAC is the client's address (a VM NIC for forwarded flows). The
        // coordinator records it so forwarded UDP responses can be rebuilt toward the client
        // instead of this host's own NIC MAC.
        var clientMac = packet.Lease.Frame.Slice(6, 6);

        try
        {
            var sent = await udpProxy.TrySendAsync(packet.Context.Key, server, udpView.Payload, cancellationToken, packet.PacketSequence, packet.FlowGeneration, clientMac).ConfigureAwait(false);
            if (!sent)
            {
                if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("udp.packet.rejected", packet, new RuntimeLogField("reason", "send"));
                LogProxyBlocked("send");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogUdpFailureRateLimited(ex);
        }
    }

    private void LogPacket(string eventName, CapturedFlowPacket packet, params RuntimeLogField[] additional)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Trace)) return;
        var fields = new RuntimeLogField[additional.Length + 4];
        fields[0] = new("packet", packet.PacketSequence == 0 ? null : packet.PacketSequence);
        fields[1] = new("flow", packet.FlowGeneration == 0 ? null : packet.FlowGeneration);
        fields[2] = new("protocol", packet.Context.Key.Protocol);
        fields[3] = new("stage", eventName);
        additional.CopyTo(fields, 4);
        _logger.Event(RuntimeLogLevel.Trace, eventName, fields);
    }

    /// <summary>
    /// The genuinely-uninitialized case: no proxy coordinator was wired, so no relay could ever
    /// exist. Every other blocked path reports its own reason through <see cref="LogProxyBlocked"/>
    /// instead — claiming "not initialized" for a capacity or parse rejection misleads diagnosis (S6c).
    /// </summary>
    private void LogProxyNotInitialized()
    {
        if (!ShouldWarn(ref _lastProxyUnavailableLogTicks)) return;
        _logger.Warn("A proxy-selected flow was blocked because proxy relay support is not initialized in this build.");
    }

    /// <summary>
    /// Reports why a proxy-selected flow was blocked (S6c). The reason values mirror the
    /// executor-level trace vocabulary: <c>redirect</c> (the TCP redirect coordinator rejected the
    /// flow — its per-event <c>tcp.redirect.rejected</c> trace carries the specific sub-reason),
    /// <c>parse</c>, and <c>send</c> match the <c>udp.packet.rejected</c> trace reasons.
    /// </summary>
    private void LogProxyBlocked(string reason)
    {
        if (!ShouldWarn(ref _lastProxyUnavailableLogTicks)) return;
        _logger.Warn($"A proxy-selected flow was blocked: reason={reason}.");
    }

    private void LogUdpFailureRateLimited(Exception exception)
    {
        if (!ShouldWarn(ref _lastUdpFailureLogTicks)) return;
        _logger.Warn($"UDP proxy handling failed: {exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>
    /// True for the single caller allowed to warn in the current window (CAS-guarded). Checked
    /// before message formatting so a suppressed call never allocates the message string.
    /// </summary>
    private static bool ShouldWarn(ref long lastLogTicks)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref lastLogTicks);
        return now - last >= ProxyUnavailableLogInterval.Ticks && Interlocked.CompareExchange(ref lastLogTicks, now, last) == last;
    }
}
