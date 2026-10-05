using WinForward.Configuration;
using WinForward.Core;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// Shared trace/debug event formatting for the UDP proxy path, so the coordinator, the setup
/// pipeline, and the setup budget emit identically shaped events. Static pure formatting over
/// an <see cref="IRuntimeLogger"/> — no instance state. The mirror of TcpRedirectLogging.
/// </summary>
internal static class UdpProxyLogging
{
    /// <summary>
    /// The session-lifecycle debug shape (<c>udp.session.created</c>, <c>.closed</c>,
    /// <c>.expired</c>). Creation passes the flow's resolved target, so the event carries both the
    /// target's name and its kind; the teardown events pass none, and both fields are then null.
    /// </summary>
    public static void LogDebug(IRuntimeLogger logger, string eventName, FlowKey flow, long flowGeneration, UdpAssociation association, ProxyTarget? target = null)
    {
        if (!logger.IsEnabled(RuntimeLogLevel.Debug)) return;
        logger.Event(RuntimeLogLevel.Debug, eventName,
            new("flow", flowGeneration == 0 ? null : flowGeneration),
            new("udpAssociation", association.Generation), new("protocol", flow.Protocol),
            new("source", flow.Local), new("destination", flow.Remote),
            new("target", target?.Name), new("targetKind", LocalOrSocks5(target)));
    }

    /// <summary>The target kind as it appears on the lifecycle events, or null when the event has no target.</summary>
    private static string? LocalOrSocks5(ProxyTarget? target)
    {
        if (target is not { } resolved) return null;
        return resolved.IsLocal ? "local" : "socks5";
    }

    public static void LogTrace(IRuntimeLogger logger, string eventName, FlowKey flow, params RuntimeLogField[] additional)
    {
        if (!logger.IsEnabled(RuntimeLogLevel.Trace)) return;
        var fields = new RuntimeLogField[additional.Length + 3];
        fields[0] = new("protocol", flow.Protocol);
        fields[1] = new("source", flow.Local);
        fields[2] = new("destination", flow.Remote);
        additional.CopyTo(fields, 3);
        logger.Event(RuntimeLogLevel.Trace, eventName, fields);
    }

    public static void LogSetupFailure(IRuntimeLogger logger, FlowKey flow, Exception exception)
    {
        if (!logger.IsEnabled(RuntimeLogLevel.Debug)) return;
        logger.Event(RuntimeLogLevel.Debug, "udp.setup.failed",
            new("protocol", flow.Protocol),
            new("source", flow.Local),
            new("destination", flow.Remote),
            new("reason", exception.GetType().Name));
    }

    /// <summary>
    /// The warn form of the setup-failure event, rate-limited by the caller. It is a plain message
    /// rather than a second structured <c>udp.setup.failed</c> event: <see cref="LogSetupFailure"/>
    /// owns that event name per occurrence, and the stability benchmark counts structured events by
    /// name, so a duplicate name would double-count one failure.
    /// </summary>
    public static void LogSetupFailureWarning(IRuntimeLogger logger, FlowKey flow, Exception exception)
    {
        if (!logger.IsEnabled(RuntimeLogLevel.Warn)) return;
        logger.Warn($"UDP session setup for {flow.Local} -> {flow.Remote} failed and will retry for a new flow: {exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>
    /// The session budget refused a datagram. The caller counts the rejection and owns the rate
    /// limit; the capacity value is what makes the line actionable (raise it or shed the flow). The
    /// event name mirrors <c>flow.capacity-block</c> and is distinct from the per-datagram
    /// <c>udp.session.rejected</c> trace so the two are counted separately.
    /// </summary>
    public static void LogCapacityRejection(IRuntimeLogger logger, FlowKey flow, int capacity)
    {
        if (!logger.IsEnabled(RuntimeLogLevel.Warn)) return;
        logger.Event(RuntimeLogLevel.Warn, "udp.session.capacity-block",
            new("protocol", flow.Protocol),
            new("source", flow.Local),
            new("destination", flow.Remote),
            new("reason", "capacity"),
            new("capacity", capacity));
    }

    /// <summary>
    /// The ready-session send was refused because the session is expiring or faulted. The caller
    /// counts the drop and owns the rate limit; this side only formats.
    /// </summary>
    public static void LogSessionUnavailableDrop(IRuntimeLogger logger, FlowKey flow)
    {
        if (!logger.IsEnabled(RuntimeLogLevel.Debug)) return;
        logger.Event(RuntimeLogLevel.Debug, "udp.send.dropped",
            new("protocol", flow.Protocol),
            new("source", flow.Local),
            new("destination", flow.Remote),
            new("reason", "sessionUnavailable"));
    }
}
