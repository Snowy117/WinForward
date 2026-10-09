using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WinForward.Core;

namespace WinForward.Runtime.Logging;

internal static partial class TcpRedirectLog
{
    [LoggerMessage(Level = LogLevel.Debug, EventName = "tcp.redirect.created", Message = "TCP redirect session {TcpAssociation} for {Source} -> {Destination} (flow {Flow}) {Outcome} on listener {Translated} via proxy {Proxy}.")]
    public static partial void TcpRedirectCreated(ILogger logger, long? flow, long tcpAssociation, Endpoint source, Endpoint destination, Endpoint translated, string proxy, string outcome);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "tcp.redirect.closed", Message = "TCP redirect session {TcpAssociation} for {Source} -> {Destination} (flow {Flow}) {Outcome} on listener {Translated} via proxy {Proxy}.")]
    public static partial void TcpRedirectClosed(ILogger logger, long? flow, long tcpAssociation, Endpoint source, Endpoint destination, Endpoint translated, string proxy, string outcome);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "tcp.relay.started", Message = "TCP relay for session {TcpAssociation} ({Source} -> {Destination}, flow {Flow}) {Outcome} on listener {Translated} via proxy {Proxy}.")]
    public static partial void TcpRelayStarted(ILogger logger, long? flow, long tcpAssociation, Endpoint source, Endpoint destination, Endpoint translated, string proxy, string outcome);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "tcp.relay.ended", Message = "TCP relay for session {TcpAssociation} ({Source} -> {Destination}, flow {Flow}) {Outcome} on listener {Translated} via proxy {Proxy}.")]
    public static partial void TcpRelayEnded(ILogger logger, long? flow, long tcpAssociation, Endpoint source, Endpoint destination, Endpoint translated, string proxy, string outcome);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "tcp.redirect.drain", Message = "The client-visible close for {Source} -> {Destination}, association {TcpAssociation}, ended as {Outcome} after {ElapsedMs} ms of drain.")]
    public static partial void TcpRedirectDrain(ILogger logger, long tcpAssociation, Endpoint source, Endpoint destination, string outcome, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "tcp.redirect.reused", Message = "Reusing the redirect for {Source} -> {Destination} (packet {Packet}, flow {Flow}, association {TcpAssociation}): {Reason}.")]
    public static partial void TcpRedirectReused(ILogger logger, long? packet, long? flow, long? tcpAssociation, Endpoint source, Endpoint destination, string? reason);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "tcp.setup.cooldown", Message = "Refused the redirect setup for {Source} -> {Destination} fail-closed while the flow waits out its setup cooldown (packet {Packet}, flow {Flow}, association {TcpAssociation}): {Reason}.")]
    public static partial void TcpSetupCooldown(ILogger logger, long? packet, long? flow, long? tcpAssociation, Endpoint source, Endpoint destination, string? reason);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "tcp.redirect.rejected", Message = "Rejected the redirect for {Source} -> {Destination} (packet {Packet}, flow {Flow}, association {TcpAssociation}): {Reason}.")]
    public static partial void TcpRedirectRejected(ILogger logger, long? packet, long? flow, long? tcpAssociation, Endpoint source, Endpoint destination, string? reason);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "tcp.setup.pending.dropped", Message = "Dropped the pending SYN setup for {Source} -> {Destination} (packet {Packet}, flow {Flow}, association {TcpAssociation}): {Reason}.")]
    public static partial void TcpSetupPendingDropped(ILogger logger, long? packet, long? flow, long? tcpAssociation, Endpoint source, Endpoint destination, string? reason);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "tcp.redirect.fragment", Message = "Tearing down the redirect for {Source} -> {Destination} on an IP fragment it can never rewrite (packet {Packet}, flow {Flow}, association {TcpAssociation}): {Reason}.")]
    public static partial void TcpRedirectFragment(ILogger logger, long? packet, long? flow, long? tcpAssociation, Endpoint source, Endpoint destination, string? reason);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "tcp.redirect.injected", Message = "Injected the rewritten SYN for {Source} -> {Destination} (packet {Packet}, flow {Flow}, association {TcpAssociation}): {Reason}.")]
    public static partial void TcpRedirectInjected(ILogger logger, long? packet, long? flow, long? tcpAssociation, Endpoint source, Endpoint destination, string? reason);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "tcp.reverse.injected", Message = "Injected the reversed frame for {Source} -> {Destination} (packet {Packet}, flow {Flow}, association {TcpAssociation}): {Reason}.")]
    public static partial void TcpReverseInjected(ILogger logger, long? packet, long? flow, long? tcpAssociation, Endpoint source, Endpoint destination, string? reason);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "tcp.redirect.clientReset", Message = "Client reset injected for {Source} -> {Destination}, association {TcpAssociation} ({Outcome}).")]
    public static partial void TcpRedirectClientReset(ILogger logger, long tcpAssociation, Endpoint source, Endpoint destination, string outcome);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "tcp.redirect.capacityReset", Message = "Capacity-rejection reset injected for {Source} -> {Destination} ({Outcome}).")]
    public static partial void TcpRedirectCapacityReset(ILogger logger, Endpoint source, Endpoint destination, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "tcp.redirect.relaySetupFailed", Message = "TCP relay setup failed for {Source} -> {Destination} via proxy {Proxy} at {Upstream} with {Error} after {Attempts} attempt(s): socket error {SocketError}, native error {NativeError}.")]
    public static partial void TcpRedirectRelaySetupFailed(ILogger logger, string error, SocketError? socketError, int? nativeError, string upstream, string proxy, Endpoint source, Endpoint destination, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "tcp.redirect.failed", Message = "The TCP redirect for {Source} -> {Destination} failed with {Error} (reason {Reason}, native error {NativeError}, adapter handle {AdapterHandle}, toward MSTCP {TowardMstcp}).")]
    public static partial void TcpRedirectFailed(ILogger logger, string reason, int? nativeError, string error, long adapterHandle, bool towardMstcp, Endpoint source, Endpoint destination);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "tcp.redirect.deferred-overflow", Message = "A deferred redirect frame toward {Direction} for adapter {AdapterHandle} fell back to the immediate send because the lane table is full ({LaneCapacity} lane(s), {FramesPerLane} frame(s) per lane): {Reason}.")]
    public static partial void TcpRedirectDeferredOverflow(ILogger logger, string reason, long adapterHandle, string direction, int laneCapacity, int framesPerLane);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "tcp.redirect.deferred-failed", Message = "The deferred TCP redirect for {Source} -> {Destination} failed with {Error} (reason {Reason}, native error {NativeError}, adapter handle {AdapterHandle}, toward MSTCP {TowardMstcp}).")]
    public static partial void TcpRedirectDeferredFailed(ILogger logger, string reason, int? nativeError, string error, long adapterHandle, bool towardMstcp, Endpoint source, Endpoint destination);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "tcp.redirect.batch-failed", Message = "The batched redirect injection toward {Direction} for adapter {AdapterHandle} failed with {Error} and its {Frames} frame(s) degraded to single sends (reason {Reason}, native error {NativeError}).")]
    public static partial void TcpRedirectBatchFailed(ILogger logger, string reason, int? nativeError, string error, long adapterHandle, string direction, int frames);

    [LoggerMessage(Level = LogLevel.Information, EventName = "tcp.redirect.capacity", Message = "TCP redirect capacity {Budget} rejected {RejectedSinceLastSummary} flow(s) since the last summary, {RejectedTotal} in total.")]
    public static partial void TcpRedirectCapacity(ILogger logger, int budget, long rejectedTotal, long rejectedSinceLastSummary);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "tcp.redirect.unrelatedPeer", Message = "Accepted a peer that is not the redirect's expected client on listener {Listener}: expected {Expected}, actual {Actual}.")]
    public static partial void TcpRedirectUnrelatedPeer(ILogger logger, Endpoint listener, Endpoint expected, Endpoint actual);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "tcp.relay.faulted", Message = "A TCP relay pump faulted with {Error}.")]
    public static partial void TcpRelayFaulted(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The TCP setup executor ring is full; blocking the redirect flow.")]
    public static partial void TcpSetupExecutorRingFull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect setup failed: {Error}: {Detail}.")]
    public static partial void TcpRedirectSetupFailed(ILogger logger, string error, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The TCP redirect client reset injection failed: {Error}.")]
    public static partial void TcpRedirectClientResetInjectionFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The TCP redirect capacity reset injection failed: {Error}.")]
    public static partial void TcpRedirectCapacityResetInjectionFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect torn down by an IP fragment without observed sequences ({Source} -> {Destination}); no client reset is possible.")]
    public static partial void TcpRedirectFragmentTeardownWithoutSequences(ILogger logger, Endpoint source, Endpoint destination);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect failed: listener allocation failed, blocking the flow.")]
    public static partial void TcpRedirectListenerAllocationFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect failed: no local address available on the origin adapter, blocking the flow.")]
    public static partial void TcpRedirectLocalAddressUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect failed: redirect-table capacity reached or translated-tuple collision, blocking the flow.")]
    public static partial void TcpRedirectClaimFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect failed: the captured frame is not writable in place, blocking the flow.")]
    public static partial void TcpRedirectFrameNotWritable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect failed: SYN endpoint rewrite failed, blocking the flow.")]
    public static partial void TcpRedirectSynRewriteFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The TCP redirect accept failed with {Error}; retrying after a bounded delay.")]
    public static partial void TcpRedirectAcceptFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect relay disposal after a failed attach failed.")]
    public static partial void TcpRedirectRelayDisposalAfterFailedAttach(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect accepted-connection disposal after a failed attach failed.")]
    public static partial void TcpRedirectAcceptedConnectionDisposalFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The TCP redirect relay-end client reset failed.")]
    public static partial void TcpRedirectRelayEndResetFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect relay completion handling failed.")]
    public static partial void TcpRedirectRelayCompletionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect deferred-injection failure handling faulted ({Error}: {Detail}); the lane keeps draining.")]
    public static partial void TcpRedirectDeferredFailureTailFaulted(ILogger logger, string error, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect session release failed.")]
    public static partial void TcpRedirectSessionReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect listener disposal failed.")]
    public static partial void TcpRedirectListenerDisposalFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TCP redirect relay disposal failed.")]
    public static partial void TcpRedirectRelayDisposalFailed(ILogger logger, Exception exception);
}
