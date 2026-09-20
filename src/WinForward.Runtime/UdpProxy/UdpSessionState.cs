namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// The explicit lifecycle vocabulary of one UDP flow's session. Two levels feed it, each observed
/// under its own lock:
/// <list type="bullet">
/// <item><description>Slot level (the coordinator's <c>_gate</c>): the flow's slot exists without
/// an attached session while its relay is dialing (<see cref="SettingUp"/>); the setup pipeline
/// attaches the session and later flips the slot ready, at which point the session level takes
/// over.</description></item>
/// <item><description>Session level (the session's activity gate): <see cref="Active"/> is the
/// steady state; idle expiry admits <see cref="Expiring"/> (the sweeper owns the removal, and the
/// per-session lifetime is cancelled so the receive loop ends as a normal teardown); a genuine
/// receive or send fault yields <see cref="Faulted"/> (the failure handler owns the removal);
/// <see cref="Disposed"/> is terminal.</description></item>
/// </list>
/// Transitions are one-way — <c>SettingUp → Active → Expiring → Disposed</c> and
/// <c>Active → Faulted → Disposed</c> — except that <c>CancelExpiry</c> can return an <see cref="Expiring"/>
/// session to <see cref="Active"/> when the sweep loses a removal race (the winning owner then
/// disposes it).
/// </summary>
internal enum UdpSessionState
{
    /// <summary>The flow has a slot but no session yet: its relay is dialing and datagrams are queued.</summary>
    SettingUp,

    /// <summary>The session is live and relaying.</summary>
    Active,

    /// <summary>Idle expiry was admitted; the sweeper owns the slot removal.</summary>
    Expiring,

    /// <summary>A genuine receive or send fault was recorded; the failure handler owns the slot removal.</summary>
    Faulted,

    /// <summary>Session disposal was admitted; terminal.</summary>
    Disposed,
}
