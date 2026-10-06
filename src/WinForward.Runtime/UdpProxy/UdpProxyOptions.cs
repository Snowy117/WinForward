using Microsoft.Extensions.Logging;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// Construction options for <see cref="UdpProxyCoordinator"/>: the optional dependencies in one
/// named record, with defaults matching the coordinator's historical behavior. The shared native
/// pools and setup executor are required constructor parameters owned by composition; the
/// coordinator only borrows them and never disposes them. The two <see langword="internal"/>
/// members are test seams (reached through <c>InternalsVisibleTo</c>) and are never set by
/// production composition.
/// </summary>
public sealed record UdpProxyOptions
{
    /// <summary>Receives lifecycle events; null falls back to the no-op logger.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>The session budget (also the bound on the setup-cooldown index).</summary>
    public int Capacity { get; init; } = 16_384;

    /// <summary>
    /// The per-session relay socket receive buffer in bytes, fed from the validated
    /// <c>udpRelayReceiveBufferKb</c> budget. The transport factory applies it; the coordinator
    /// only reports it so the heartbeat can estimate the aggregate kernel receive-buffer
    /// footprint (configured bytes x live sessions).
    /// </summary>
    public int RelayReceiveBufferBytes { get; init; } = Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize;

    /// <summary>The pinned maximum Ethernet frame size shared with the transport and reinjector; owns the receive window.</summary>
    public int MaximumFrameSize { get; init; } = UdpFrameBuilder.DefaultMaximumEthernetFrame;

    /// <summary>The clock driving setup cooldowns, datagram TTLs, tombstone-style stamps and the ready path's activity store (injectable for fake-time tests).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// The composition's shared activity-bucket clock, driving the sessions' activity stamps and the
    /// expiry scan's cutoff. Null derives a private clock from <see cref="TimeProvider"/>, so
    /// compositions that only inject a time provider keep their behaviour.
    /// </summary>
    public ActivityBucketClock? ActivityClock { get; init; }

    /// <summary>Test seam: awaited between the expiry snapshot and the per-session recheck; null in production.</summary>
    internal Func<ValueTask>? BeforeExpiryRecheck { get; init; }

    /// <summary>Test seam: overrides the aggregate setup-queue byte budget; null keeps the production default.</summary>
    internal long? SetupQueueGlobalByteBudget { get; init; }
}
