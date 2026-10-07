using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

/// <summary>
/// The latency arm's udp book: what came back on one lane and what is still owed, plus the connect
/// facts the lane body measured. Every counter is a property and none is named after a
/// <see cref="LaneCounts"/> member (D18.1): the send side belongs to the engine, this side to the
/// policy, and a name on both would be two truths about one number.
/// </summary>
/// <remarks>
/// The pending book maps a sequence to the instant it was wanted for — the only place a round trip's
/// start can come from — and its size is the in-flight count, because a reply leaves both together
/// (D18.5 #3). Single-writer by contract (D18.5 #11): <see cref="UdpLatencyPolicy.Settle"/> on the
/// send thread is the only writer, and the receive thread only enqueues settlements.
/// </remarks>
internal sealed class UdpLatencyState
{
    private readonly Dictionary<ulong, long> _pending = [];

    /// <summary>0 or 1, set by the lane body itself and summed across lanes, so a lane that never ran cannot pass for a lane that merely sent nothing.</summary>
    internal long Started { get; set; }

    /// <summary>
    /// Every frame the classifier called an arrival, duplicates included, so it can exceed the sent
    /// count. Loss is therefore published against <c>Received - UnmatchedReplies</c> — the frames that
    /// consumed a pending request — which no duplicate can inflate past the sent count.
    /// </summary>
    internal long Received { get; set; }

    /// <summary>Frames this lane could not score: a filler mismatch, a bad checksum, or a datagram that never decoded.</summary>
    internal long Corrupt { get; set; }

    /// <summary>The socket itself failed on the receive side; a non-zero value means the lane stopped reading before its deadline.</summary>
    internal long ProtocolErrors { get; set; }

    /// <summary>Replies that consumed no pending request, whether a duplicate or a sequence this socket never sent.</summary>
    internal long UnmatchedReplies { get; set; }

    /// <summary>
    /// Replies bearing a connection id this socket never used. Each udp socket has its own id, so a
    /// datagram from another flow is internally consistent — its checksum and filler both validate
    /// against its own id — and would otherwise be credited as a legitimate arrival on this lane.
    /// </summary>
    internal long ForeignConnection { get; set; }

    /// <summary>Requests this socket sent that no reply has consumed: the window's occupancy.</summary>
    internal long InFlight { get; private set; }

    /// <summary>Requests still owed when the lane stopped: the pending book's size, and one half of the contract's <c>outstandingAtTeardown</c>.</summary>
    internal long Pending => _pending.Count;

    /// <summary>
    /// Takes one accepted send into the book: the sequence can now be matched by a reply, and it holds a
    /// slot of the window until one matches it.
    /// </summary>
    internal void Book(long sequence, long intendedTicks)
    {
        _pending[(ulong)sequence] = intendedTicks;
        InFlight++;
    }

    /// <summary>
    /// D18.5 #3's second step, and this lane's WasSent answer: a sequence the book still holds is a
    /// sequence this socket sent and no reply has consumed, so removing it is what proves the reply
    /// belonged to a request. The release of the slot is the caller's separate step, in the order the
    /// ruling pins.
    /// </summary>
    internal bool TryTakePending(long sequence, out long intendedTicks) => _pending.Remove((ulong)sequence, out intendedTicks);

    /// <summary>D18.5 #3's third step: the slot a consumed request held goes back to the window.</summary>
    internal void ReleaseInFlight() => InFlight--;
}

/// <summary>What the send thread must book for one queued receive outcome (D18.5 #2: the receive thread classifies, the send thread counts).</summary>
internal enum UdpBooking
{
    /// <summary>A datagram the classifier scored.</summary>
    Verdict = 0,

    /// <summary>A datagram too large for the lane's buffer, so it cannot be handed up as a message.</summary>
    Oversized = 1,

    /// <summary>The socket failed; no datagram arrived and the engine's receive loop has stopped.</summary>
    TransportFailed = 2,
}

/// <summary>
/// One receive outcome waiting for the send thread. A record struct, never a reference (D18.5 #9): the
/// receive thread enqueues it and drops every span it saw.
/// </summary>
/// <param name="Booking">What happened, which decides which counter moves.</param>
/// <param name="Verdict">The classifier's product; only read for <see cref="UdpBooking.Verdict"/>.</param>
/// <param name="ReceivedTicks">When the receive completed, never when it was settled (D18.5 #1).</param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct UdpSettlement(UdpBooking Booking, ReplyVerdict Verdict, long ReceivedTicks);

/// <summary>
/// The latency arm's udp policy: window admission, frame construction and the reply book. It holds the
/// window and the in-flight count the engine deliberately does not (D18.1) and frames straight into the
/// engine's buffer, so a request is written once and no lane-owned copy exists.
/// </summary>
/// <remarks>
/// The receive thread only classifies and enqueues (D18.5 #2); every counter and every round-trip
/// sample moves in <see cref="Settle"/>, on the send thread, so the stats writer and the book have one
/// writer each.
/// </remarks>
internal sealed class UdpLatencyPolicy : ILanePolicy
{
    private readonly UdpLatencyState _state;
    private readonly LogHistogram _rtt;
    private readonly ConcurrentQueue<UdpSettlement> _settlements = new();
    private readonly uint _connectionId;
    private readonly int _payloadBytes;
    private readonly int _window;

    internal UdpLatencyPolicy(UdpLatencyState state, uint connectionId, int payloadBytes, int window, LogHistogram rtt)
    {
        _state = state;
        _connectionId = connectionId;
        _payloadBytes = payloadBytes;
        _window = window;
        _rtt = rtt;
    }

    public bool IsDrained => _settlements.IsEmpty;

    public bool BookEmpty => _state is { InFlight: 0, Pending: 0 };

    public LaneSlotDecision BuildRequest(long sequence, long intendedTicks, Span<byte> destination, out int length)
    {
        if (_state.InFlight >= _window)
        {
            // The window is full: the engine defers this intent and counts the deferral.
            length = 0;
            return LaneSlotDecision.Defer;
        }

        Filler.Fill(_connectionId, (ulong)sequence, destination.Slice(FrameCodec.HeaderSize, _payloadBytes));
        length = FrameCodec.WriteFrameInPlace(destination, _connectionId, (ulong)sequence, (ulong)intendedTicks, _payloadBytes);
        return LaneSlotDecision.Send;
    }

    public void OnSent(long sequence, long intendedTicks, in LaneSendResult result)
    {
        // A refused send never enters the book: there is no reply to expect on that sequence, and no
        // slot of the window to hold.
        if (result.Accepted)
        {
            _state.Book(sequence, intendedTicks);
        }
    }

    public void OnReceive(in LaneReceiveResult result, ReadOnlySpan<byte> payload, long receivedTicks)
    {
        // Receive thread: decode, classify, enqueue — no counter moves here (D18.5 #2).
        var settlement = result.Kind switch
        {
            LaneReceiveKind.Payload => new UdpSettlement(UdpBooking.Verdict, ReplyClassifier.Classify(payload, _connectionId), receivedTicks),

            // Too large for the lane's buffer: unreadable, and the engine's receive loop continues. The
            // transport names it; the book counts it as an unscoreable datagram, which is the bucket a
            // kernel-truncated datagram landed in before the seam could tell the two apart.
            LaneReceiveKind.Malformed => new UdpSettlement(UdpBooking.Oversized, default, receivedTicks),

            _ => new UdpSettlement(UdpBooking.TransportFailed, default, receivedTicks),
        };

        _settlements.Enqueue(settlement);
    }

    public void Settle(long nowTicks)
    {
        // The contract is to empty the queue, not to book one item: the engine's burst limit is a
        // guard against a policy that never drains, never a budget this policy spends (D18.6 #5).
        while (_settlements.TryDequeue(out var settlement))
        {
            switch (settlement.Booking)
            {
                case UdpBooking.Verdict:
                    Book(settlement.Verdict, settlement.ReceivedTicks);
                    break;

                case UdpBooking.Oversized:
                    _state.Corrupt++;
                    break;

                case UdpBooking.TransportFailed:
                    _state.ProtocolErrors++;
                    break;

                default:
                    throw new InvalidOperationException($"Unhandled udp booking '{settlement.Booking}'.");
            }
        }
    }

    /// <summary>
    /// The reply ladder's booking half, in the order D18.5 #3 pins: every valid frame counts, the
    /// pending book decides whether the reply belonged to a request, and only a reply that did releases
    /// its slot.
    /// </summary>
    private void Book(in ReplyVerdict verdict, long receivedTicks)
    {
        switch (verdict.Kind)
        {
            case ReplyKind.Arrived:
                _state.Received++;
                if (_state.TryTakePending(verdict.Sequence, out var intended))
                {
                    // A round trip is measured against arrival, never against the later settle (D18.5 #1).
                    _rtt.Record(Clock.ToNanoseconds(receivedTicks - intended));
                    _state.ReleaseInFlight();
                }
                else
                {
                    // Not in the pending book, which is this lane's WasSent answer: the reply consumed no
                    // request, so no slot is released — a duplicate must not open the window twice.
                    _state.UnmatchedReplies++;
                }

                break;

            case ReplyKind.ForeignConnection:
                _state.ForeignConnection++;
                break;

            case ReplyKind.Corrupt:
            case ReplyKind.CorruptKnownSequence:
            case ReplyKind.Undecodable:
                // What this arm publishes as corrupt is every frame it cannot score, whatever the
                // decoder's reason: the three verdicts stay distinguishable at the seam, not in a
                // published counter whose meaning is fixed.
                _state.Corrupt++;
                break;

            case ReplyKind.Unmatched:
                // The classifier never answers this -- only a ladder's WasSent step produces it, and
                // this ladder's step is the pending lookup above, which books the same counter from the
                // book it owns. The arm names the verdict so the switch covers the vocabulary.
                _state.UnmatchedReplies++;
                break;

            default:
                throw new InvalidOperationException($"Unhandled reply kind '{verdict.Kind}'.");
        }
    }
}
