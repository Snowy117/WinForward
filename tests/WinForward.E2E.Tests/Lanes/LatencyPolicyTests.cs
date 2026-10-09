using System.Buffers.Binary;
using System.Text.Json;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Contracts;
using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// The two lane policies on their own: the book they keep, the order they book a reply in, and the
/// window they answer admission from. These facts drive the policies directly, so what they pin is the
/// policy's contract and not the engine's loop (the loop's order has its own facts), and they are the
/// place the ruled order of a udp settlement and the composition of
/// <c>outstandingAtTeardown</c> are locked.
/// </summary>
public sealed class LatencyPolicyTests
{
    private const uint UdpConnectionId = 0x7100_0001u;
    private const uint TcpConnectionId = 0x7400_0001u;
    private const int PayloadBytes = 24;

    [Fact]
    public void ARequestIsFramedIntoTheEngineBufferWithItsSequenceAndTheLanesConnectionId()
    {
        var policy = NewUdpPolicy(out _, out _, window: 4);
        var buffer = new byte[FrameCodec.HeaderSize + PayloadBytes + FrameCodec.TrailerSize];

        var decision = policy.BuildRequest(3, 1234, buffer, out var length);

        Assert.Equal(LaneSlotDecision.Send, decision);
        Assert.Equal(buffer.Length, length);
        Assert.True(FrameCodec.TryDecode(buffer.AsSpan(0, length), out var header, out var payload, out var error), error.ToString());
        Assert.Equal(UdpConnectionId, header.ConnectionId);
        Assert.Equal(3u, header.Sequence);
        Assert.Equal(PayloadBytes, payload.Length);
        Assert.True(Filler.Matches(header.ConnectionId, header.Sequence, payload), "the framed payload must satisfy the filler the target validates");
    }

    [Fact]
    public void AMatchedReplyIsRecordedAtItsArrivalAndReleasesItsSlot()
    {
        var policy = NewUdpPolicy(out var rtt, out var state, window: 4);
        var intended = Clock.Now - Clock.FromSeconds(0.04);
        policy.OnSent(1, intended, new LaneSendResult(true, false, null));

        var arrived = Clock.Now;
        policy.OnReceive(Payload(1), Frame(1), arrived);
        policy.Settle(arrived);

        Assert.Equal(1, state.Received);
        Assert.Equal(0, state.UnmatchedReplies);
        Assert.Equal(0, state.InFlight);
        Assert.Equal(0, state.Pending);
        Assert.Equal(1, rtt.Count);
        var snapshot = HistogramSnapshot(rtt);
        Assert.InRange(snapshot.GetProperty("minUs").GetDouble(), 30_000, 400_000);
    }

    [Fact]
    public void ADuplicateReplyCountsAsReceivedAndUnmatchedAndReleasesNoSecondSlot()
    {
        var policy = NewUdpPolicy(out _, out var state, window: 4);
        policy.OnSent(1, Clock.Now, new LaneSendResult(true, false, null));
        var frame = Frame(1);

        policy.OnReceive(Payload(1), frame, Clock.Now);
        policy.Settle(Clock.Now);
        policy.OnReceive(Payload(1), frame, Clock.Now);
        policy.Settle(Clock.Now);


        // The duplicate is an arrival -- received counts every scored frame -- and it consumed no
        // request, so it is unmatched. What it must not do is release the slot a second time: the
        // window's occupancy is the policy's, and a reply that answered nothing cannot open it.
        Assert.Equal(2, state.Received);
        Assert.Equal(1, state.UnmatchedReplies);
        Assert.Equal(0, state.InFlight);
        Assert.Equal(0, state.Pending);
    }

    [Fact]
    public void AReplyForASequenceThisSocketNeverSentIsUnmatchedAndHoldsNoSlot()
    {
        var policy = NewUdpPolicy(out _, out var state, window: 4);
        policy.OnSent(1, Clock.Now, new LaneSendResult(true, false, null));

        policy.OnReceive(Payload(9), Frame(9), Clock.Now);
        policy.Settle(Clock.Now);


        // This lane's WasSent answer is the pending book: a sequence it does not hold was never sent by
        // this socket. The frame still counts as received -- loss is published as received minus
        // unmatched, so a reply that belongs to no request cancels out of it -- and it releases nothing.
        Assert.Equal(1, state.Received);
        Assert.Equal(1, state.UnmatchedReplies);
        Assert.Equal(1, state.InFlight);
        Assert.Equal(1, state.Pending);
    }

    [Fact]
    public void AFrameFromAnotherConnectionOrWithABadFillerIsBookedWithoutTouchingTheReplyBook()
    {
        var policy = NewUdpPolicy(out _, out var state, window: 4);
        policy.OnSent(1, Clock.Now, new LaneSendResult(true, false, null));

        var foreign = new FrameBuffer(PayloadBytes);
        var foreignLength = foreign.Build(UdpConnectionId + 1, 1, 0);
        policy.OnReceive(Payload(foreignLength), foreign.Memory.Span[..foreignLength], Clock.Now);

        var corrupt = new FrameBuffer(PayloadBytes);
        corrupt.Build(UdpConnectionId, 1, 0);
        corrupt.FlipPayloadByte(PayloadBytes, 0);
        var corruptLength = corrupt.RecomputeChecksum(PayloadBytes);
        policy.OnReceive(Payload(1), corrupt.Memory.Span[..corruptLength], Clock.Now);

        policy.Settle(Clock.Now);

        Assert.Equal(1, state.ForeignConnection);
        Assert.Equal(1, state.Corrupt);
        Assert.Equal(0, state.Received);
        Assert.Equal(1, state.InFlight);
        Assert.Equal(1, state.Pending);
    }

    [Fact]
    public void AnUnhandableDatagramIsUnscoreableAndASocketFailureIsAProtocolError()
    {
        var policy = NewUdpPolicy(out _, out var state, window: 4);

        policy.OnReceive(new LaneReceiveResult(LaneReceiveKind.Malformed, 0, FrameDecodeError.Truncated), [], Clock.Now);
        policy.OnReceive(new LaneReceiveResult(LaneReceiveKind.IOError, 0), [], Clock.Now);
        Assert.False(policy.IsDrained);

        policy.Settle(Clock.Now);

        Assert.Equal(1, state.Corrupt);
        Assert.Equal(1, state.ProtocolErrors);
        Assert.True(policy.IsDrained, "one settle call has to empty the whole queue (D18.6 #5)");
    }

    [Fact]
    public void BookEmptyAndIsDrainedAreTwoDifferentQuestions()
    {
        var policy = NewUdpPolicy(out _, out _, window: 4);

        // Nothing is owed, but the queue holds a settlement: the drain's completion statement is not a
        // claim about what is still expected.
        policy.OnReceive(Payload(1), Frame(1), Clock.Now);

        Assert.False(policy.IsDrained);
        Assert.True(policy.BookEmpty);
    }

    [Fact]
    public void TheWindowAdmitsUpToItsBoundAndReopensWhenASlotIsReleased()
    {
        var policy = NewUdpPolicy(out _, out var state, window: 2);
        var buffer = new byte[FrameCodec.HeaderSize + PayloadBytes + FrameCodec.TrailerSize];

        Assert.Equal(LaneSlotDecision.Send, policy.BuildRequest(1, 0, buffer, out _));
        policy.OnSent(1, Clock.Now, new LaneSendResult(true, false, null));
        Assert.Equal(LaneSlotDecision.Send, policy.BuildRequest(2, 0, buffer, out _));
        policy.OnSent(2, Clock.Now, new LaneSendResult(true, false, null));

        Assert.Equal(LaneSlotDecision.Defer, policy.BuildRequest(3, 0, buffer, out _));
        Assert.Equal(LaneSlotDecision.Defer, policy.BuildRequest(4, 0, buffer, out _));

        // A refused send never enters the book, so it cannot hold a slot either.
        Assert.Equal(LaneSlotDecision.Defer, policy.BuildRequest(5, 0, buffer, out _));
        policy.OnSent(5, Clock.Now, new LaneSendResult(false, false, "refused"));
        Assert.Equal(2, state.InFlight);

        policy.OnReceive(Payload(1), Frame(1), Clock.Now);
        policy.Settle(Clock.Now);

        Assert.Equal(LaneSlotDecision.Send, policy.BuildRequest(6, 0, buffer, out _));
    }

    [Fact]
    public void ARoundTripIsMeasuredAgainstArrivalAndNotAgainstTheLaterSettle()
    {
        var policy = NewUdpPolicy(out var rtt, out _, window: 4);
        var intended = Clock.Now;
        policy.OnSent(1, intended, new LaneSendResult(true, false, null));

        var arrived = intended + Clock.FromSeconds(0.05);
        policy.OnReceive(Payload(1), Frame(1), arrived);

        // Settled five seconds later: a sample taken at the settle instant would be two orders of
        // magnitude larger, and the reading has to be the arrival.
        policy.Settle(arrived + Clock.FromSeconds(5));

        var snapshot = HistogramSnapshot(rtt);
        Assert.InRange(snapshot.GetProperty("maxUs").GetDouble(), 35_000, 500_000);
    }

    [Fact]
    public void ThePendingBookAndTheInFlightWindowAlwaysCountTheSameRequests()
    {
        var policy = NewUdpPolicy(out _, out var state, window: 8);

        policy.OnSent(1, Clock.Now, new LaneSendResult(true, false, null));
        policy.OnSent(2, Clock.Now, new LaneSendResult(true, false, null));
        policy.OnSent(3, Clock.Now, new LaneSendResult(false, false, "refused"));
        Assert.Equal(state.InFlight, state.Pending);

        policy.OnReceive(Payload(1), Frame(1), Clock.Now);
        policy.OnReceive(Payload(1), Frame(1), Clock.Now);
        policy.OnReceive(Payload(77), Frame(77), Clock.Now);
        policy.Settle(Clock.Now);

        Assert.Equal(state.InFlight, state.Pending);

        policy.OnReceive(Payload(2), Frame(2), Clock.Now);
        policy.Settle(Clock.Now);
        Assert.Equal(0, state.InFlight);
        Assert.Equal(0, state.Pending);
    }

    [Fact]
    public void ATcpReplyIsMatchedInArrivalOrderAndNotByTheSequenceItEchoes()
    {
        var policy = NewTcpPolicy(out var rtt, out var state, window: 4);

        // Two requests whose intended instants are a second apart, so which entry a reply consumed is
        // readable from the sample it produced.
        policy.OnSent(1, Clock.Now - Clock.FromSeconds(1), new LaneSendResult(true, false, null));
        policy.OnSent(2, Clock.Now, new LaneSendResult(true, false, null));

        // The reply claims sequence 2; arrival-order matching consumes the oldest entry regardless.
        policy.OnReceive(new LaneReceiveResult(LaneReceiveKind.Payload, PayloadBytes), Frame(2), Clock.Now);
        policy.Settle(Clock.Now);

        Assert.Equal(1, state.Received);
        Assert.Equal(0, state.UnmatchedReplies);
        Assert.Equal(1, state.InFlight);
        var snapshot = HistogramSnapshot(rtt);
        Assert.InRange(snapshot.GetProperty("minUs").GetDouble(), 900_000, 2_000_000);
    }

    [Fact]
    public void ATcpReplyWithNoRequestLeftIsUnmatchedAndReleasesNoSlot()
    {
        var policy = NewTcpPolicy(out _, out var state, window: 4);
        policy.OnSent(1, Clock.Now, new LaneSendResult(true, false, null));

        policy.OnReceive(new LaneReceiveResult(LaneReceiveKind.Payload, PayloadBytes), Frame(1), Clock.Now);
        policy.OnReceive(new LaneReceiveResult(LaneReceiveKind.Payload, PayloadBytes), Frame(2), Clock.Now);
        policy.Settle(Clock.Now);

        Assert.Equal(2, state.Received);
        Assert.Equal(1, state.UnmatchedReplies);
        Assert.Equal(0, state.InFlight);
    }

    [Fact]
    public void ATcpStreamEndIsNotACorruptFrameAndAFailedTransportIsNotOneEither()
    {
        var policy = NewTcpPolicy(out _, out var state, window: 4);

        policy.OnReceive(new LaneReceiveResult(LaneReceiveKind.EndOfStream, 0), [], Clock.Now);
        policy.OnReceive(new LaneReceiveResult(LaneReceiveKind.Malformed, 0, FrameDecodeError.BadChecksum), [], Clock.Now);
        policy.OnReceive(new LaneReceiveResult(LaneReceiveKind.IOError, 0, FrameDecodeError.BadMagic), [], Clock.Now);
        policy.Settle(Clock.Now);

        Assert.Equal(1, state.RemoteClosed);
        Assert.Equal(1, state.Corrupt);
        Assert.Equal(1, state.ProtocolErrors);
        Assert.Equal(0, state.Received);
    }

    /// <summary>
    /// The composition rule behind <c>outstandingAtTeardown</c>: the record publishes the policy's
    /// pending book plus the engine's defer-queue occupancy. The engine is the only thing that can see
    /// the queue, so the fact drives a real lane (real policy, a transport that never answers) and reads
    /// both halves from where they live.
    /// </summary>
    [Fact]
    public async Task OutstandingAtTeardownIsThePolicyPendingBookPlusTheEnginesDeferredIntents()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var state = new UdpLatencyState();
        var policy = new UdpLatencyPolicy(state, UdpConnectionId, PayloadBytes, window: 2, new LogHistogram());
        var transport = new LaneTransportFake(log);
        var engine = new LaneEngine<LaneTransportFake>(transport, policy, LaneOptions(offerSeconds: 0.02));

        var counts = await engine.RunAsync(cancellation.Token).ConfigureAwait(false);

        // The window is two and nothing ever answers, so every slot past the second one waits: the
        // queue holds four and the rest are dropped.
        Assert.Equal(2, counts.SentOk);
        Assert.Equal(2, state.Pending);
        Assert.True(counts.DeferredQueued > 4, $"the paced window has to overrun the backlog, but only {counts.DeferredQueued} slots were deferred");
        Assert.Equal(4, counts.DeferredPending);
        Assert.Equal(counts.DeferredQueued - 4, counts.DeferredDropped);

        // Every supplied slot is either still owed as a reply or still waiting in the queue, and the
        // two together are what the record publishes.
        Assert.Equal(counts.Supplied - counts.DeferredDropped, state.Pending + counts.DeferredPending);
    }

    /// <summary>
    /// The engine's defer occupancy is a snapshot of the queue, not a formula over the other counters.
    /// The policy below makes the difference visible: it refuses the retried intent, so an intent is
    /// dequeued without any counter moving — a subtraction over the counters would report the deferred
    /// count as the whole queue history (4), while the queue really holds one.
    /// </summary>
    [Fact]
    public async Task DeferredPendingIsTheQueueOccupancyAndNotASubtractionOverTheOtherCounters()
    {
        var log = new LaneEventLog();
        using var cancellation = new CancellationTokenSource();
        var policy = new SkipTheRetryPolicy(cancellation);
        var transport = new LaneTransportFake(log);
        var engine = new LaneEngine<LaneTransportFake>(transport, policy, LaneOptions(offerSeconds: 30));

        var counts = await engine.RunAsync(cancellation.Token).ConfigureAwait(false);

        Assert.Equal(5, counts.Supplied);
        Assert.Equal(1, counts.SentOk);
        Assert.Equal(4, counts.DeferredQueued);
        Assert.Equal(0, counts.DeferredDropped);
        Assert.Equal(1, counts.DeferredPending);
        Assert.Equal([1], transport.SentSequences);

        // The subtraction reports four here: it cannot see a skipped retry leaving the queue without
        // moving any counter.
        var subtraction = (counts.DeferredQueued - counts.DeferredDropped) - ((counts.SentOk + counts.SendFailures) - (counts.Supplied - counts.DeferredQueued));
        Assert.NotEqual(counts.DeferredPending, subtraction);
    }

    /// <summary>
    /// The options the two engine-driven facts run with: buffers big enough for a real framed request,
    /// no pace, and a short grace bound so the drain is never what a fact measures.
    /// </summary>
    private static LaneEngineOptions LaneOptions(double offerSeconds)
    {
        return new LaneEngineOptions
        {
            RatePerSecond = 2000,
            StartTicks = Clock.Now,
            DeadlineTicks = Clock.Now + Clock.FromSeconds(offerSeconds),
            BacklogLimit = 4,
            SendBufferBytes = FrameCodec.HeaderSize + PayloadBytes + FrameCodec.TrailerSize,
            ReceiveBufferBytes = FrameCodec.HeaderSize + PayloadBytes + FrameCodec.TrailerSize,
            DrainLimitTicks = Clock.FromSeconds(0.05),
        };
    }

    private static UdpLatencyPolicy NewUdpPolicy(out LogHistogram rtt, out UdpLatencyState state, int window)
    {
        rtt = new LogHistogram();
        state = new UdpLatencyState();
        return new UdpLatencyPolicy(state, UdpConnectionId, PayloadBytes, window, rtt);
    }

    private static LatencyTcpPolicy NewTcpPolicy(out LogHistogram rtt, out LatencyTcpState state, int window)
    {
        rtt = new LogHistogram();
        state = new LatencyTcpState();
        return new LatencyTcpPolicy(state, TcpConnectionId, PayloadBytes, window, rtt);
    }

    private static LaneReceiveResult Payload(int length) => new(LaneReceiveKind.Payload, length);

    private static byte[] Frame(ulong sequence)
    {
        var frame = new FrameBuffer(PayloadBytes);
        var length = frame.Build(UdpConnectionId, sequence, 0);
        return frame.Memory[..length].ToArray();
    }

    private static JsonElement HistogramSnapshot(LogHistogram histogram)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            histogram.Snapshot().WriteTo(writer, ArmKeys.Common.LatencyRecord.TcpRtt);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.GetProperty(ArmKeys.Common.LatencyRecord.TcpRtt).Clone();
    }

    /// <summary>
    /// A policy that sends the first slot, defers every later one, and answers <c>Skip</c> to a retried
    /// intent — a decision no real lane uses. It is what lets a fact tell a queue snapshot apart from a
    /// formula: a skipped retry leaves the queue without moving any counter.
    /// </summary>
    private sealed class SkipTheRetryPolicy : ILanePolicy
    {
        private readonly CancellationTokenSource _cancelAfter;
        private long _highest;

        internal SkipTheRetryPolicy(CancellationTokenSource cancelAfter)
        {
            _cancelAfter = cancelAfter;
        }

        public bool IsDrained => true;

        public bool BookEmpty => false;

        public LaneSlotDecision BuildRequest(long sequence, long intendedTicks, Span<byte> destination, out int length)
        {
            length = sizeof(long);
            if (sequence <= _highest)
            {
                // The retry of an intent already deferred: the slot yields nothing at all.
                return LaneSlotDecision.Skip;
            }

            _highest = sequence;
            BinaryPrimitives.WriteInt64LittleEndian(destination, sequence);
            switch (sequence)
            {
                case 1:
                    return LaneSlotDecision.Send;

                case 5:
                    // Cancels with this intent still queued, so the run ends holding one deferred request
                    // that the subtraction over the counters cannot account for.
                    // ReSharper disable once MethodHasAsyncOverload // The fact needs the loop to stop synchronously inside this call, so the offer loop's own next iteration sees it and no continuation is left pending.
                    _cancelAfter.Cancel();
                    break;
            }

            return LaneSlotDecision.Defer;
        }

        public void OnSent(long sequence, long intendedTicks, in LaneSendResult result)
        {
            /* the window never reopens in this fact */
        }

        public void OnReceive(in LaneReceiveResult result, ReadOnlySpan<byte> payload, long receivedTicks)
        {
            /* nothing arrives */
        }

        public void Settle(long nowTicks)
        {
            /* the policy owns no settlement queue */
        }
    }
}
