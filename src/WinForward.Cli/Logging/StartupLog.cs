using Microsoft.Extensions.Logging;

namespace WinForward.Cli.Logging;

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, EventName = "cli.run.resolved", Message = "Configuration loaded from {Sources}: log level {LogLevel}, formatter {Formatter}, {Targets} target(s), {HostRules} host and {ForwardedRules} forwarded rule(s), TCP flow capacity {TcpFlowCapacity}, UDP session capacity {UdpSessionCapacity} with a {UdpRelayReceiveBufferKiB} KiB relay buffer each and {UdpSessionIdleSeconds}s idle retention, process paths {ProcessPathDisclosure}.")]
    public static partial void RunResolved(ILogger logger, string sources, string logLevel, string formatter, int targets, int hostRules, int forwardedRules, int tcpFlowCapacity, int udpSessionCapacity, int udpRelayReceiveBufferKiB, int udpSessionIdleSeconds, string processPathDisclosure);

    [LoggerMessage(Level = LogLevel.Information, EventName = "cli.target.socks5", Message = "SOCKS5 target {Target} is available at {Endpoint} over {UdpTransport} UDP, authentication {Authentication}.")]
    public static partial void Socks5TargetAvailable(ILogger logger, string target, string endpoint, string udpTransport, string authentication);

    [LoggerMessage(Level = LogLevel.Information, EventName = "cli.target.local", Message = "Local target {Target} is available at {Endpoint}.")]
    public static partial void LocalTargetAvailable(ILogger logger, string target, string endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pre-flight capture scope resolved to {Adapters} adapter(s) in tunnel mode.")]
    public static partial void CaptureScopePreflightResolved(ILogger logger, int adapters);

    [LoggerMessage(Level = LogLevel.Information, Message = "Interception started. Press Ctrl+C to stop.")]
    public static partial void InterceptionStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shutdown requested; restoring adapter modes.")]
    public static partial void ShutdownRequested(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "WinForward stopped cleanly.")]
    public static partial void StoppedCleanly(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration warning: {Detail}")]
    public static partial void ConfigurationWarning(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "High-resolution timer resolution was not applied; the empty-queue poll granularity stays at about 15.6 ms instead of about 1 ms.")]
    public static partial void HighResolutionTimerUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SOCKS5 address pre-resolution failed for target {Target}: {Detail}")]
    public static partial void Socks5AddressResolutionFailed(ILogger logger, string target, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "NDISAPI unavailable: ndisapi.dll was not found in the application directory.")]
    public static partial void NdisApiLibraryMissing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "NDISAPI unavailable: ndisapi.dll is missing a required export.")]
    public static partial void NdisApiExportMissing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "NDISAPI ABI mismatch: the native library is incompatible with this build.")]
    public static partial void NdisApiAbiMismatch(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "NDISAPI driver error: {Detail}")]
    public static partial void NdisApiDriverError(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "No MSTCP-bound adapters are available to capture.")]
    public static partial void NoCaptureAdapters(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Capture scope resolution failed: {Detail}")]
    public static partial void CaptureScopeResolutionFailed(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "WinForward failed during startup.")]
    public static partial void StartupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "WinForward failed while intercepting.")]
    public static partial void RuntimeFailed(ILogger logger, Exception exception);
}
