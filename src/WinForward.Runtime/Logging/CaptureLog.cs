using Microsoft.Extensions.Logging;
using WinForward.Core;
using WinForward.Runtime.TcpRedirect;

namespace WinForward.Runtime.Logging;

internal static partial class CaptureLog
{
    [LoggerMessage(Level = LogLevel.Error, EventName = "adapter.slot-exhausted", Message = "Capture slot table is exhausted at {Slots} slot(s); adapter {Name} ({Adapter}) was refused.")]
    public static partial void AdapterSlotExhausted(ILogger logger, string adapter, string name, int slots);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "capture.packetEvent.unavailable", Message = "Packet-arrival registration failed for adapter {Name} ({Adapter}) with native error {NativeError}; its pump falls back to sleep polling.")]
    public static partial void CapturePacketEventUnavailable(ILogger logger, string adapter, string name, int nativeError);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "adapter.readShape.mismatch", Message = "Read-shape mismatch on adapter {Name} ({Adapter}): the driver queued {Queued} of the {Requested} requested packet(s) (native error {NativeError}).")]
    public static partial void AdapterReadShapeMismatch(ILogger logger, string adapter, string name, int requested, uint queued, int nativeError);

    [LoggerMessage(Level = LogLevel.Error, EventName = "adapter.degraded", Message = "Adapter {Name} ({Adapter}) degraded with native error {NativeError}; its capture pump stopped and its mode is restored.")]
    public static partial void AdapterDegraded(ILogger logger, string adapter, string name, int nativeError);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "packet.captured", Message = "Captured packet {Packet} ({Bytes} byte(s)) on adapter {AdapterName} ({Adapter}, handle {AdapterHandle}) in the {Direction} direction with flags {Flags}.")]
    public static partial void PacketCaptured(ILogger logger, long packet, int bytes, string adapter, string adapterName, nint adapterHandle, string direction, uint flags);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "packet.failed", Message = "Packet {Packet} ({Bytes} byte(s)) failed: {Reason}.")]
    public static partial void PacketFailed(ILogger logger, long packet, int bytes, string reason);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "adapter.addressQuery.failed", Message = "The unicast-address query failed and adapter address fingerprints degrade to empty for this run: {Error}.")]
    public static partial void AdapterAddressQueryFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Detail}")]
    public static partial void ScopeResolutionError(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "No MSTCP-bound adapters are available to capture.")]
    public static partial void NoCaptureAdapters(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Detail}")]
    public static partial void ScopeResolutionWarning(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Every capture-scope adapter disappeared; interception is paused until an adapter returns.")]
    public static partial void CaptureScopeEmpty(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, EventName = "capture.scope.resolved", Message = "Capture scope resolved with {Adapters} adapter(s): {Scope}.")]
    public static partial void CaptureScopeResolved(ILogger logger, int adapters, string scope);

    [LoggerMessage(Level = LogLevel.Information, EventName = "capture.generation.started", Message = "Capture generation starting with {Adapters} adapter(s): {Scope}.")]
    public static partial void CaptureGenerationStarted(ILogger logger, int adapters, string scope);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "runner.forcedRefresh", Message = "Forced capture refresh from counter {Reason}: {Consecutive} consecutive trigger(s), cooldown {CooldownSeconds}s, degraded {Degraded}, counter windows {Windows}.")]
    public static partial void RunnerForcedRefresh(ILogger logger, string reason, int consecutive, long cooldownSeconds, string? degraded, string? windows);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "generation.startup-fault", Message = "Capture generation startup fault {NativeError} on attempt {Attempt} was absorbed; a forced rebuild is armed.")]
    public static partial void GenerationStartupFaultAbsorbed(ILogger logger, int nativeError, string attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "The capture generation ended; stopping the run.")]
    public static partial void CaptureGenerationEnded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Durable-layer disposal failed during shutdown: {Error}.")]
    public static partial void DurableDisposalFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, EventName = "adapter.refresh", Message = "Capture adapter scope refresh: added {Added}, removed {Removed}, changed {Changed}, degraded {Degraded}, noop {Noop}, forced {Forced}.")]
    public static partial void AdapterRefresh(ILogger logger, string? added, string? removed, string? changed, string? degraded, string? noop, string? forced);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "packet.reinjected", Message = "Reinjected captured {Protocol} packet {Packet} (flow {Flow}) toward {Target}.")]
    public static partial void PacketReinjected(ILogger logger, long? packet, long? flow, TransportProtocol protocol, string target);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "packet.dropped", Message = "Dropped captured {Protocol} packet {Packet} (flow {Flow}): {Reason}.")]
    public static partial void PacketDropped(ILogger logger, long? packet, long? flow, TransportProtocol protocol, string reason);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "tcp.packet.handled", Message = "Handled captured {Protocol} packet {Packet} (flow {Flow}) with outcome {Outcome}.")]
    public static partial void TcpPacketHandled(ILogger logger, long? packet, long? flow, TransportProtocol protocol, TcpRedirectOutcome outcome);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "udp.packet.rejected", Message = "Rejected captured {Protocol} packet {Packet} (flow {Flow}): {Reason}.")]
    public static partial void UdpPacketRejected(ILogger logger, long? packet, long? flow, TransportProtocol protocol, string reason);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "reinject.pass-failed", Message = "Pass reinjection failed with native error {NativeError} ({Error}) on adapter {Adapter} (handle {AdapterHandle}) toward {Direction}: {Frames} frame(s) from {Source} -> {Destination} ({Protocol}).")]
    public static partial void ReinjectPassFailed(ILogger logger, int? nativeError, string error, string? adapter, long adapterHandle, string direction, int frames, Endpoint? source, Endpoint? destination, TransportProtocol? protocol);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pass batching is degraded to immediate single sends because more than {Lanes} concurrent (adapter, direction) lanes are active.")]
    public static partial void PassBatchingDegraded(ILogger logger, int lanes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A pass lane retired for adapter {Adapter} still held {Frames} frame(s); the frames are dropped and their rented buffers returned because the iteration-end flush contract was breached.")]
    public static partial void PassLaneRetiredWithPendingFrames(ILogger logger, string adapter, int frames);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP proxy handling failed: {Error}: {Reason}.")]
    public static partial void TcpProxyHandlingFailed(ILogger logger, string error, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDP proxy handling failed: {Error}: {Reason}.")]
    public static partial void UdpProxyHandlingFailed(ILogger logger, string error, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A proxy-selected flow was blocked because proxy relay support is not initialized in this build.")]
    public static partial void ProxyRelayNotInitialized(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A proxy-selected flow was blocked: {Reason}.")]
    public static partial void ProxyFlowBlocked(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, EventName = "runner.forcedRefresh.degraded", Message = "Interception health degraded after {Consecutive} consecutive forced-refresh trigger(s); trigger spacing drops to {SpacingSeconds}s.")]
    public static partial void RunnerForcedRefreshDegraded(ILogger logger, int consecutive, long spacingSeconds);

    [LoggerMessage(Level = LogLevel.Error, EventName = "generation.startup-fault.exhausted", Message = "Capture generation startup fault {NativeError} on attempt {Attempt} exhausted the recovery budget; the run fails closed.")]
    public static partial void GenerationStartupFaultExhausted(ILogger logger, int nativeError, string attempt);
}
