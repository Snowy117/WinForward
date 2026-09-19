using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// Construction options for <see cref="UdpProxyCoordinator"/>: every optional dependency in one
/// named record, with defaults matching the coordinator's historical behavior. A pool or the
/// setup executor that is not injected is created by the coordinator and then owned (and
/// disposed) by it; an injected instance is never disposed by the coordinator. The two
/// <see langword="internal"/> members are test seams (reached through <c>InternalsVisibleTo</c>) and are
/// never set by production composition.
/// </summary>
public sealed record UdpProxyOptions
{
    /// <summary>Receives lifecycle events; null falls back to the no-op runtime logger.</summary>
    public IRuntimeLogger? Logger { get; init; }

    /// <summary>The session budget (also the bound on the setup-cooldown index).</summary>
    public int Capacity { get; init; } = 16_384;

    /// <summary>The pinned maximum Ethernet frame size shared with the transport and reinjector; owns the receive window.</summary>
    public int MaximumFrameSize { get; init; } = UdpFrameBuilder.DefaultMaximumEthernetFrame;

    /// <summary>The clock driving setup cooldowns, datagram TTLs, and activity stamps (injectable for fake-time tests).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Shared pool backing every session's receive window; created if absent.</summary>
    public NativeBufferPool? ReceiveWindowPool { get; init; }

    /// <summary>Shared pool backing every queued setup datagram; created if absent.</summary>
    public NativeBufferPool? SetupQueuePool { get; init; }

    /// <summary>Shared setup executor; created if absent.</summary>
    public ISetupExecutor? SetupExecutor { get; init; }

    /// <summary>Test seam: awaited between the expiry snapshot and the per-session recheck; null in production.</summary>
    internal Func<ValueTask>? BeforeExpiryRecheck { get; init; }

    /// <summary>Test seam: overrides the aggregate setup-queue byte budget; null keeps the production default.</summary>
    internal long? SetupQueueGlobalByteBudget { get; init; }
}
