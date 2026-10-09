using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinForward.E2E.Client.Lanes;

/// <summary>
/// The send half of a lane: pacing, the offer loop (<c>BuildRequest → SendAsync → OnSent</c>, strictly
/// serial), the bounded defer queue and the schedule-truncation verdict. It owns no window and no
/// in-flight count — the policy answers admission (D18.1) — and it never touches the wire format: the
/// policy frames into the engine's buffer and says how many bytes to send.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading contract (D18.2).</b> The send thread — the caller's thread, the one running
/// <see cref="RunAsync"/> — exclusively calls <see cref="ILanePolicy.BuildRequest"/>,
/// <see cref="ILaneTransport.SendAsync"/>, <see cref="ILanePolicy.OnSent"/> and
/// <see cref="ILanePolicy.Settle"/>, in that order per slot and never re-entrantly. The receive loop
/// runs on whatever thread <see cref="ILaneTransport.ReceiveAsync"/> completes on and only ever calls
/// <see cref="ILanePolicy.OnReceive"/>. The policy's book (pending, in-flight, histograms) is written
/// by the send thread alone; the receive thread classifies and enqueues a settlement record it owns,
/// and the engine defines no settlement type.
/// </para>
/// <para>
/// <b>Run shape.</b> Open → offer loop → bounded grace drain → cancel and join the receive loop → final
/// settle → <see cref="LaneCounts"/> (D18.5 #4). The engine starts no threads and disposes nothing: the
/// transport's lifetime belongs to the caller, and running on the calling thread is what lets an
/// allocation gate measure the send path with <see cref="GC.GetAllocatedBytesForCurrentThread"/>.
/// </para>
/// </remarks>
/// <typeparam name="TTransport">The lane's transport adapter; a class so the engine holds the instance it opened.</typeparam>
internal sealed class LaneEngine<TTransport>
    where TTransport : class, ILaneTransport
{
    // One settle per arm-end call is what the contract promises; the bound only exists so a policy
    // that books in batches is still allowed while one that never drains cannot hold the arm open.
    private const int SettleBurstLimit = 8;

    // The grace drain's tick. One millisecond keeps the cost of the drain negligible against its
    // one-second bound while still letting replies land between settles.
    private const int GraceTickMilliseconds = 1;

    private readonly TTransport _transport;
    private readonly ILanePolicy _policy;
    private readonly LaneEngineOptions _options;
    private readonly LaneState _state;
    private bool _ran;

    internal LaneEngine(TTransport transport, ILanePolicy policy, LaneEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(policy);

        _transport = transport;
        _policy = policy;
        _options = options;
        _state = new LaneState(options);
    }

    /// <summary>
    /// The transport's connect outcome, valid once <see cref="RunAsync"/> has returned. A lane whose
    /// transport did not open offered nothing, and its counts say
    /// <see cref="LaneCounts.ScheduleTruncated"/>; the arm reads this to record the connect failure the
    /// engine cannot count (it owns no per-arm failure counter beyond the schedule verdict).
    /// </summary>
    internal LaneOpenResult OpenResult { get; private set; }

    /// <summary>
    /// Runs one lane to its end. Counters and the defer queue are per-run state, so an engine runs
    /// once; a second call is a programming error rather than a silent restart.
    /// </summary>
    internal async Task<LaneCounts> RunAsync(CancellationToken cancellationToken)
    {
        if (_ran)
        {
            throw new InvalidOperationException($"{nameof(LaneEngine<>)}.{nameof(RunAsync)} runs once: its counters and defer queue are per-run state.");
        }

        _ran = true;
        OpenResult = await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!OpenResult.Ok)
        {
            // A lane that never connected offered none of its share of the schedule, so the record
            // must say the schedule was short rather than read as a quiet run.
            _state.ScheduleTruncated = true;
            return _state.Snapshot();
        }

        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = ReceiveLoopAsync(receiveCancellation.Token);
        await OfferLoopAsync(cancellationToken).ConfigureAwait(false);
        await GraceDrainAsync(receive, cancellationToken).ConfigureAwait(false);
        await receiveCancellation.CancelAsync().ConfigureAwait(false);
        await receive.ConfigureAwait(false);

        // The receive loop is joined, so no producer is left: the first settle books whatever the last
        // arrivals queued, and the policy's own IsDrained states that the queue really is empty.
        for (var attempt = 0; attempt < SettleBurstLimit; attempt++)
        {
            _policy.Settle(Clock.Now);
            if (_policy.IsDrained)
            {
                break;
            }
        }

        return _state.Snapshot();
    }

    private async ValueTask OfferLoopAsync(CancellationToken cancellationToken)
    {
        var pacer = new Pacer(_options.RatePerSecond, _options.StartTicks);
        try
        {
            while (!cancellationToken.IsCancellationRequested && Clock.Now < _options.DeadlineTicks)
            {
                var slot = _state.Supplied;

#pragma warning disable S6966, VSTHRD103, MA0042 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
                // ReSharper disable once MethodHasAsyncOverload // The pacing must block: WaitUntilAsync's Task.Delay resolves to the 15.6 ms Windows timer tick and cannot hold these instants.
                Pacer.WaitUntil(pacer.IntendedTicks(slot), cancellationToken);
#pragma warning restore S6966, VSTHRD103, MA0042

                // Sequences are 1-based, as they have always been: slot 0 offers sequence 1 and
                // carries the instant slot 0 was intended for.
                _state.Supplied = slot + 1;

                // The offer loop's one ordering rule (D18.5 #3): the previous slot's settlements are
                // booked after this slot's pace and before this slot's request.
                _policy.Settle(Clock.Now);
                await OfferSlotAsync(_state.Supplied, pacer.IntendedTicks(slot), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the offer loop */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the transport first */
        }

        // Stopping before the deadline means the tail of the schedule was never offered at all.
        _state.ScheduleTruncated = Clock.Now < _options.DeadlineTicks;
    }

    private async ValueTask OfferSlotAsync(long sequence, long intendedTicks, CancellationToken cancellationToken)
    {
        // A deferred intent always goes out before a newer one, so a sample's age stays tied to when
        // it was wanted rather than to whichever slot happened to free.
        var blocked = false;
        while (_state.Deferred.Count > 0)
        {
            var deferred = _state.Deferred.Peek();
            var retry = Build(deferred.Sequence, deferred.IntendedTicks, out var deferredLength);
            if (retry == LaneSlotDecision.Defer)
            {
                // The window is still closed for the oldest intent.
                blocked = true;
                break;
            }

            _state.Deferred.Dequeue();
            if (retry == LaneSlotDecision.Send)
            {
                await SendAsync(deferred.Sequence, deferred.IntendedTicks, deferredLength, cancellationToken).ConfigureAwait(false);
            }

            // Skip: the retry yielded no sample, and it was counted when it was first deferred.
        }

        if (blocked)
        {
            // The oldest intent could not go out, so a newer one must not overtake it: this slot is
            // deferred without asking, exactly as a closed window would leave it.
            Defer(sequence, intendedTicks);
            return;
        }

        var decision = Build(sequence, intendedTicks, out var length);

        // ReSharper disable once ConvertIfStatementToSwitchStatement // The switch form over these three decisions trips SwitchStatementHandlesSomeKnownEnumValuesWithDefault; this if/else chain is the shape the repository already uses for an enum with one silent case (NdisPacketActionExecutor).
        if (decision == LaneSlotDecision.Send)
        {
            await SendAsync(sequence, intendedTicks, length, cancellationToken).ConfigureAwait(false);
        }
        else if (decision == LaneSlotDecision.Defer)
        {
            Defer(sequence, intendedTicks);
        }

        // LaneSlotDecision.Skip: the slot yields nothing at all — no send, no queue entry, no counter.
    }

    private async ValueTask SendAsync(long sequence, long intendedTicks, int length, CancellationToken cancellationToken)
    {
        LaneSendResult result;
        var wouldBlock = false;
        try
        {
            var send = _transport.SendAsync(_state.Buffer.AsMemory(0, length), cancellationToken);

            // The ValueTask reuse trick (audit §9.6): IsCompleted is read before the single await. It
            // is the engine's own reading of the same property the transport reports, and either one
            // means this send did not complete synchronously, so the counter moves once per send.
            wouldBlock = !send.IsCompleted;
            result = await send.ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            // One request failed, not the loop: on a connected datagram socket a previous send's icmp
            // error surfaces here while the next send may well succeed. A transport that throws
            // instead of answering is booked exactly like one that answers Accepted: false — the two
            // shapes differ in who caught the socket error, not in what the lane counts, and neither
            // one ends the schedule.
            result = new LaneSendResult(Accepted: false, WouldBlock: wouldBlock, Error: exception.Message);
        }

        // One reading of "did this send block", from either side of the seam, and one increment per
        // send: a failed send that parked on the socket counts in both the would-block and the failure
        // counters, which is the pair the two counters published before the engine existed.
        if (wouldBlock || result.WouldBlock)
        {
            _state.SendWouldBlock++;
        }

        if (result.Accepted)
        {
            _state.SentOk++;
        }
        else
        {
            _state.SendFailures++;
        }

        // Called on success and on failure alike (D18.5 #6), and always before the next BuildRequest.
        _policy.OnSent(sequence, intendedTicks, result);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var destination = _state.ReceiveBuffer.AsMemory();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await _transport.ReceiveAsync(destination, cancellationToken).ConfigureAwait(false);

                // The receive instant is read here, on the receive thread, and travels on the
                // settlement: a round trip is measured against arrival, never against the later
                // Settle (D18.5 #1).
                _policy.OnReceive(received, destination.Span[..received.Length], Clock.Now);

                if (received.Kind is LaneReceiveKind.EndOfStream or LaneReceiveKind.IOError)
                {
                    // The transport's two terminal outcomes; a malformed message is one message, not
                    // the lane.
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline, the lane teardown or a shutdown request ended the loop */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the transport first */
        }
    }

    private async ValueTask GraceDrainAsync(Task receive, CancellationToken cancellationToken)
    {
        // The tail cohort is what the grace drain exists for: the offer loop has stopped, but replies
        // still in flight are sampled rather than cancelled. Bounded, so a silent product cannot hold
        // the arm open.
        var until = Clock.Now + _options.DrainLimitTicks;
        try
        {
            while (Clock.Now < until && !receive.IsCompleted && !_policy.BookEmpty)
            {
                _policy.Settle(Clock.Now);
                await Task.Delay(GraceTickMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the drain */
        }
    }

    private LaneSlotDecision Build(long sequence, long intendedTicks, out int length) =>
        _policy.BuildRequest(sequence, intendedTicks, _state.Buffer, out length);

    private void Defer(long sequence, long intendedTicks)
    {
        // Every slot that found the window closed counts here — the contract's windowOverflow — and the
        // subset the bounded queue cannot hold counts as dropped as well — the contract's backlogDrops.
        _state.DeferredQueued++;
        if (_state.Deferred.Count >= _options.BacklogLimit)
        {
            _state.DeferredDropped++;
            return;
        }

        _state.Deferred.Enqueue(new DeferredRequest(sequence, intendedTicks));
    }

    /// <summary>
    /// The engine's per-run state, private and nested so a lane's mutable send-side counters cannot be
    /// named from outside — let alone read or written. The only send-side surface a caller gets is the
    /// immutable <see cref="LaneCounts"/> snapshot.
    /// </summary>
    private sealed class LaneState
    {
        internal LaneState(LaneEngineOptions options)
        {
            Buffer = new byte[options.SendBufferBytes];
            ReceiveBuffer = new byte[options.ReceiveBufferBytes];
            Deferred = new Queue<DeferredRequest>(options.BacklogLimit);
        }

        /// <summary>The frame buffer the policy writes into; reused by every slot.</summary>
        internal byte[] Buffer { get; }

        /// <summary>The receive buffer the transport reads into; reused by every receive.</summary>
        internal byte[] ReceiveBuffer { get; }

        /// <summary>Deferred intents, oldest first: the queue's head is the oldest waiting intent.</summary>
        internal Queue<DeferredRequest> Deferred { get; }

        /// <summary>
        /// The queue's occupancy, read when the run returns: the intents no window ever let out. It is
        /// the engine's half of <c>outstandingAtTeardown</c> (D18.6 #1) and deliberately not derived
        /// from the other counters, which cannot see a slot the policy skipped.
        /// </summary>
        private long DeferredPending => Deferred.Count;

        internal long Supplied { get; set; }

        internal long SentOk { get; set; }

        internal long SendWouldBlock { get; set; }

        internal long SendFailures { get; set; }

        internal long DeferredQueued { get; set; }

        internal long DeferredDropped { get; set; }

        internal bool ScheduleTruncated { get; set; }

        internal LaneCounts Snapshot() => new(Supplied, SentOk, SendWouldBlock, SendFailures, DeferredQueued, DeferredDropped, DeferredPending, ScheduleTruncated);
    }

    /// <summary>An intent that found the window closed: the sequence and intended instant it waits for.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct DeferredRequest(long Sequence, long IntendedTicks);
}
