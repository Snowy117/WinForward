using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Lanes;

namespace WinForward.E2E.Tests.Lanes;

internal enum LaneEventKind
{
    Open,
    Receive,
    ReceiveEnded,
    Build,
    Send,
    Sent,
    Settle,
}

/// <summary>One seam callback, in the order it happened.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct LaneEvent(LaneEventKind Kind, long Sequence, long Ticks);

/// <summary>
/// The ordered record both fakes write into, so a fact can assert the order the engine drives its seam
/// in — pace before settle, settle before build, build before send, and <c>OnSent</c> before the next
/// build — instead of inferring it from counts.
/// </summary>
internal sealed class LaneEventLog
{
    private readonly Lock _gate = new();
    private readonly List<LaneEvent> _events = [];

    internal void Record(LaneEventKind kind, long sequence)
    {
        lock (_gate)
        {
            _events.Add(new LaneEvent(kind, sequence, Clock.Now));
        }
    }

    internal LaneEvent[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _events];
        }
    }
}

/// <summary>How the fake policy answers admission.</summary>
internal enum LanePolicyBehavior
{
    /// <summary>Every slot sends.</summary>
    Send = 0,

    /// <summary>Every slot yields nothing.</summary>
    Skip = 1,

    /// <summary>A real window: send while in-flight is below the window, defer while it is not.</summary>
    Window = 2,
}

/// <summary>What the fake transport answers a send with.</summary>
internal enum LaneSendBehavior
{
    /// <summary><c>Accepted</c>, completed synchronously.</summary>
    Accept = 0,

    /// <summary><c>Accepted: false</c> with an error, completed synchronously.</summary>
    Refuse = 1,

    /// <summary>A <see cref="SocketException"/>, thrown rather than answered.</summary>
    Throw = 2,
}

/// <summary>One <c>BuildRequest</c> call: what the policy was asked for and what it answered.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct SlotRequest(long Sequence, long IntendedTicks, bool IsRetry, LaneSlotDecision Decision);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SentRecord(long Sequence, bool Accepted, bool WouldBlock, string? Error);

/// <summary>One settlement the fake's receive side handed its own queue.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct LaneSettlement(long Sequence, long ReceivedTicks);

/// <summary>
/// A policy shaped like the real one: it owns the window and the in-flight count, frames into the
/// engine's buffer, books sends in <c>OnSent</c> and books replies only in <c>Settle</c>. Its counters
/// are properties and none of them is named after a <see cref="LaneCounts"/> member, which is the
/// property the disjointness contract is asserted against.
/// </summary>
internal sealed class LanePolicyFake : ILanePolicy
{
    private readonly LaneEventLog _log;
    private readonly ConcurrentQueue<LaneSettlement> _settlements = new();
    private readonly List<SlotRequest> _requests = [];
    private readonly List<SentRecord> _sends = [];
    private readonly List<long> _builtSequences = [];
    private long _inFlight;
    private long _highestSequence;
    private long _accepted;
    private long _refused;
    private long _settled;
    private long _settleCalls;
    private long _serialityViolations;
    private long _reentrancyViolations;
    private long _buildDepth;
    private bool _awaitingOnSent;

    internal LanePolicyFake(LaneEventLog log, LanePolicyBehavior behavior = LanePolicyBehavior.Send, int window = 1)
    {
        _log = log;
        Behavior = behavior;
        Window = window;
    }

    internal LanePolicyBehavior Behavior { get; }

    internal int Window { get; }

    /// <summary>
    /// How many grace settles the book needs before it reports empty. 0 keeps the real meaning (empty
    /// when nothing is pending and nothing is in flight); a larger value stages "the drain has work to
    /// do" without a wall-clock assertion.
    /// </summary>
    internal long GraceTicksBeforeEmpty { get; init; }

    /// <summary>
    /// Stages a late reply: from this settle call on, each settle books one request as answered, so the
    /// window opens again and the deferred intents are retried. A fake's device for the shape the real
    /// receive thread produces; 0 never does this.
    /// </summary>
    internal long AnswerFromSettle { get; init; }

    /// <summary>Cancels the run once this many slots have been through their settle; 0 never cancels.</summary>
    internal long CancelAfterSettles { get; init; }

    internal CancellationTokenSource? CancelAfter { get; init; }

    /// <summary>Slots the engine offered a new sequence for; retries do not count.</summary>
    internal long OfferedSlots => Volatile.Read(ref _highestSequence);

    /// <summary>Every <c>BuildRequest</c> call, retries included.</summary>
    internal long BuildCalls
    {
        get
        {
            lock (_requests)
            {
                return _requests.Count;
            }
        }
    }

    internal long AcceptedSends => Volatile.Read(ref _accepted);

    internal long RefusedSends => Volatile.Read(ref _refused);

    internal long SettleCalls => Volatile.Read(ref _settleCalls);

    internal long SettledItems => Volatile.Read(ref _settled);

    /// <summary>The receive instant the last booked settlement carried, read on the send thread in <c>Settle</c>.</summary>
    internal long LastSettledReceivedTicks { get; private set; }

    internal long InFlight => Volatile.Read(ref _inFlight);

    internal long Pending => _settlements.Count;

    /// <summary>Builds that ran while a previous send's <c>OnSent</c> had not happened yet (must be 0).</summary>
    internal long SerialityViolations => Volatile.Read(ref _serialityViolations);

    /// <summary>Builds that re-entered a build (must be 0).</summary>
    internal long ReentrancyViolations => Volatile.Read(ref _reentrancyViolations);

    /// <summary>
    /// Settles that ran after the offer loop: one per grace tick until the staged book emptied, plus
    /// the single settle the engine always runs once it has joined the receive loop (D18.5 #4).
    /// </summary>
    internal long SettlesAfterTheOfferLoop => Math.Max(0, SettleCalls - OfferedSlots);

    internal SlotRequest[] Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    internal SentRecord[] Sends
    {
        get
        {
            lock (_sends)
            {
                return [.. _sends];
            }
        }
    }

    internal long[] BuiltSequences
    {
        get
        {
            lock (_builtSequences)
            {
                return [.. _builtSequences];
            }
        }
    }

    public bool IsDrained => _settlements.IsEmpty;

    public bool BookEmpty => InFlight == 0 && Pending == 0 && SettlesAfterTheOfferLoop >= GraceTicksBeforeEmpty;

    public LaneSlotDecision BuildRequest(long sequence, long intendedTicks, Span<byte> destination, out int length)
    {
        length = 0;
        if (Interlocked.Increment(ref _buildDepth) > 1)
        {
            _reentrancyViolations++;
        }

        if (_awaitingOnSent)
        {
            // The engine must await the send and report it before it asks for the next request.
            _serialityViolations++;
        }

        try
        {
            // The engine offers a new sequence for a new slot and an older one for a deferred retry,
            // so a sequence it has already seen is what a retry looks like from here.
            var isRetry = sequence <= _highestSequence;
            _highestSequence = Math.Max(_highestSequence, sequence);
            _log.Record(LaneEventKind.Build, sequence);
            lock (_builtSequences)
            {
                _builtSequences.Add(sequence);
            }

            var decision = Behavior switch
            {
                LanePolicyBehavior.Skip => LaneSlotDecision.Skip,
                LanePolicyBehavior.Send => LaneSlotDecision.Send,
                _ => InFlight < Window ? LaneSlotDecision.Send : LaneSlotDecision.Defer,
            };

            if (decision == LaneSlotDecision.Send)
            {
                BinaryPrimitives.WriteInt64LittleEndian(destination, sequence);
                length = sizeof(long);
                _awaitingOnSent = true;
            }

            lock (_requests)
            {
                _requests.Add(new SlotRequest(sequence, intendedTicks, isRetry, decision));
            }

            return decision;
        }
        finally
        {
            Interlocked.Decrement(ref _buildDepth);
        }
    }

    public void OnSent(long sequence, long intendedTicks, in LaneSendResult result)
    {
        if (!_awaitingOnSent)
        {
            _serialityViolations++;
        }

        _awaitingOnSent = false;
        lock (_sends)
        {
            _sends.Add(new SentRecord(sequence, result.Accepted, result.WouldBlock, result.Error));
        }

        if (result.Accepted)
        {
            Interlocked.Increment(ref _accepted);
            Interlocked.Increment(ref _inFlight);
        }
        else
        {
            Interlocked.Increment(ref _refused);
        }

        _log.Record(LaneEventKind.Sent, sequence);
    }

    /// <summary>
    /// Decode, classify, enqueue — and nothing else (D18.5 #2). The fake keeps no counter here on
    /// purpose: a policy that counted on the receive thread would be exactly what the contract forbids.
    /// </summary>
    public void OnReceive(in LaneReceiveResult result, ReadOnlySpan<byte> payload, long receivedTicks)
    {
        var sequence = result.Kind == LaneReceiveKind.Payload && payload.Length >= sizeof(long)
            ? BinaryPrimitives.ReadInt64LittleEndian(payload)
            : 0;
        _settlements.Enqueue(new LaneSettlement(sequence, receivedTicks));
    }

    public void Settle(long nowTicks)
    {
        var call = Interlocked.Increment(ref _settleCalls);
        while (_settlements.TryDequeue(out var settlement))
        {
            LastSettledReceivedTicks = settlement.ReceivedTicks;
            Interlocked.Increment(ref _settled);
            if (Interlocked.Read(ref _inFlight) > 0)
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        if (AnswerFromSettle > 0 && call >= AnswerFromSettle && Interlocked.Read(ref _inFlight) > 0)
        {
            Interlocked.Decrement(ref _inFlight);
        }

        _log.Record(LaneEventKind.Settle, _highestSequence);
        if (CancelAfterSettles > 0 && call >= CancelAfterSettles)
        {
            CancelAfter?.Cancel();
        }
    }
}

/// <summary>
/// A transport that answers everything the engine asks and records what it was asked, including the
/// two failure shapes a real adapter can present (a refused send and a thrown socket error) and the
/// incomplete <c>ValueTask</c> that <c>sendWouldBlock</c> is counted from.
/// </summary>
internal sealed class LaneTransportFake : ILaneTransport
{
    // Long enough that a callback racing the engine's IsCompleted read cannot land inside it, short
    // enough that the facts built on it stay in the hundreds of milliseconds.
    private const int IncompleteSendDelayMilliseconds = 50;

    private readonly LaneEventLog _log;
    private readonly List<long> _sent = [];
    private long _sendCalls;
    private long _receiveCalls;
    private long _disposeCalls;
    private long _overlapViolations;
    private int _sendPending;

    internal LaneTransportFake(LaneEventLog log)
    {
        _log = log;
    }

    /// <summary>The connect outcome the engine sees.</summary>
    internal LaneOpenResult Open { get; init; } = new(true, null);

    internal LaneSendBehavior Behavior { get; init; } = LaneSendBehavior.Accept;

    /// <summary>Reports <c>WouldBlock</c> in the result, as a real adapter's own pre-await check does.</summary>
    internal bool ReportWouldBlock { get; init; }

    /// <summary>
    /// Answers asynchronously, so the returned <c>ValueTask</c> is incomplete when the engine reads
    /// <c>IsCompleted</c>. The completion is delayed rather than merely yielded: a yield's continuation
    /// can win that race on a loaded host — a property of this fake, not of the engine, whose reading is
    /// a point-in-time observation by design (<c>sendWouldBlock</c> means "not synchronous", audit §9.6).
    /// </summary>
    internal bool IncompleteSends { get; init; }

    /// <summary>Cancels after this many sends; 0 never cancels.</summary>
    internal long CancelAfterSends { get; init; }

    internal CancellationTokenSource? CancelAfter { get; init; }

    /// <summary>The policy to hand a reply to after the sends named in <see cref="ReplyAfterSends"/>.</summary>
    internal LanePolicyFake? ReplyTo { get; init; }

    /// <summary>1-based send numbers after which the fake hands the policy one reply, as the receive thread would.</summary>
    internal long[] ReplyAfterSends { get; init; } = [];

    internal long SendCalls => Volatile.Read(ref _sendCalls);

    internal long ReceiveCalls => Volatile.Read(ref _receiveCalls);

    internal long DisposeCalls => Volatile.Read(ref _disposeCalls);

    /// <summary>Sends that started while an earlier one was still pending (must be 0).</summary>
    internal long OverlapViolations => Volatile.Read(ref _overlapViolations);

    internal long[] SentSequences
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public ValueTask<LaneOpenResult> OpenAsync(CancellationToken cancellationToken)
    {
        _log.Record(LaneEventKind.Open, 0);
        return new ValueTask<LaneOpenResult>(Open);
    }

    public ValueTask<LaneSendResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _sendCalls);
        var sequence = payload.Length >= sizeof(long) ? BinaryPrimitives.ReadInt64LittleEndian(payload.Span) : 0;
        lock (_sent)
        {
            _sent.Add(sequence);
        }

        _log.Record(LaneEventKind.Send, sequence);

        // The cancellation is armed before the failure shapes below, so a transport that only ever
        // throws still ends the offer loop instead of spinning it to the deadline.
        if (CancelAfterSends > 0 && call >= CancelAfterSends)
        {
            CancelAfter?.Cancel();
        }

        if (Behavior == LaneSendBehavior.Throw)
        {
            throw new SocketException((int)SocketError.ConnectionReset);
        }

        if (Interlocked.Exchange(ref _sendPending, 1) == 1)
        {
            // A send that is still in flight when the next one starts means the engine let two overlap.
            _overlapViolations++;
        }

        var result = Behavior == LaneSendBehavior.Refuse
            ? new LaneSendResult(false, ReportWouldBlock, "the fake refused this send")
            : new LaneSendResult(true, ReportWouldBlock, null);

        if (CancelAfterSends > 0 && call >= CancelAfterSends)
        {
            CancelAfter?.Cancel();
        }

        if (ReplyTo is not null && Array.IndexOf(ReplyAfterSends, call) >= 0)
        {
            ReplyTo.OnReceive(new LaneReceiveResult(LaneReceiveKind.Payload, payload.Length), payload.Span, Clock.Now);
        }

        if (!IncompleteSends)
        {
            Volatile.Write(ref _sendPending, 0);
            return new ValueTask<LaneSendResult>(result);
        }

        return CompleteLaterAsync(result);
    }

    /// <summary>
    /// Hands the loop <see cref="LaneReceiveKind.EndOfStream"/> on the receive this counter names, the way
    /// a peer's FIN ends a stream lane, so a fact can reach the grace drain's third exit — the receive loop
    /// having ended on its own — without cancelling the run. 0 parks until the engine cancels.
    /// </summary>
    internal long EndOfStreamAtReceive { get; init; }

    /// <summary>
    /// Nothing arrives on a lane the fake drives: the receive loop suspends here until the engine's
    /// teardown cancels the token, which is the cancellation path the loop is supposed to absorb.
    /// </summary>
    public ValueTask<LaneReceiveResult> ReceiveAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _receiveCalls);
        _log.Record(LaneEventKind.Receive, 0);
        if (PayloadAtReceive > 0 && call == PayloadAtReceive)
        {
            return new ValueTask<LaneReceiveResult>(PayloadAfterAsync(cancellationToken));
        }

        return EndOfStreamAtReceive > 0 && call >= EndOfStreamAtReceive
            ? new ValueTask<LaneReceiveResult>(new LaneReceiveResult(LaneReceiveKind.EndOfStream, 0))
            : new ValueTask<LaneReceiveResult>(ParkAsync(cancellationToken));
    }

    /// <summary>
    /// Hands the loop one well-formed message on this receive (1-based), and never before
    /// <see cref="PayloadNotBeforeTicks"/>: a reply staged for an instant past the offer loop's deadline
    /// is what proves the lane samples the cohort it still has in flight instead of losing it with the
    /// socket. 0 never does this, and the wait is the engine's to cancel.
    /// </summary>
    internal long PayloadAtReceive { get; init; }

    /// <summary>The instant <see cref="PayloadAtReceive"/> may be handed up at, on <see cref="Clock"/>'s scale.</summary>
    internal long PayloadNotBeforeTicks { get; init; }

    public void Dispose() => Interlocked.Increment(ref _disposeCalls);

    private async Task<LaneReceiveResult> ParkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The engine joins this loop at the end of a lane; the event is what lets a fact assert the
            // join happened before the arm-end settle.
            _log.Record(LaneEventKind.ReceiveEnded, 0);
            throw;
        }

        return new LaneReceiveResult(LaneReceiveKind.EndOfStream, 0);
    }

    /// <summary>
    /// Holds the staged message until its instant and then hands it up. The delay is cancelled with the
    /// run, so a lane whose drain never runs loses the message the same way it loses the socket.
    /// </summary>
    private async Task<LaneReceiveResult> PayloadAfterAsync(CancellationToken cancellationToken)
    {
        var remainingTicks = PayloadNotBeforeTicks - Stopwatch.GetTimestamp();
        if (remainingTicks > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency), cancellationToken).ConfigureAwait(false);
        }

        return new LaneReceiveResult(LaneReceiveKind.Payload, 0);
    }

    private async ValueTask<LaneSendResult> CompleteLaterAsync(LaneSendResult result)
    {
        // Deliberately not cancelled by the run's token: a send that the arm's cancellation interrupts
        // would never reach OnSent, and these facts are about the send that did happen.
        await Task.Delay(IncompleteSendDelayMilliseconds).ConfigureAwait(false);
        Volatile.Write(ref _sendPending, 0);
        return result;
    }
}
