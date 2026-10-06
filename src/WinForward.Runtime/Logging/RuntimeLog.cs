using Microsoft.Extensions.Logging;

namespace WinForward.Runtime.Logging;

internal static partial class RuntimeLog
{
    [LoggerMessage(Level = LogLevel.Information, EventName = "runner.heartbeat", Message = "Runner heartbeat after {UptimeSeconds}s: {Flows} of {FlowCapacity} flow(s), {TcpSessions} of {TcpCapacity} TCP session(s), {UdpSessions} of {UdpCapacity} UDP session(s) with {UdpRelayBufferMB} MB of relay buffers, {PumpsRunning} running and {PumpsDegraded} degraded pump(s), degraded {Degraded}, {ConsecutiveForced} consecutive forced trigger(s) with {CooldownSeconds}s of cooldown, {GcCollections} GC collection(s) ({GcGen0}/{GcGen1}/{GcGen2}), {GcAllocatedBytes} byte(s) allocated since startup, {Pools} pool(s) at occupancy {PoolOccupancy}, counter deltas {Deltas}.")]
    public static partial void RunnerHeartbeat(
        ILogger logger,
        long uptimeSeconds,
        int? flows,
        int? flowCapacity,
        int? tcpSessions,
        int? tcpCapacity,
        int? udpSessions,
        int? udpCapacity,
        int? udpRelayBufferMB,
        int? pumpsRunning,
        int? pumpsDegraded,
        string? degraded,
        int? consecutiveForced,
        long? cooldownSeconds,
        int? gcCollections,
        int? gcGen0,
        int? gcGen1,
        int? gcGen2,
        long? gcAllocatedBytes,
        int? pools,
        long? poolOccupancy,
        string? deltas);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "gc.collected", Message = "Garbage collection observed since the last heartbeat: gen0 {Gen0}, gen1 {Gen1}, gen2 {Gen2} ({SinceStart} collection(s) since startup).")]
    public static partial void GcCollected(ILogger logger, int? gen0, int? gen1, int? gen2, int sinceStart);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "adapter.retry", Message = "Adapter {Adapter} ({Name}) hit native error {NativeError} on read retry {Attempt}.")]
    public static partial void AdapterRetry(ILogger logger, string adapter, string name, int nativeError, int attempt);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "runtime.expired", Message = "The idle-expiry sweep expired {Flows} flow(s), {TcpRedirects} TCP redirect(s), {UdpSessions} UDP session(s) and {AttributionPending} pending attribution(s).")]
    public static partial void RuntimeExpired(ILogger logger, int flows, int tcpRedirects, int udpSessions, int attributionPending);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Idle-expiry sweep failed and will retry on the next tick: {Error}: {Detail}.")]
    public static partial void IdleExpirySweepFailed(ILogger logger, string error, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime heartbeat summary failed and will retry on the next tick: {Error}: {Detail}.")]
    public static partial void HeartbeatSummaryFailed(ILogger logger, string error, string detail);
}
