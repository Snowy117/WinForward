using System.Net;
using Microsoft.Extensions.Logging;
using WinForward.Core;

namespace WinForward.Runtime.Logging;

internal static partial class UdpProxyLog
{
    [LoggerMessage(Level = LogLevel.Debug, EventName = "udp.session.created", Message = "UDP session created for {Source} -> {Destination} ({Protocol}) via {Target} ({TargetKind}/{UdpTransport}), association {UdpAssociation}, flow {Flow}.")]
    public static partial void UdpSessionCreated(ILogger logger, long? flow, long udpAssociation, TransportProtocol protocol, Endpoint source, Endpoint destination, string target, string targetKind, string? udpTransport);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "udp.session.closed", Message = "UDP session closed for {Source} -> {Destination} ({Protocol}), association {UdpAssociation}, flow {Flow}.")]
    public static partial void UdpSessionClosed(ILogger logger, long? flow, long udpAssociation, TransportProtocol protocol, Endpoint source, Endpoint destination);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "udp.session.expired", Message = "UDP session expired for {Source} -> {Destination} ({Protocol}), association {UdpAssociation}, flow {Flow}.")]
    public static partial void UdpSessionExpired(ILogger logger, long? flow, long udpAssociation, TransportProtocol protocol, Endpoint source, Endpoint destination);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "udp.setup.failed", Message = "UDP session setup failed for {Source} -> {Destination} ({Protocol}): {Reason}.")]
    public static partial void UdpSetupFailed(ILogger logger, TransportProtocol protocol, Endpoint source, Endpoint destination, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDP session setup for {Source} -> {Destination} failed and will retry for a new flow: {Error}: {Detail}.")]
    public static partial void UdpSetupFailureWarning(ILogger logger, Endpoint source, Endpoint destination, string error, string detail);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "udp.session.capacity-block", Message = "The UDP session budget of {Capacity} session(s) is exhausted; the datagram from {Source} -> {Destination} ({Protocol}) was dropped fail-closed: {Reason}.")]
    public static partial void UdpSessionCapacityBlock(ILogger logger, TransportProtocol protocol, Endpoint source, Endpoint destination, string reason, int capacity);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "udp.send.dropped", Message = "Dropped the {Protocol} datagram from {Source} -> {Destination}: {Reason}.")]
    public static partial void UdpSendDropped(ILogger logger, TransportProtocol protocol, Endpoint source, Endpoint destination, string reason);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "udp.setup.cooldown", Message = "Refused the {Protocol} datagram from {Source} -> {Destination} fail-closed while the flow waits out its setup cooldown: {Reason}.")]
    public static partial void UdpSetupCooldown(ILogger logger, TransportProtocol protocol, Endpoint source, Endpoint destination, string reason);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "udp.session.rejected", Message = "Rejected the {Protocol} datagram from {Source} -> {Destination}: {Reason}.")]
    public static partial void UdpSessionRejected(ILogger logger, TransportProtocol protocol, Endpoint source, Endpoint destination, string reason);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "udp.packet.sent", Message = "Sent {Bytes} byte(s) of the {Protocol} flow from {Source} -> {Destination} (packet {Packet}, flow {Flow}, association {UdpAssociation}).")]
    public static partial void UdpPacketSent(ILogger logger, TransportProtocol protocol, Endpoint source, Endpoint destination, long? packet, long? flow, int bytes, long udpAssociation);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "udp.packet.received", Message = "Received {Bytes} byte(s) for flow {Flow} from {Source} toward {Destination}, association {UdpAssociation}.")]
    public static partial void UdpPacketReceived(ILogger logger, long? flow, long udpAssociation, Endpoint source, Endpoint destination, int bytes);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "udp.setupqueue.dropped", Message = "The setup queue of the {Protocol} flow from {Source} -> {Destination} overflowed and dropped {Dropped} datagram(s).")]
    public static partial void UdpSetupQueueDropped(ILogger logger, TransportProtocol protocol, Endpoint source, Endpoint destination, int dropped);

    [LoggerMessage(Level = LogLevel.Debug, Message = "UDP session setup queues dropped {DroppedTotal} datagram(s) total (drop-oldest).")]
    public static partial void UdpSetupQueueDroppedTotal(ILogger logger, long droppedTotal);

    [LoggerMessage(Level = LogLevel.Debug, Message = "UDP transport skipped datagrams in the last window: unexpectedSource={UnexpectedSource} oversized={Oversized} malformed={Malformed} connectionReset={ConnectionReset} domainDestination={DomainDestination}.")]
    public static partial void UdpTransportSkippedSummary(ILogger logger, long unexpectedSource, long oversized, long malformed, long connectionReset, long domainDestination);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "udp.response.foreign_source", Message = "The relay response declared {Source} as its source while the flow expects {Destination} (origin {Origin}, association {UdpAssociation}).")]
    public static partial void UdpResponseForeignSource(ILogger logger, Endpoint destination, Endpoint source, FlowOriginKind origin, long udpAssociation);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "udp.response.reinjected", Message = "Reinjected the {Protocol} response for {Destination} toward {Source} (origin {Origin}) to {Target}, {Bytes} byte(s).")]
    public static partial void UdpResponseReinjected(ILogger logger, TransportProtocol protocol, FlowOriginKind origin, Endpoint source, Endpoint destination, string target, int bytes);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "udp.response.dropped", Message = "Dropped the {Protocol} response for {Destination} toward {Source} (origin {Origin}, {Bytes} byte(s)): {Reason}.")]
    public static partial void UdpResponseDropped(ILogger logger, TransportProtocol protocol, FlowOriginKind origin, Endpoint source, Endpoint destination, string reason, int? bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDP response frame build failed; dropping the response (fail-closed).")]
    public static partial void UdpResponseFrameBuildFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Forwarded UDP response dropped fail-closed: the flow's client MAC was not recorded.")]
    public static partial void UdpClientMacMissingDrop(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "udp.reinject.unresolved", Message = "The origin adapter {OriginAdapter} of {Source} -> {Destination} is no longer in the capture map ({MapAdapters}); reinjecting through the {Fallback} target instead.")]
    public static partial void UdpReinjectUnresolved(ILogger logger, Endpoint source, Endpoint destination, string? originAdapter, string mapAdapters, string fallback);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "udp.reinject.drop", Message = "Dropped the UDP response for {Source} -> {Destination} fail-closed (origin {OriginKind}, adapter {OriginAdapter}): {Reason}.")]
    public static partial void UdpReinjectDrop(ILogger logger, Endpoint source, Endpoint destination, FlowOriginKind originKind, string? originAdapter, string reason);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "udp.association.lost", Message = "The SOCKS5 UDP association with {Proxy} was lost at relay {Relay}: {Reason}.")]
    public static partial void UdpAssociationLost(ILogger logger, string proxy, IPEndPoint relay, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDP response reinjection failed; the response was skipped: {Error}: {Detail}.")]
    public static partial void UdpInjectionFailed(ILogger logger, string error, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDP receive-failure teardown faulted: {Error}: {Detail}.")]
    public static partial void UdpReceiveFailureTeardownFaulted(ILogger logger, string error, string detail);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "udp.targets.noMac", Message = "The UDP host fallback adapter {Host} ({Name}) has no usable MAC address; its targets are dropped fail-closed.")]
    public static partial void UdpTargetsNoMacHostFallback(ILogger logger, string host, string name);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "udp.targets.noMac.adapters", Message = "{Count} adapter(s) in the capture scope have no usable MAC address ({Adapters}); their UDP targets are dropped fail-closed.")]
    public static partial void UdpTargetsNoMacAdapters(ILogger logger, string adapters, long count);
}
