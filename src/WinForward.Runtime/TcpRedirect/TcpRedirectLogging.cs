using WinForward.Configuration;
using WinForward.Core;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// Shared trace/debug event formatting for the TCP redirect path, so the coordinator, the setup
/// pipeline, the session store, and the accept loop emit identically shaped events. Static pure
/// formatting over an <see cref="IRuntimeLogger"/> — no instance state.
/// </summary>
internal static class TcpRedirectLogging
{
    public static void LogDebug(IRuntimeLogger logger, string eventName, TcpRedirectSession session, string outcome)
    {
        if (!logger.IsEnabled(RuntimeLogLevel.Debug)) return;
        var association = session.Association;
        logger.Event(RuntimeLogLevel.Debug, eventName,
            new("flow", session.FlowGeneration == 0 ? null : session.FlowGeneration),
            new("tcpAssociation", association.Generation), new("source", association.OriginalKey.Local),
            new("destination", association.OriginalKey.Remote), new("translated", association.TranslatedListenerTuple),
            new("proxy", session.Server.Name), new("outcome", outcome));
    }

    public static void LogTrace(IRuntimeLogger logger, string eventName, CapturedFlowPacket packet, TcpRedirectAssociation? association, string? reason = null)
    {
        if (!logger.IsEnabled(RuntimeLogLevel.Trace)) return;
        logger.Event(RuntimeLogLevel.Trace, eventName,
            new("packet", packet.PacketSequence == 0 ? null : packet.PacketSequence),
            new("flow", packet.FlowGeneration == 0 ? null : packet.FlowGeneration),
            new("tcpAssociation", association?.Generation), new("source", packet.Context.Key.Local),
            new("destination", packet.Context.Key.Remote), new("reason", reason));
    }
}
