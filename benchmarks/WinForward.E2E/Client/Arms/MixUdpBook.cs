using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinForward.E2E.Client.Lanes;

namespace WinForward.E2E.Client.Arms;

/// <summary>What one receive-side outcome asks the send side to book.</summary>
internal enum MixUdpBooking
{
    /// <summary>One datagram the classifier scored; the send side resolves it against the sent book.</summary>
    Verdict = 0,

    /// <summary>The receive loop's socket failed. No datagram arrived, and the lane is client-caused loss.</summary>
    ReceiveFailed = 1,
}

/// <summary>
/// One receive outcome waiting for the send side. A record struct, never a reference (D18.5 #9): the
/// receive task enqueues it and drops every span it saw. The verdict is deliberately left unresolved —
/// the WasSent step belongs to the send side, which is the only holder of the sent book (D19.3 F).
/// </summary>
/// <param name="Booking">What happened, which decides which counter moves.</param>
/// <param name="Verdict">The classifier's product; only read for <see cref="MixUdpBooking.Verdict"/>.</param>
/// <param name="ArrivedTicks">When the datagram arrived, never when it was settled (D18.5 #1).</param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct MixUdpSettlement(MixUdpBooking Booking, ReplyVerdict Verdict, long ArrivedTicks);

/// <summary>
/// The mix arm's UDP book for one desktop: what was handed to the socket, what is still owed, and the
/// hand-over between the lane's two sides.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading contract</b> — the same shape <c>ILanePolicy.Settle</c> pins for the latency arm,
/// carried by this book's own types (D19.2 ⑯: no shared type, no merge into <c>LaneEngine</c>):
/// </para>
/// <list type="number">
/// <item>the <b>receive</b> task calls only <see cref="Offer"/> and
/// <see cref="OfferReceiveFailure"/>. It classifies the datagram, enqueues the still-unresolved
/// verdict with the instant the datagram arrived, and moves nothing else: no tracker call, no
/// counter, no pending removal, no round-trip sample. That is what makes the receive side readable
/// in one place and the send side the tracker's only writer.</item>
/// <item>the <b>send</b> side calls <see cref="BookSent"/> before it hands the datagram to the
/// socket — never after, so a reply can never name a sequence the sent book has not booked yet —
/// and <see cref="Settle"/> at every pacing point: after <c>Pacer.WaitUntil</c> and before the next
/// send, the order D18.2 pins. Every tracker mutation, the <c>WasSent</c> question, the pending
/// removal and the round-trip sample happen inside <see cref="Settle"/>, on that one logical
/// writer.</item>
/// <item>at arm end the order is offer loop → drain to the horizon → cancel and join the receive
/// task → one final <see cref="Settle"/>, so a reply that arrived just before the socket closed is
/// still booked instead of being dropped with the queue.</item>
/// </list>
/// <para>
/// The send side is one logical writer rather than one thread id: the arm's send loop is async and
/// resumes on whichever thread completes its socket send. What the contract guarantees is that no
/// second booker runs concurrently with it, and the queue is what makes that a property of the
/// book rather than of the scheduler.
/// </para>
/// </remarks>
internal sealed class MixUdpBook
{
    private readonly UdpReliabilityTracker _tracker;
    private readonly LogHistogram _rtt;
    private readonly ConcurrentQueue<MixUdpSettlement> _settlements = new();
    private readonly Dictionary<ulong, long> _pending = [];
    private int _offerThreadId;
    private int _settleThreadId;

    internal MixUdpBook(UdpReliabilityTracker tracker, LogHistogram rtt)
    {
        _tracker = tracker;
        _rtt = rtt;
    }

    /// <summary>
    /// Requests this socket sent that no reply has consumed yet: the pending book's size, send side
    /// only, and the count the arm's drain waits on.
    /// </summary>
    internal long Pending => _pending.Count;

    /// <summary>Settlements the send side has drained, duplicates and failures included (witness, not published).</summary>
    internal long Settled { get; private set; }

    /// <summary>
    /// <see cref="Settle"/> calls that found the queue non-empty (witness, not published): zero means
    /// the send side never actually drained a settlement while the lane was running, whatever the
    /// totals say.
    /// </summary>
    internal long SettlesUnderLoad { get; private set; }

    /// <summary>
    /// Arrived verdicts the settle path itself resolved against the sent book (witness, not
    /// published): a count below <see cref="Settled"/> would mean some WasSent step was decided
    /// before the settlement — i.e. on the receive side, where the sent book may still be growing.
    /// </summary>
    internal long ResolvedOnSettle { get; private set; }

    /// <summary>
    /// Settlements offered with a verdict that is already resolved (witness, not published).
    /// <see cref="ReplyClassifier.Classify"/> has no WasSent step and never answers
    /// <see cref="ReplyKind.Unmatched"/>, so an unmatched verdict here is proof that the receive side
    /// performed the WasSent step itself — on a sent book the send side may not have finished growing.
    /// </summary>
    internal long OffersAlreadyResolved { get; private set; }

    /// <summary>The thread that last offered a settlement (witness, not published).</summary>
    internal int OfferThreadId => Volatile.Read(ref _offerThreadId);

    /// <summary>The thread that last settled (witness, not published).</summary>
    internal int SettleThreadId => Volatile.Read(ref _settleThreadId);

    /// <summary>Books one offered pacing slot. Send side, before the datagram reaches the socket.</summary>
    internal void BookSupplied() => _tracker.MarkSupplied();

    /// <summary>
    /// Books one datagram handed to the socket and opens its pending slot, in that order: a reply that
    /// arrives before this returns must not be able to see the send as unknown.
    /// </summary>
    internal void BookSent(long sequence, long intendedTicks)
    {
        _tracker.MarkSent(sequence, intendedTicks);
        _pending[(ulong)sequence] = intendedTicks;
    }

    /// <summary>
    /// Books a send the socket refused. The pending slot stays open on purpose: the arm's horizon has
    /// to keep spanning the drain a refused datagram would have needed, so the drain cannot end early
    /// on a sequence the path still owes an answer for.
    /// </summary>
    internal void BookSendRefused(long sequence) => _tracker.MarkSendRefused(sequence);

    /// <summary>Books a socket failure that cost the lane everything it had not yet sent.</summary>
    internal void BookSendFailure() => _tracker.MarkSendFailure();

    /// <summary>
    /// Receive side: enqueue one classified datagram. Nothing here touches a counter or a book, and the
    /// verdict goes in as the classifier left it — resolving it here would answer for a sent book this
    /// side does not own and may not have finished growing.
    /// </summary>
    internal void Offer(in ReplyVerdict verdict, long arrivedTicks)
    {
        LatchOfferThread();
        if (verdict.Kind == ReplyKind.Unmatched)
        {
            // The classifier never produces this kind: only a WasSent step does.
            OffersAlreadyResolved++;
        }

        _settlements.Enqueue(new MixUdpSettlement(MixUdpBooking.Verdict, verdict, arrivedTicks));
    }

    /// <summary>Receive side: the socket failed, so the lane owes an answer about it to the send side.</summary>
    internal void OfferReceiveFailure()
    {
        LatchOfferThread();
        _settlements.Enqueue(new MixUdpSettlement(MixUdpBooking.ReceiveFailed, default, Clock.Now));
    }

    /// <summary>
    /// Drains every settlement offered so far and books it. The contract is to empty the queue, not to
    /// spend a budget: a settle that stopped early would leave a reply for later, and the arm's
    /// identities are computed from what this returns into the books.
    /// </summary>
    /// <returns>How many settlements this call booked.</returns>
    internal long Settle()
    {
        Volatile.Write(ref _settleThreadId, Environment.CurrentManagedThreadId);
        long drained = 0;
        while (_settlements.TryDequeue(out var settlement))
        {
            drained++;
            Book(in settlement);
        }

        Settled += drained;
        if (drained > 0)
        {
            SettlesUnderLoad++;
        }

        return drained;
    }

    private void Book(in MixUdpSettlement settlement)
    {
        switch (settlement.Booking)
        {
            case MixUdpBooking.Verdict:
                Book(settlement.Verdict, settlement.ArrivedTicks);
                break;

            case MixUdpBooking.ReceiveFailed:
                _tracker.MarkSendFailure();
                break;

            default:
                throw new InvalidOperationException($"Unhandled mix udp booking '{settlement.Booking}'.");
        }
    }

    /// <summary>
    /// The reply ladder's booking half, in the order D18.5 #3 pins: the WasSent question first, then
    /// the pending lookup that decides whether the reply consumed a request, then the booking. All
    /// three are the send side's, which is the point of the settlement: the receive side saw the same
    /// datagram but is not allowed to answer for this book.
    /// </summary>
    private void Book(in ReplyVerdict raw, long arrivedTicks)
    {
        var verdict = raw;
        if (verdict.Kind == ReplyKind.Arrived)
        {
            verdict = verdict.Resolve(_tracker.WasSent(verdict.Sequence));
            ResolvedOnSettle++;
            if (verdict.Kind == ReplyKind.Arrived && _pending.Remove((ulong)verdict.Sequence, out var intended))
            {
                // A round trip is measured against arrival, never against the later settle (D18.5 #1).
                _rtt.Record(Clock.ToNanoseconds(arrivedTicks - intended));
            }
        }

        switch (verdict.Kind)
        {
            case ReplyKind.Arrived:
                _tracker.MarkArrival(verdict.Sequence, arrivedTicks, verdict.PayloadBytes);
                break;

            case ReplyKind.Corrupt:
            case ReplyKind.CorruptKnownSequence:
                _tracker.MarkCorruptWithKnownSequence(verdict.Sequence);
                break;

            case ReplyKind.ForeignConnection:
                _tracker.MarkForeignConnection();
                break;

            case ReplyKind.Unmatched:
                _tracker.MarkUnmatchedReply();
                break;

            case ReplyKind.Undecodable:
                _tracker.MarkCorrupt();
                break;

            default:
                // Every verdict the classifier can produce is named above, so a future one has to be
                // wired here rather than booked by whichever side happened to be last.
                throw new InvalidOperationException($"Unhandled reply kind '{verdict.Kind}'.");
        }
    }

    private void LatchOfferThread()
    {
        var thread = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _offerThreadId) != thread)
        {
            Volatile.Write(ref _offerThreadId, thread);
        }
    }
}
