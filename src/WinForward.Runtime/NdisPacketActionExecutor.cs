using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;

namespace WinForward.Runtime;

/// <summary>
/// Executes pass/block/proxy packet dispositions. A pass copies the captured frame into a native
/// buffer and reinjects it exactly once in its captured direction through the <see cref="IPacketReinjector"/>;
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
    private long _lastProxyUnavailableLogTicks;

    public NdisPacketActionExecutor(IPacketReinjector reinjector, IRuntimeLogger? logger = null, TcpProxyCoordinator? tcpProxy = null, UdpProxyCoordinator? udpProxy = null)
    {
        ArgumentNullException.ThrowIfNull(reinjector);
        _reinjector = reinjector;
        _tcpProxy = tcpProxy;
        _udpProxy = udpProxy;
        _logger = logger ?? NullRuntimeLogger.Instance;
    }

    public ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var metadata = packet.Metadata;
        using var buffer = new NdisPacketBuffer();
        buffer.SetFrame(packet.Lease.Frame.Span, metadata.DeviceFlags, metadata.AdapterHandle);
        if (metadata.IsOnSend) _reinjector.SendToAdapter(metadata.AdapterHandle, buffer);
        else _reinjector.SendToMstcp(metadata.AdapterHandle, buffer);
        return ValueTask.CompletedTask;
    }

    public ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        // Consumed: the lease is already completed by the dispatcher; no reinjection occurs.
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        // UDP datagram on a proxy-decided UDP flow: hand the payload to the SOCKS5 UDP relay
        // coordinator. The original datagram is consumed (the relay transport owns forwarding,
        // including any buffered setup traffic); it is never reinjected. A parse failure or an
        // unsent datagram fails closed without a pass downgrade.
        if (_udpProxy is not null && packet.Context.Key.Protocol == TransportProtocol.Udp)
        {
            if (!IpUdpPacket.TryParse(packet.Lease.Frame.Span, out var udpView))
            {
                LogProxyUnavailable();
                return;
            }

            try
            {
                var sent = await _udpProxy.TrySendAsync(packet.Context.Key, server, udpView.Payload, cancellationToken).ConfigureAwait(false);
                if (!sent)
                {
                    LogProxyUnavailable();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn($"UDP proxy handling failed: {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }

        if (_tcpProxy is null)
        {
            LogProxyUnavailable();
            return;
        }

        try
        {
            var outcome = await _tcpProxy.HandlePacketAsync(packet, server, cancellationToken).ConfigureAwait(false);
            if (outcome == TcpRedirectOutcome.Blocked)
            {
                LogProxyUnavailable();
            }
            // Injected: the coordinator rewrote and reinjected the frame itself; the lease is
            // consumed. NotRelevant: mid-flow data on a flow with no active association is
            // passed through by normal policy handling.
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

    private void LogProxyUnavailable()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastProxyUnavailableLogTicks);
        if (now - last >= ProxyUnavailableLogInterval.Ticks && Interlocked.CompareExchange(ref _lastProxyUnavailableLogTicks, now, last) == last)
        {
            _logger.Warn("A proxy-selected flow was blocked because proxy relay support is not initialized in this build.");
        }
    }
}