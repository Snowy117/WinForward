using WinForward.Core;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// Construction options for <see cref="TcpProxyCoordinator"/>: every optional dependency in one
/// named record, with defaults matching the coordinator's historical behavior. A pool or the
/// setup executor that is not injected is created by the coordinator and then owned (and
/// disposed) by it; an injected instance is never disposed by the coordinator.
/// </summary>
public sealed record TcpRedirectOptions
{
    /// <summary>Receives lifecycle events; null falls back to the no-op runtime logger.</summary>
    public IRuntimeLogger? Logger { get; init; }

    /// <summary>The concurrent proxied-flow budget; null keeps the historical default (16,384).</summary>
    public int? Capacity { get; init; }

    /// <summary>The clock driving tombstone grace, setup cooldowns, capacity-reset cooldowns, and pending-SYN TTLs (injectable for fake-time tests).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Optional sink for client-visible failure health signals; null reports to the shared no-op.</summary>
    public IInterceptionHealthSignal? HealthSignal { get; init; }

    /// <summary>Shared pool backing the retained SYN copies; created if absent.</summary>
    public NativeBufferPool? SynCopyPool { get; init; }

    /// <summary>Shared setup executor; created if absent.</summary>
    public ISetupExecutor? SetupExecutor { get; init; }
}
