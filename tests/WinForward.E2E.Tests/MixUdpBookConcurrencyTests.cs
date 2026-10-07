using System.Collections.Concurrent;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The MIX lane's two-sided book (D19.3 F): the receive task only offers a settlement carrying the
/// classifier's <b>unresolved</b> verdict and the instant the datagram arrived, the send side answers
/// <c>WasSent</c>, removes the pending slot, samples the round trip and books the arrival — and the
/// identity <c>delivered == sent == settled</c> holds with no reply lost and none booked twice.
/// </summary>
/// <remarks>
/// <para>
/// The two roles are pinned to real threads on purpose. The production lane's send loop is async and
/// resumes on whichever thread completed its socket send, which is why no assertion out there may rest
/// on a thread id; here the send role runs synchronously on this thread and the receive role on a
/// thread of its own, so "both sides really ran, and they are not the same side" is a fact of the run.
/// </para>
/// <para>
/// Counter-proofs — each was applied to the production code and watched this file turn red, and the
/// evidence records the restored hashes (D11):
/// </para>
/// <list type="bullet">
/// <item>make <see cref="MixUdpBook.Offer"/> book the arrival itself (<c>tracker.MarkArrival</c>)
/// instead of enqueuing: nothing is ever settled, so <c>Settled</c>, <c>SettlesUnderLoad</c>, the
/// pending book and the round-trip count are red while <c>delivered</c> and <c>sent</c> still agree —
/// which is why the settlement count, not the tracker totals, is what this file counts.</item>
/// <item>make <see cref="MixUdpBook.Offer"/> resolve the verdict (<c>WasSent</c> plus the pending
/// removal) and enqueue it resolved, the way the pre-contract code did it inline on the receive
/// thread: in <see cref="AReplyThatBeatsItsOwnSendIsStillResolvedByTheSendSide"/> the first reply is
/// offered before its own send was booked, so the early WasSent answers "never sent":
/// <c>ResolvedOnSettle</c> comes up short and the ladder books an unmatched reply.</item>
/// <item>hand <see cref="MixUdpBook.Offer"/> a verdict that is already resolved:
/// <see cref="ReplyClassifier.Classify"/> never answers <c>Unmatched</c>, so
/// <c>OffersAlreadyResolved</c> counts it.</item>
/// </list>
/// </remarks>
public sealed class MixUdpBookConcurrencyTests
{
    private const int Rounds = 1000;
    private const int DatagramsPerRound = 8;
    private const int PayloadBytes = MixUdpLoop.UdpPayloadBytes;
    private const uint ConnectionId = 0x4D55_0007u;
    private static readonly TimeSpan s_budget = TimeSpan.FromSeconds(10);

    [Fact]
    public void EveryDatagramOfferedByTheReceiveThreadIsSettledExactlyOnceByTheSendSide()
    {
        for (var round = 1; round <= Rounds; round++)
        {
            RunRound(DatagramsPerRound, replyFirst: false, $"round {round} of {Rounds}");
        }
    }

    [Fact]
    public void AReplyThatBeatsItsOwnSendIsStillResolvedByTheSendSide()
    {
        for (var round = 1; round <= Rounds; round++)
        {
            RunRound(DatagramsPerRound, replyFirst: true, $"round {round} of {Rounds}");
        }
    }

    private static void RunRound(int count, bool replyFirst, string round)
    {
        var sendThread = Environment.CurrentManagedThreadId;
        var tracker = new UdpReliabilityTracker();
        var rtt = new LogHistogram();
        var book = new MixUdpBook(tracker, rtt);
        var replies = new BlockingCollection<long>(new ConcurrentQueue<long>(), DatagramsPerRound);
        // Completion sources rather than reset events: the receive thread signals them, so nothing
        // here may be disposed while that thread could still be running.
        var firstOffer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allOffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var datagram = new byte[FrameCodec.HeaderSize + PayloadBytes + FrameCodec.TrailerSize];
        long delivered = 0;
        var receiveThreadId = 0;

        var receive = new Thread(() =>
        {
            Volatile.Write(ref receiveThreadId, Environment.CurrentManagedThreadId);
            // The token lives and dies with this thread: a token owned by the round could be disposed
            // while the thread still waits on it.
            using var stop = new CancellationTokenSource(s_budget);
            try
            {
                foreach (var sequence in replies.GetConsumingEnumerable(stop.Token))
                {
                    // The production receive side in one statement: build the reply's bytes, classify them,
                    // offer the classifier's own verdict with the instant it arrived.
                    Filler.Fill(ConnectionId, (ulong)sequence, datagram.AsSpan(FrameCodec.HeaderSize, PayloadBytes));
                    var length = FrameCodec.WriteFrameInPlace(datagram, ConnectionId, (ulong)sequence, (ulong)sequence, PayloadBytes);
                    book.Offer(ReplyClassifier.Classify(datagram.AsSpan(0, length), ConnectionId), Clock.Now);

                    var seen = Interlocked.Increment(ref delivered);
                    if (seen == 1)
                    {
                        firstOffer.TrySetResult();
                    }

                    if (seen == count)
                    {
                        allOffered.TrySetResult();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The budget ended this wait. A BlockingCollection signals a cancelled consuming token by
                // throwing out of the enumeration rather than by ending it, and an unhandled throw on a
                // thread of its own takes the whole test host down — killing the run and losing the
                // results of every test that had not reported yet. A round that reaches this catch is one
                // an assertion already abandoned before CompleteAdding, so there is nothing left to do
                // here but stop; the failure itself is reported by the test thread.
            }
        })
        { IsBackground = true, Name = "mix-udp-book-receive" };
        receive.Start();

        long settledInLoop = 0;
        for (var sequence = 1; sequence <= count; sequence++)
        {
            if (replyFirst)
            {
                // The reply is on the wire before the send side booked its own slot: the race the
                // settlement exists for. The first one is staged so it is not left to the scheduler.
                replies.Add(sequence);
                if (sequence == 1)
                {
                    Assert.True(firstOffer.Task.Wait(s_budget), $"the receive thread never offered a settlement — {round}");
                }

                book.BookSupplied();
                book.BookSent(sequence, Clock.Now);
            }
            else
            {
                book.BookSupplied();
                book.BookSent(sequence, Clock.Now);
                replies.Add(sequence);
                if (sequence == 1)
                {
                    // Stage the overlap instead of hoping for it (test-stability 2.5): by the time the
                    // first pacing point settles, a settlement is already in the queue, so "a settle that
                    // found the queue non-empty" is a fact of this run rather than of the host's mood.
                    Assert.True(firstOffer.Task.Wait(s_budget), $"the receive thread never offered a settlement — {round}");
                }
            }

            settledInLoop += book.Settle();
        }

        Assert.True(allOffered.Task.Wait(s_budget), $"the receive thread never offered every datagram — {round}");
        var settledFinally = book.Settle();
        replies.CompleteAdding();
        Assert.True(receive.Join(s_budget), $"the receive thread did not stop — {round}");

        var counts = tracker.Classify(UdpLossMath.WindowTicks(UdpLossMath.DefaultWindowMilliseconds), Clock.Now);
        var diagnostic =
            $"{round} (reply first {replyFirst}): settled {book.Settled} (offer loop {settledInLoop} + final {settledFinally}), settled under load {book.SettlesUnderLoad}, resolved on settle {book.ResolvedOnSettle}, offered already resolved {book.OffersAlreadyResolved}, offered {delivered}, sent {count}, pending {book.Pending}, outstanding {tracker.Outstanding}, arrived {counts.Arrived}, late {counts.Late}, never {counts.Never}, undetermined {counts.Undetermined}, duplicate {counts.Duplicate}, reordered {counts.Reordered}, unmatched {tracker.UnmatchedReplies}, corrupt {counts.Corrupt}, received {tracker.ReceivedDatagrams}, rtt samples {rtt.Count}, out of range {tracker.OutOfRange}, offer thread {book.OfferThreadId} (receive {receiveThreadId}), settle thread {book.SettleThreadId} (send {sendThread})";

        Assert.True(delivered == count, diagnostic);
        Assert.True(book.Settled == count, diagnostic);
        Assert.True(settledInLoop + settledFinally == count, diagnostic);
        Assert.True(book.SettlesUnderLoad >= 1, diagnostic);
        Assert.True(book.ResolvedOnSettle == count, diagnostic);
        Assert.True(book.OffersAlreadyResolved == 0, diagnostic);
        Assert.True(book.Pending == 0, diagnostic);
        Assert.True(tracker.Outstanding == 0, diagnostic);
        Assert.True(counts.Arrived == count, diagnostic);
        Assert.True(counts is { Late: 0, Never: 0, Undetermined: 0, CorruptDatagrams: 0 }, diagnostic);
        Assert.True(counts is { Duplicate: 0, Reordered: 0, Corrupt: 0 }, diagnostic);
        Assert.True(tracker.UnmatchedReplies == 0, diagnostic);
        Assert.True(tracker.ReceivedDatagrams == count, diagnostic);
        Assert.True(tracker.OutOfRange == 0, diagnostic);

        // Every reply consumed exactly one pending slot, which is what "the pending removal is the send
        // side's" means for the round-trip sample: one sample per datagram, none for a duplicate.
        Assert.True(rtt.Count == count, diagnostic);

        Assert.True(book.OfferThreadId == receiveThreadId, diagnostic);
        Assert.True(book.OfferThreadId != book.SettleThreadId, diagnostic);
        Assert.True(book.SettleThreadId == sendThread, diagnostic);
    }
}
