namespace WinForward.E2E.Client.Lanes;

/// <summary>
/// What the policy wants done with one offered slot (D18.5 #7). Three states, not two: the window
/// admits the request, the window is closed but the intent can wait in the engine's bounded queue, or
/// the slot yields nothing at all.
/// </summary>
internal enum LaneSlotDecision
{
    /// <summary>Send the bytes <c>BuildRequest</c> framed into the destination.</summary>
    Send = 0,

    /// <summary>
    /// The window is closed. The intent enters the engine's bounded defer queue and is retried, oldest
    /// first, on a later slot — with the sequence and intended instant it was first offered with.
    /// </summary>
    Defer = 1,

    /// <summary>
    /// The slot yields nothing: nothing is sent, nothing is queued and no counter moves. A skipped slot
    /// is still a supplied slot, because the schedule offered it.
    /// </summary>
    Skip = 2,
}

/// <summary>
/// Everything about a lane that is not send mechanics: window admission, frame construction, reply
/// interpretation and every receive-side count. The engine owns pacing, the offer loop and the defer
/// queue; the policy owns the book.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread contract (D18.2, D18.5 #2/#4).</b> <see cref="BuildRequest"/>, <see cref="OnSent"/> and
/// <see cref="Settle"/> are called on the send thread only — the thread the engine runs on — strictly
/// in that order within a slot and never re-entrantly; <see cref="OnReceive"/> is called on whatever
/// thread the transport's receive completed on. The book (pending, in-flight, histograms, counters) is
/// written on the send thread alone; the receive thread may only decode, classify and enqueue its own
/// settlement record into a queue of its own. The engine defines no settlement type and never reads
/// one.
/// </para>
/// <para>
/// <b>Counters are properties, not fields.</b> A policy states a count as a property and never under a
/// <see cref="LaneCounts"/> member's name: the send-side counters are the engine's, and a policy that
/// kept a second copy of one would be two truths about one number.
/// </para>
/// </remarks>
internal interface ILanePolicy
{
    /// <summary>
    /// Frames one request into <paramref name="destination"/> and answers what to do with it.
    /// <paramref name="length"/> is the number of bytes to send and is only read for
    /// <see cref="LaneSlotDecision.Send"/>. A deferred retry is offered with the sequence and intended
    /// instant it was first deferred with, so the call is idempotent and free of side effects (D18.5
    /// #8) — the window answer itself may of course have moved on since.
    /// </summary>
    LaneSlotDecision BuildRequest(long sequence, long intendedTicks, Span<byte> destination, out int length);

    /// <summary>
    /// Books a send's outcome. Called for every request that reached
    /// <see cref="ILaneTransport.SendAsync"/> — success and failure alike (D18.5 #6) — and strictly
    /// before the next <see cref="BuildRequest"/> on the same lane.
    /// </summary>
    // ReSharper disable once UnusedParameter.Global // The instant an intent was wanted for is part of the seam's record; a policy that books a send without it is not evidence that the parameter is unused.
    void OnSent(long sequence, long intendedTicks, in LaneSendResult result);

    /// <summary>
    /// Decodes and classifies one receive outcome and enqueues the settlement it produces. Receive
    /// thread: it may not touch the book and it may not count anything (D18.5 #2) — counters move in
    /// <see cref="Settle"/>. <paramref name="payload"/> is the transport's bytes and does not outlive
    /// the call; <paramref name="receivedTicks"/> is when the receive completed and is what a round
    /// trip must be measured against (D18.5 #1), never the instant of the later
    /// <see cref="Settle"/>.
    /// </summary>
    void OnReceive(in LaneReceiveResult result, ReadOnlySpan<byte> payload, long receivedTicks);

    /// <summary>
    /// The send thread's only settlement point: it removes what the receive thread queued and records
    /// it. Called after every pace and before the next <see cref="BuildRequest"/>, and once more at the
    /// end of the lane after the receive loop has been joined.
    /// </summary>
    // ReSharper disable once UnusedParameter.Global // The settle instant is part of the seam's record; a policy whose samples all carry their own timestamps is not evidence that the parameter is unused.
    void Settle(long nowTicks);

    /// <summary>
    /// The settlement queue is empty: everything the receive thread classified has been booked. This is
    /// the arm-end drain's completion statement, not a claim that replies are still expected —
    /// <see cref="BookEmpty"/> is that one.
    /// </summary>
    bool IsDrained { get; }

    /// <summary>
    /// Nothing is outstanding: no pending request and nothing in flight. The arm-end grace drain keeps
    /// receiving and settling until this holds, or until its bound expires.
    /// </summary>
    bool BookEmpty { get; }
}
