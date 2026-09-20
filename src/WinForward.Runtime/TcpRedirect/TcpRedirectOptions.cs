namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// Construction options for <see cref="TcpProxyCoordinator"/>: the optional dependencies in one
/// named record, with defaults matching the coordinator's historical behavior. The shared native
/// pool and setup executor are required constructor parameters owned by composition; the
/// coordinator only borrows them and never disposes them.
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
}
