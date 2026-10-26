using System.Net;
using Microsoft.Extensions.Logging;
using WinForward.Runtime.TcpRedirect;
using WinForward.Userland.Policy;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// The WinForward.Kernel backend's events. The startup sequence, the shutdown summary and every
/// backend-level failure (device, policy, session, accept) stay visible at the default level. The
/// per-connection record — accepted, refused, capacity-blocked, relay established/ended/faulted — is
/// Debug, the packet path's level for a per-flow record: refusals are per-connection and can arrive at
/// connection rate, so the shutdown summary's counters carry them at the default level instead of a
/// line per refusal.
/// </summary>
internal static partial class KernelBackendLog
{
    [LoggerMessage(Level = LogLevel.Error, EventName = "kernel.device.failed", Message = "The WinForward.Kernel driver call {Operation} failed with {Error}: {Detail}. The Kernel backend stops instead of reporting a driver it cannot command as serving traffic.")]
    public static partial void DeviceFailed(ILogger logger, string operation, string error, string detail);

    [LoggerMessage(Level = LogLevel.Error, EventName = "kernel.policy.failed", Message = "The Kernel host policy did not compile: {Path}: {Message}")]
    public static partial void PolicyCompilationFailed(ILogger logger, string path, string message);

    [LoggerMessage(Level = LogLevel.Information, EventName = "kernel.state.cleared", Message = "Disabled the Kernel host redirect and cleared the forwarded state, so a previous run's configuration cannot redirect connections to a listener that is not up yet.")]
    public static partial void StaleStateCleared(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, EventName = "kernel.listener.bound", Message = "Kernel redirect listener bound on port {ListenerPort} ({TcpListeners} TCP listener(s)); block sink port {BlockSinkPort}, block reachable {ContainsBlock}.")]
    public static partial void ListenerBound(ILogger logger, int listenerPort, int tcpListeners, int blockSinkPort, bool containsBlock);

    [LoggerMessage(Level = LogLevel.Information, EventName = "kernel.rules.installed", Message = "Kernel host rule blob installed and verified: generation {Generation}, {RuleCount} rule(s), fallback {Fallback}, {BlobBytes} byte(s), block reachable {ContainsBlock}.")]
    public static partial void RulesInstalled(ILogger logger, uint generation, int ruleCount, RuleAction fallback, int blobBytes, bool containsBlock);

    [LoggerMessage(Level = LogLevel.Information, EventName = "kernel.ready", Message = "Kernel host TCP interception is ready: control ABI {AbiVersion}, listener port {ListenerPort}, block sink port {BlockSinkPort}, {RuleCount} host rule(s), TCP flow capacity {FlowCapacity}, relay setup bound {SetupWorkers}.")]
    public static partial void Ready(ILogger logger, uint abiVersion, int listenerPort, int blockSinkPort, int ruleCount, int flowCapacity, int setupWorkers);

    [LoggerMessage(Level = LogLevel.Information, EventName = "kernel.started", Message = "Kernel host TCP interception started. Press Ctrl+C to stop.")]
    public static partial void Started(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, EventName = "kernel.shutdown", Message = "Shutdown requested; disabling the Kernel host redirect before the listener and the block sink are released.")]
    public static partial void ShuttingDown(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, EventName = "kernel.stop", Message = "WinForward.Kernel stopped: {Accepted} connection(s) accepted, {Refused} refused, {CapacityBlocked} refused at capacity, {Established} relay(s) established, {Stalled} stalled, {Faulted} faulted, {Active} session(s) active.")]
    public static partial void Stopped(ILogger logger, long accepted, long refused, long capacityBlocked, long established, long stalled, long faulted, long active);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "kernel.accept.failed", Message = "The Kernel redirect listener accept failed with {Error}; retrying after a bounded delay.")]
    public static partial void AcceptFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Error, EventName = "kernel.session.failed", Message = "The Kernel session {Site} failed while the backend was running.")]
    public static partial void SessionFailed(ILogger logger, string site, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "kernel.context.failed", Message = "Closed the connection from {Peer} without a relay: {Error}")]
    public static partial void ContextFailed(ILogger logger, string error, EndPoint? peer);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "kernel.session.refused", Message = "Closed the connection from {Peer} without a relay: {Reason}")]
    public static partial void SessionRefused(ILogger logger, string reason, EndPoint? peer);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "kernel.session.accepted", Message = "Accepted a Kernel-redirected TCP connection from {Peer} for {Destination} (rule {RuleIndex}, process {ProcessId}).")]
    public static partial void SessionAccepted(ILogger logger, EndPoint? peer, EndPoint destination, int ruleIndex, int processId);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "kernel.session.capacity", Message = "Kernel TCP flow capacity {Capacity} is full; closed the connection from {Peer} for {Destination} instead of queueing it.")]
    public static partial void CapacityBlocked(ILogger logger, int capacity, EndPoint? peer, EndPoint destination);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "kernel.relay.established", Message = "Kernel relay established for {Destination} via target {Target} (rule {RuleIndex}, process {ProcessId}).")]
    public static partial void RelayEstablished(ILogger logger, EndPoint destination, string target, int ruleIndex, int processId);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "kernel.relay.ended", Message = "Kernel relay for {Destination} via target {Target} ended as {EndKind} after {ServerBytes} server byte(s).")]
    public static partial void RelayEnded(ILogger logger, EndPoint destination, string target, RelayEndKind endKind, long serverBytes);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "kernel.relay.abandoned", Message = "Kernel relay for {Destination} was torn down by the shutdown before it ended on its own.")]
    public static partial void RelayAbandoned(ILogger logger, EndPoint destination);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "kernel.relay.setupFailed", Message = "The Kernel relay setup for {Destination} via target {Target} failed with {Error}: {Detail}")]
    public static partial void RelaySetupFailed(ILogger logger, EndPoint destination, string target, string error, string detail);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "kernel.relay.faulted", Message = "Kernel relay for {Destination} faulted with {Error}.")]
    public static partial void RelayFaulted(ILogger logger, EndPoint destination, string error);

    [LoggerMessage(Level = LogLevel.Error, EventName = "kernel.runtime.failed", Message = "The WinForward.Kernel backend failed while intercepting.")]
    public static partial void RuntimeFailed(ILogger logger, Exception exception);
}
