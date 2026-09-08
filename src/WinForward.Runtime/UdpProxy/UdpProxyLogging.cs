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
    public static void LogDebug(IRuntimeLogger logger, string eventName, FlowKey flow, long flowGeneration, UdpAssociation association, string? serverName)
    {
        if (!logger.IsEnabled(RuntimeLogLevel.Debug)) return;
        logger.Event(RuntimeLogLevel.Debug, eventName,
            new("flow", flowGeneration == 0 ? null : flowGeneration),
            new("udpAssociation", association.Generation), new("protocol", flow.Protocol),
            new("source", flow.Local), new("destination", flow.Remote), new("proxy", serverName));
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
}
