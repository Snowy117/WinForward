using System.Runtime.Versioning;
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

    public CapturePacketProcessor(FlowDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public async ValueTask ProcessAsync(NdisCapturedPacket packet, WindowsAdapter adapter, CancellationToken cancellationToken)
    {
        var frame = packet.Buffer.GetFrame().ToArray();
        var lease = new PacketLease(frame);
        var metadata = new PacketCaptureMetadata(packet.DeviceFlags, packet.AdapterHandle);
        var isOnSend = (packet.DeviceFlags & NdisApiAbi.PacketFlagOnSend) != 0;
        try
        {
            if (!IpTcpUdpPacket.TryParse(frame, out var view))
            {
                var nonFlowContext = PacketFlowClassifier.ClassifyNonFlow(adapter, isOnSend);
                await _dispatcher.DispatchNonFlowAsync(new CapturedFlowPacket(lease, nonFlowContext, metadata), cancellationToken).ConfigureAwait(false);
                return;
            }

            var flowContext = PacketFlowClassifier.ClassifyFlow(view, adapter, isOnSend);
            await _dispatcher.DispatchAsync(new CapturedFlowPacket(lease, flowContext, metadata), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lease.Dispose();
            throw;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}