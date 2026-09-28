using WinForward.Core;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// The control seam between the setup pipeline (<see cref="UdpSessionSetup"/>) and the
/// coordinator's slot state: exactly these five gate-taking operations, so the module can be
/// exercised directly against a fake host (no real coordinator) and the coordinator's gate stays
/// the single arbiter of slot state. A <see cref="UdpProxyCoordinator.UdpSessionSlot"/> is an
/// opaque handle to this module — every member takes the coordinator gate internally and the
/// setup pipeline never observes the slot dictionary itself.
/// </summary>
internal interface IUdpSessionSlotHost
{
    /// <summary>Attaches a constructed session to its slot under the coordinator gate; the point where the session becomes visible to dispatch.</summary>
    void AttachSession(UdpProxyCoordinator.UdpSessionSlot slot, UdpProxySession session);

    /// <summary>Re-stamps the slot's queued setup datagrams at dial start, but only while the slot is still the flow's registered owner; returns the number of entries refreshed.</summary>
    int RefreshSetupStamps(FlowKey flow, UdpProxyCoordinator.UdpSessionSlot slot);

    /// <summary>One flush-dequeue step: verifies slot ownership, dequeues the next buffered datagram (releasing its budget charge exactly once; the caller releases the lease), or flips the slot ready when the queue drains.</summary>
    (UdpSessionSetup.FlushStep Step, NativeLease Lease, int Length, DateTimeOffset EnqueuedAt) DequeueForFlush(FlowKey flow, UdpProxyCoordinator.UdpSessionSlot slot);

    /// <summary>Removes the flow's slot when it is still the exact <paramref name="slot"/> instance, releasing the session and draining queued datagrams fail-closed; returns true when this caller owned the removal. Only <see cref="UdpTeardownReason.SetupFailure"/> arms the setup cooldown.</summary>
    Task<bool> RemoveSlotAsync(FlowKey flow, UdpProxyCoordinator.UdpSessionSlot slot, UdpTeardownReason reason);

    /// <summary>
    /// The receive-failure signal: a non-blocking notification that the session's receive loop
    /// ended on a fault. It must return promptly — it runs inside that loop's own frame — and the
    /// host turns it into a tracked teardown on its own scope.
    /// </summary>
    void RemoveReceiveFailedSession(UdpProxySession session);
}
