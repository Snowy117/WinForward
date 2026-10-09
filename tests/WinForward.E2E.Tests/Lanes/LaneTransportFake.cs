using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Lanes;

namespace WinForward.E2E.Tests.Lanes;

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
