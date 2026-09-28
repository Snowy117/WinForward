namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// Why a UDP flow's session slot is being torn down. The reason is data, not an inference: only
/// <see cref="SetupFailure"/> arms the setup-failure cooldown (a dead SOCKS5 server must not be
/// hammered at datagram rate), while an idle expiry, a transient fault, and a shutdown all leave
/// the flow free to set up again immediately.
/// </summary>
internal enum UdpTeardownReason
{
    /// <summary>The relay setup failed against a reachable-looking server: arm the setup cooldown.</summary>
    SetupFailure,

    /// <summary>Idle expiry: the sweeper owns the removal.</summary>
    Expiry,

    /// <summary>A genuine receive or send fault: the failure handler owns the removal.</summary>
    Fault,

    /// <summary>
    /// The flow's shared SOCKS5 UDP association died without an in-place recovery. Like
    /// <see cref="Fault"/> it arms no setup cooldown: the next datagram re-establishes the flow
    /// (through the pool, which dials a fresh association).
    /// </summary>
    AssociationLost,

    /// <summary>Shutdown or caller cancellation of a setup/dial.</summary>
    Shutdown,
}
