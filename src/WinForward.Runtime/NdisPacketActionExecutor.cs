using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;

namespace WinForward.Runtime;

/// <summary>
/// Executes pass/block/proxy packet dispositions. A pass copies the captured frame into a native
/// buffer and reinjects it exactly once in its captured direction through the <see cref="IPacketReinjector"/>;
/// a block consumes the frame without reinjection. Because proxy relay is not implemented in this
/// build, a proxy decision fails closed: the frame is consumed and dropped (never silently passed)
/// with a rate-limited structured log. The reinjector is abstracted so the direction mapping is
/// unit-testable without NDISAPI hardware.
/// </summary>
public sealed class NdisPacketActionExecutor : IPacketActionExecutor
{
    private static readonly TimeSpan ProxyUnavailableLogInterval = TimeSpan.FromSeconds(5);

    private readonly IPacketReinjector _reinjector;
    private readonly IRuntimeLogger _logger;
    private long _lastProxyUnavailableLogTicks;

    public NdisPacketActionExecutor(IPacketReinjector reinjector, IRuntimeLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(reinjector);
        _reinjector = reinjector;
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

    public ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        LogProxyUnavailable();
        return ValueTask.CompletedTask;
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