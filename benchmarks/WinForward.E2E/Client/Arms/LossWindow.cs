namespace WinForward.E2E.Client.Arms;

/// <summary>
/// The loss arm's window admission: one offered pacing slot either goes to the socket or is dropped as
/// client send loss. It is a unit of its own so the simulation that proves "a path which drops
/// datagrams must not stop the client sending" drives the arm's own decision instead of a copy of it.
/// </summary>
internal static class LossWindow
{
    /// <summary>
    /// Books one offered slot and answers whether the arm may hand it to the socket.
    /// </summary>
    /// <remarks>
    /// The release runs first and is driven by the <i>intended</i> send instant: a slot whose W has
    /// elapsed leaves the window whether or not a reply ever came, which is what keeps a datagram the
    /// path dropped from holding a slot for the rest of the arm and stopping the offer loop. The
    /// decision is the arm's only client-side drop: a slot that does not fit is booked as
    /// <see cref="UdpReliabilityTracker.WindowOverflow"/> and never reaches the sent population, so it
    /// cannot be reported as path loss.
    /// </remarks>
    /// <returns><see langword="true"/> when the slot may be sent, <see langword="false"/> when it was
    /// booked as a window overflow instead.</returns>
    internal static bool Admit(UdpReliabilityTracker tracker, long nowTicks, long windowTicks, int window)
    {
        tracker.MarkSupplied();
        tracker.Retire(nowTicks, windowTicks);
        if (tracker.Outstanding < window)
        {
            return true;
        }

        tracker.MarkWindowOverflow();
        return false;
    }
}
