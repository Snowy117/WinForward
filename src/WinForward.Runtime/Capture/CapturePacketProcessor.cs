using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Windows;

namespace WinForward.Runtime.Capture;

/// <summary>
/// Bridges a captured NDISAPI packet into the flow dispatcher. Parsing and classification run
/// directly on the pump-owned native frame: the lease carries the native buffer as its frame
/// source and materializes a pooled managed copy only when a consumer actually needs a stable
/// view (proxy paths that rewrite or relay). Frames that cannot be classified into a TCP/UDP
/// flow (non-IP, fragmented, malformed, or a non-TCP/UDP protocol) take the non-flow path.
/// The lease is always completed exactly once: on any unexpected exception it is disposed
/// (fail-closed to block) and the exception is propagated so the runtime shuts down and restores
/// adapter modes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CapturePacketProcessor
{
    private readonly FlowDispatcher _dispatcher;
    private readonly IRuntimeLogger _logger;
    private readonly Action<nint>? _onBatchCompleted;
    private long _nextPacketSequence;

    public CapturePacketProcessor(FlowDispatcher dispatcher, IRuntimeLogger? logger = null, Action<nint>? onBatchCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _onBatchCompleted = onBatchCompleted;
    }

    /// <summary>
    /// Optional batch-completed pass-through handed to each capture pump (invoked once per pump
    /// iteration and once at loop exit, carrying the pump adapter's enumeration handle). The
    /// runtime composition wires it to the executor's pending-pass flush so accumulated pass
    /// reinjections leave as batched IOCTLs; this holder keeps the executor out of the pump layer.
    /// </summary>
    public Action<nint>? OnBatchCompleted => _onBatchCompleted;

    public async ValueTask ProcessAsync(NdisCapturedPacket packet, WindowsAdapter adapter, CancellationToken cancellationToken)
    {
        // The native frame stays owned by the pump for the whole dispatch (the pump awaits this
        // handler before touching its batch again), so parsing can run on the native span and the
        // lease copies lazily only for consumers that keep the frame beyond the synchronous pass
        // path. The materialized pooled array returns after the dispatch completes: the dispatcher
        // completes the lease BEFORE the executor runs, so a completion-triggered return would
        // expose a reused array to those readers.
        var frameSpan = packet.Buffer.GetFrame();
        var lease = PacketLease.TakeNative(packet.Buffer);
        var metadata = new PacketCaptureMetadata(packet.DeviceFlags, packet.AdapterHandle, packet.Flags);
        var nativeFrame = new NativeFrameHandle(packet.Buffer);
        var frameLength = frameSpan.Length;
        var isOnSend = (packet.DeviceFlags & NdisApiAbi.PacketFlagOnSend) != 0;
        var sequence = Interlocked.Increment(ref _nextPacketSequence);
        if (_logger.IsEnabled(RuntimeLogLevel.Trace))
        {
            _logger.Event(RuntimeLogLevel.Trace, "packet.captured",
                new("packet", sequence), new("bytes", frameLength), new("adapter", adapter.StableId),
                new("adapterName", adapter.FriendlyName), new("adapterHandle", adapter.RuntimeHandle),
                new("direction", isOnSend ? "send" : "receive"), new("flags", packet.Flags));
        }
        try
        {
            if (!IPTcpUdpPacket.TryParse(frameSpan, out var view))
            {
                var nonFlowContext = PacketFlowClassifier.ClassifyNonFlow(adapter, isOnSend);
                await _dispatcher.DispatchNonFlowAsync(new CapturedFlowPacket(lease, nonFlowContext, metadata, sequence, NativeFrame: nativeFrame), cancellationToken).ConfigureAwait(false);
                return;
            }

            var flowContext = PacketFlowClassifier.ClassifyFlow(view, adapter, isOnSend);
            await _dispatcher.DispatchAsync(new CapturedFlowPacket(lease, flowContext, metadata, sequence, NativeFrame: nativeFrame), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(RuntimeLogLevel.Trace))
            {
                _logger.Event(RuntimeLogLevel.Trace, "packet.failed",
                    new("packet", sequence), new("bytes", frameLength), new("reason", "canceled"));
            }
            lease.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            if (_logger.IsEnabled(RuntimeLogLevel.Trace))
            {
                _logger.Event(RuntimeLogLevel.Trace, "packet.failed",
                    new("packet", sequence), new("bytes", frameLength),
                    new("reason", exception.GetType().Name));
            }
            lease.Dispose();
            throw;
        }
        finally
        {
            lease.Release();
        }
    }
}
