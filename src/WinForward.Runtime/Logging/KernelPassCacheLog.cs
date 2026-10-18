using Microsoft.Extensions.Logging;
using WinForward.NdisApi;

namespace WinForward.Runtime.Logging;

/// <summary>
/// The kernel pass cache's events. Every message is aggregate only: a rule's 5-tuple, an address, or a
/// port never reaches a log record, matching the per-packet privacy posture of the capture family.
/// </summary>
internal static partial class KernelPassCacheLog
{
    [LoggerMessage(Level = LogLevel.Information, EventName = "kernel.passCache", Message = "Kernel pass cache: {Rules} rule(s), {Active} active, {Bypassed} packet(s) bypassed, {Captured} captured, {Reinjected} reinjected, {CrossingsPerMille} crossing(s) per mille, {CallsPerMille} call(s) per mille.")]
    public static partial void PassCacheSummary(ILogger logger, int rules, int active, long bypassed, long captured, long reinjected, long? crossingsPerMille, long? callsPerMille);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "kernel.passCache.unavailable", Message = "The kernel pass cache is unavailable and stays disabled: the NDISAPI static filter table API is missing or refused its initial reset (native error {NativeError}). Capture keeps the user-mode path.")]
    public static partial void PassCacheUnavailable(ILogger logger, int? nativeError);

    [LoggerMessage(Level = LogLevel.Error, EventName = "kernel.passCache.disabled", Message = "The kernel pass cache disabled itself after a persistent {Operation} fault ({Status}, native error {NativeError}); capture keeps the user-mode path.")]
    public static partial void PassCacheDisabled(ILogger logger, string operation, NdisKernelFilterStatus status, int? nativeError);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "kernel.passCache.fault", Message = "Kernel pass cache {Operation} failed with {Status} (native error {NativeError}); the table is retried on the next tick.")]
    public static partial void PassCacheFault(ILogger logger, string operation, NdisKernelFilterStatus status, int? nativeError);

    [LoggerMessage(Level = LogLevel.Information, EventName = "kernel.passCache.reset", Message = "Kernel pass cache reset on {Reason}: {Status} (native error {NativeError}).")]
    public static partial void PassCacheReset(ILogger logger, string reason, NdisKernelFilterStatus status, int? nativeError);
}
