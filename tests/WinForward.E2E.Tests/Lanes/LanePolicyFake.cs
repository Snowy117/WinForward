using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinForward.E2E.Client.Lanes;

namespace WinForward.E2E.Tests.Lanes;

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
