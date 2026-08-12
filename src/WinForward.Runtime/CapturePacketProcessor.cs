using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Windows;

namespace WinForward.Runtime;

/// <summary>
/// Bridges a captured NDISAPI packet into the flow dispatcher. The native frame is copied into a
/// managed <see cref="PacketLease"/> before the pump's native buffer is released, so no native memory
/// outlives its batch. Frames that cannot be classified into a TCP/UDP flow (non-IP, fragmented,
/// malformed, or a non-TCP/UDP protocol) take the non-flow path. The lease is always completed
/// exactly once: on any unexpected exception it is disposed (fail-closed to block) and the exception
/// is propagated so the runtime shuts down and restores adapter modes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CapturePacketProcessor
{
    private readonly FlowDispatcher _dispatcher;
    private readonly IRuntimeLogger _logger;
    private long _nextPacketSequence;

    public CapturePacketProcessor(FlowDispatcher dispatcher, IRuntimeLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _logger = logger ?? NullRuntimeLogger.Instance;
    }

    public async ValueTask ProcessAsync(NdisCapturedPacket packet, WindowsAdapter adapter, CancellationToken cancellationToken)
    {
        var frame = packet.Buffer.GetFrame().ToArray();
        var lease = new PacketLease(frame);
        var metadata = new PacketCaptureMetadata(packet.DeviceFlags, packet.AdapterHandle, packet.Flags);
        var isOnSend = (packet.DeviceFlags & NdisApiAbi.PacketFlagOnSend) != 0;
        var sequence = Interlocked.Increment(ref _nextPacketSequence);
        if (_logger.IsEnabled(RuntimeLogLevel.Trace))
        {
            _logger.Event(RuntimeLogLevel.Trace, "packet.captured",
                new("packet", sequence), new("bytes", frame.Length), new("adapter", adapter.StableId),
                new("adapterName", adapter.FriendlyName), new("adapterHandle", adapter.RuntimeHandle),
                new("direction", isOnSend ? "send" : "receive"), new("flags", packet.Flags));
        }
        try
        {
            if (!IpTcpUdpPacket.TryParse(frame, out var view))
            {
                var nonFlowContext = PacketFlowClassifier.ClassifyNonFlow(adapter, isOnSend);
                await _dispatcher.DispatchNonFlowAsync(new CapturedFlowPacket(lease, nonFlowContext, metadata, sequence), cancellationToken).ConfigureAwait(false);
                return;
            }

            var flowContext = PacketFlowClassifier.ClassifyFlow(view, adapter, isOnSend);
            await _dispatcher.DispatchAsync(new CapturedFlowPacket(lease, flowContext, metadata, sequence), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(RuntimeLogLevel.Trace))
            {
                _logger.Event(RuntimeLogLevel.Trace, "packet.failed",
                    new("packet", sequence), new("bytes", frame.Length), new("reason", "canceled"));
            }
            lease.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            if (_logger.IsEnabled(RuntimeLogLevel.Trace))
            {
                _logger.Event(RuntimeLogLevel.Trace, "packet.failed",
                    new("packet", sequence), new("bytes", frame.Length),
                    new("reason", exception.GetType().Name));
            }
            lease.Dispose();
            throw;
        }
    }
}
