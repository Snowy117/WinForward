using Microsoft.Extensions.Logging;
using WinForward.Core;
using WinForward.Runtime.TcpRedirect;

namespace WinForward.Runtime.Logging;

internal static partial class FlowLog
{
    [LoggerMessage(Level = LogLevel.Information, EventName = "flow.table.initialized", Message = "Flow table initialized with {FlowCapacity} flow slot(s) and {SetupWorkers} setup worker(s) (0 = platform default).")]
    public static partial void FlowTableInitialized(ILogger logger, int flowCapacity, int setupWorkers);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "packet.classified", Message = "Packet {Packet} (flow {Flow}) was classified as {Kind} on {Protocol} {Origin} {Source} -> {Destination}, process {Process} at {ProcessPath}.")]
    public static partial void PacketClassified(ILogger logger, long? packet, long? flow, string kind, TransportProtocol protocol, FlowOriginKind origin, Endpoint source, Endpoint destination, string? process, string? processPath);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "packet.flowResolved", Message = "Packet {Packet} (flow {Flow}) resolved a flow entry with existing {Existing} and outcome {Outcome} on {Protocol} {Origin} {Source} -> {Destination}, process {Process} at {ProcessPath}.")]
    public static partial void PacketFlowResolved(ILogger logger, long? packet, long? flow, bool? existing, string? outcome, TransportProtocol protocol, FlowOriginKind origin, Endpoint source, Endpoint destination, string? process, string? processPath);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "packet.selfTraffic", Message = "Packet {Packet} (flow {Flow}) belongs to this host's own traffic with outcome {Outcome} on {Protocol} {Origin} {Source} -> {Destination}, process {Process} at {ProcessPath}.")]
    public static partial void PacketSelfTraffic(ILogger logger, long? packet, long? flow, string outcome, TransportProtocol protocol, FlowOriginKind origin, Endpoint source, Endpoint destination, string? process, string? processPath);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "packet.reverseHandled", Message = "Packet {Packet} (flow {Flow}) was handled by the TCP reverse hook with outcome {Outcome} on {Protocol} {Origin} {Source} -> {Destination}, process {Process} at {ProcessPath}.")]
    public static partial void PacketReverseHandled(ILogger logger, long? packet, long? flow, TcpRedirectOutcome outcome, TransportProtocol protocol, FlowOriginKind origin, Endpoint source, Endpoint destination, string? process, string? processPath);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "packet.fragmentHandled", Message = "Packet {Packet} (flow {Flow}) was handled by the IP fragment hook with outcome {Outcome} on {Protocol} {Origin} {Source} -> {Destination}, process {Process} at {ProcessPath}.")]
    public static partial void PacketFragmentHandled(ILogger logger, long? packet, long? flow, TcpRedirectOutcome outcome, TransportProtocol protocol, FlowOriginKind origin, Endpoint source, Endpoint destination, string? process, string? processPath);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "packet.action", Message = "Packet {Packet} (flow {Flow}) takes action {Action} by rule {Rule} toward target {Target} with reason {Reason} on {Protocol} {Origin} {Source} -> {Destination}, process {Process} at {ProcessPath}.")]
    public static partial void PacketAction(ILogger logger, long? packet, long? flow, FlowAction action, int? rule, string? target, string? reason, TransportProtocol protocol, FlowOriginKind origin, Endpoint source, Endpoint destination, string? process, string? processPath);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "packet.completed", Message = "Packet {Packet} (flow {Flow}) completed with disposition {Disposition} on {Protocol} {Origin} {Source} -> {Destination}, process {Process} at {ProcessPath}.")]
    public static partial void PacketCompleted(ILogger logger, long? packet, long? flow, PacketDisposition disposition, TransportProtocol protocol, FlowOriginKind origin, Endpoint source, Endpoint destination, string? process, string? processPath);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "flow.capacity-block", Message = "The new {Protocol} {Origin} flow {Source} -> {Destination} was blocked fail-closed: the flow table holds {TableSize} of {Capacity} slot(s).")]
    public static partial void FlowCapacityBlock(ILogger logger, TransportProtocol protocol, FlowOriginKind origin, Endpoint source, Endpoint destination, int tableSize, int capacity);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "flow.created", Message = "Flow {Flow} created for the {Protocol} {Origin} flow {Source} -> {Destination}: process {Process} at {ProcessPath}, action {Action} by rule {Rule} via target {Target}.")]
    public static partial void FlowCreated(ILogger logger, long? flow, TransportProtocol protocol, FlowOriginKind origin, Endpoint source, Endpoint destination, string? process, string? processPath, FlowAction action, int? rule, string? target);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "flow.attribution-miss", Message = "Process attribution returned no owner for the {Protocol} host flow {Local} -> {Remote} (afterRetry {AfterRetry}).")]
    public static partial void FlowAttributionMiss(ILogger logger, TransportProtocol protocol, Endpoint local, Endpoint remote, bool afterRetry);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "flow.attribution.pending-rejected", Message = "Refused a new packet for the pending {Protocol} {Origin} flow {Source} -> {Destination}: the pending-attribution depth is {Pending}.")]
    public static partial void FlowAttributionPendingRejected(ILogger logger, TransportProtocol protocol, FlowOriginKind origin, Endpoint source, Endpoint destination, int pending);

    [LoggerMessage(Level = LogLevel.Trace, EventName = "flow.attribution.flow-full", Message = "Refused a packet for the pending {Protocol} flow {Source} -> {Destination}: its per-flow retention ring or frame budget is full.")]
    public static partial void FlowAttributionFlowFull(ILogger logger, TransportProtocol protocol, Endpoint source, Endpoint destination);
}
