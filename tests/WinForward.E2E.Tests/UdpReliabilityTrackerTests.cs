using System.Diagnostics;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using Xunit;

namespace WinForward.E2E.Tests;

public sealed class UdpReliabilityTrackerTests
{
    private static readonly long s_windowTicks = UdpLossMath.WindowTicks(UdpLossMath.DefaultWindowMilliseconds);

    [Fact]
    public void EverySentDatagramLandsInExactlyOneClassificationBucket()
    {
        var tracker = new UdpReliabilityTracker();
        const int sent = 10;
        for (var sequence = 1; sequence <= sent; sequence++)
        {
            tracker.MarkSupplied();
            tracker.MarkSent(sequence, sendTicks: 0);
        }

        tracker.MarkArrival(1, nowTicks: Milliseconds(50), payloadBytes: 8);
        tracker.MarkArrival(2, nowTicks: Milliseconds(500), payloadBytes: 8);
        tracker.MarkCorruptWithKnownSequence(3);

        var counts = tracker.Classify(s_windowTicks, observationEndTicks: Milliseconds(1_000));

        Assert.Equal(sent, counts.Arrived + counts.Late + counts.Never + counts.Undetermined + counts.CorruptDatagrams);
        Assert.Equal(1, counts.Arrived);
        Assert.Equal(1, counts.Late);
        Assert.Equal(1, counts.CorruptDatagrams);
        Assert.Equal(7, counts.Never);
        Assert.Equal(0, counts.Undetermined);
        Assert.Equal(sent, tracker.SentOk);
        Assert.Equal(0, tracker.Outstanding);
        Assert.Equal(0, tracker.OutOfRange);
    }

    [Fact]
    public void ADatagramWhoseWindowHasNotElapsedIsUndeterminedNotLost()
    {
        var tracker = new UdpReliabilityTracker();
        tracker.MarkSupplied();
        tracker.MarkSent(1, sendTicks: 0);
        tracker.MarkSupplied();
        tracker.MarkSent(2, sendTicks: Milliseconds(10));

        var counts = tracker.Classify(s_windowTicks, observationEndTicks: Milliseconds(20));

        Assert.Equal(2, counts.Undetermined);
        Assert.Equal(0, counts.Never);
        Assert.Equal(0, counts.Arrived + counts.Late + counts.CorruptDatagrams);
        Assert.Equal(2, tracker.Outstanding);
    }

    [Fact]
    public void ARefusedSendLeavesTheClassifiedPopulation()
    {
        var tracker = new UdpReliabilityTracker();
        tracker.MarkSupplied();
        tracker.MarkSent(1, sendTicks: 0);

        tracker.MarkSendRefused(1);

        var counts = tracker.Classify(s_windowTicks, observationEndTicks: 0);
        Assert.Equal(1, tracker.Supplied);
        Assert.Equal(0, tracker.SentOk);
        Assert.Equal(0, tracker.Outstanding);
        Assert.Equal(1, tracker.SendFailure);
        Assert.Equal(0, counts.Arrived + counts.Late + counts.Never + counts.Undetermined + counts.CorruptDatagrams);
    }

    [Fact]
    public void HalfThePopulationArrivingHalfMissingStillAddsUp()
    {
        var tracker = new UdpReliabilityTracker();
        const int sent = 500;
        for (var sequence = 1; sequence <= sent; sequence++)
        {
            tracker.MarkSupplied();
            tracker.MarkSent(sequence, sendTicks: Milliseconds(sequence * 0.1));
        }

        for (var sequence = 1; sequence <= sent; sequence += 2)
        {
            tracker.MarkArrival(sequence, nowTicks: Milliseconds((sequence * 0.1) + 5), payloadBytes: 16);
        }

        var counts = tracker.Classify(s_windowTicks, observationEndTicks: Milliseconds(10_000));

        Assert.Equal(sent, counts.Arrived + counts.Late + counts.Never + counts.Undetermined + counts.CorruptDatagrams);
        Assert.Equal(250, counts.Arrived);
        Assert.Equal(0, counts.Late);
        Assert.Equal(250, counts.Never);
        Assert.Equal(0, tracker.Outstanding);
    }

    [Fact]
    public void ReorderedArrivalsAreCountedOnceEach()
    {
        var tracker = new UdpReliabilityTracker();
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            tracker.MarkSent(sequence, sendTicks: 0);
        }

        tracker.MarkArrival(3, nowTicks: Milliseconds(1), payloadBytes: 8);
        tracker.MarkArrival(1, nowTicks: Milliseconds(2), payloadBytes: 8);
        tracker.MarkArrival(3, nowTicks: Milliseconds(3), payloadBytes: 8);

        var counts = tracker.Classify(s_windowTicks, observationEndTicks: Milliseconds(10));
        Assert.Equal(2, counts.Arrived);
        Assert.Equal(1, counts.Reordered);
        Assert.Equal(1, counts.Duplicate);
        // A replayed datagram is booked as a duplicate and nothing else: it is not a second
        // received datagram in the accounting the arm publishes.
        Assert.Equal(2, tracker.ReceivedDatagrams);
    }

    // The receiver side is the untrusted one: a single flipped byte in a datagram can name
    // sequence 2^63. The bitmap must refuse it instead of indexing the arrays, and the refusal has
    // to be visible as OutOfRange rather than silently dropping the arrival.
    [Fact]
    public void ACorruptDatagramNamingAnImpossibleSequenceIsRefusedAndCounted()
    {
        var tracker = new UdpReliabilityTracker();

        tracker.MarkCorruptWithKnownSequence(long.MaxValue);

        var counts = tracker.Classify(s_windowTicks, observationEndTicks: 0);
        Assert.Equal(1, tracker.OutOfRange);
        Assert.Equal(1, counts.Corrupt);
        Assert.Equal(1, counts.Duplicate);
        Assert.Equal(0, tracker.ReceivedDatagrams);
        Assert.Equal(0, counts.CorruptDatagrams);
    }

    // The sending-side contract: a sequence outside the bounded space is refused, never indexed.
    // Ensure does not grow the parallel arrays past MaxSequence + 1 elements, so "the call returned"
    // is itself the evidence that neither array was written; reading the private fields back would
    // test the test rather than the contract. The refusal is booked on the send side only, which is
    // what tells it apart from an arrival naming an impossible sequence.
    [Fact]
    public void ASendPastTheBoundedSequenceSpaceIsRefusedWithoutTouchingTheArrays()
    {
        var tracker = new UdpReliabilityTracker();

        tracker.MarkSent(UdpReliabilityTracker.MaxSequence + 1, sendTicks: 0);

        Assert.Equal(1, tracker.SentOutOfRange);
        Assert.Equal(0, tracker.OutOfRange);
        Assert.Equal(0, tracker.SentOk);
        Assert.Equal(0, tracker.Outstanding);
        Assert.Equal(0, tracker.Supplied);

        var counts = tracker.Classify(s_windowTicks, observationEndTicks: 0);
        Assert.Equal(0, counts.Arrived + counts.Late + counts.Never + counts.Undetermined + counts.CorruptDatagrams);
    }

    [Fact]
    public void AnArrivalNamingAnImpossibleSequenceIsRefusedAndCounted()
    {
        var tracker = new UdpReliabilityTracker();

        tracker.MarkArrival(long.MaxValue, nowTicks: 0, payloadBytes: 0);
        tracker.MarkArrival(-1, nowTicks: 0, payloadBytes: 0);
        tracker.MarkArrival(UdpReliabilityTracker.MaxSequence + 1, nowTicks: 0, payloadBytes: 0);

        var counts = tracker.Classify(s_windowTicks, observationEndTicks: 0);
        Assert.Equal(3, tracker.OutOfRange);
        Assert.Equal(3, counts.Duplicate);
        Assert.Equal(0, tracker.ReceivedDatagrams);
        Assert.Equal(0, tracker.ReceivedBytes);
    }

    // A schedule index the window refused was never handed to the socket, so it cannot be published
    // as a datagram that never arrived. The scenario puts skipped indices *below* the highest sent one,
    // which is what makes Classify's sent-set guard observable: without it the scan would walk to the
    // highest sent sequence and report the skipped indices as path loss (5 never instead of 2).
    [Fact]
    public void AnIndexTheWindowRefusedIsNeverClassifiedAsLost()
    {
        var tracker = new UdpReliabilityTracker();
        const int window = 1;

        Assert.True(LossWindow.Admit(tracker, nowTicks: 0, s_windowTicks, window));
        tracker.MarkSent(1, sendTicks: 0);

        // The window is full: indices 2..4 are offered while index 1 is still inside its W.
        for (var index = 2; index <= 4; index++)
        {
            Assert.False(LossWindow.Admit(tracker, nowTicks: 0, s_windowTicks, window));
        }

        // Index 1's W elapses, so index 5 is admitted and sent: the refused indices now sit below the
        // highest sent sequence.
        var later = s_windowTicks + 1;
        Assert.True(LossWindow.Admit(tracker, later, s_windowTicks, window));
        tracker.MarkSent(5, sendTicks: later);

        var counts = tracker.Classify(s_windowTicks, observationEndTicks: (2 * s_windowTicks) + 2);

        // Precondition: there really are refused slots in this population.
        Assert.True(tracker.WindowOverflow > 0);

        Assert.Equal(5, tracker.Supplied);
        Assert.Equal(2, tracker.SentOk);
        Assert.Equal(3, tracker.WindowOverflow);

        // The only two never-classified datagrams are the two that were sent.
        Assert.Equal(2, counts.Never);
        Assert.Equal(0, counts.Undetermined);
        Assert.Equal(tracker.SentOk, counts.Arrived + counts.Late + counts.Never + counts.Undetermined + counts.CorruptDatagrams);
    }

    // A datagram's age runs from the instant it was *scheduled* for, never from the instant the
    // client got around to sending it, so a client that stalls raises its own late and never counts
    // instead of having the stall subtracted out of the measurement.
    [Fact]
    public void AClientStallIsCountedAsLateAndNeverRatherThanSubtracted()
    {
        var tracker = new UdpReliabilityTracker();

        // Both datagrams were wanted at instant 0. The client then stalls: the first reaches the wire
        // long after its W and the second only after the observation ended.
        tracker.MarkSent(1, sendTicks: 0);
        tracker.MarkSent(2, sendTicks: 0);
        tracker.MarkArrival(1, nowTicks: 3 * s_windowTicks, payloadBytes: 8);

        var counts = tracker.Classify(s_windowTicks, observationEndTicks: s_windowTicks + 1);

        Assert.Equal(1, counts.Late);
        Assert.Equal(1, counts.Never);
        Assert.Equal(0, counts.Arrived);
        Assert.Equal(0, counts.Undetermined);
    }

    [Fact]
    public void TheBitmapAcceptsTheLastSequenceAndRefusesEverythingBeyondIt()
    {
        var bitmap = new SequenceBitmap();

        Assert.True(bitmap.TrySet(UdpReliabilityTracker.MaxSequence));
        Assert.False(bitmap.TrySet(UdpReliabilityTracker.MaxSequence));
        Assert.True(bitmap.IsSet(UdpReliabilityTracker.MaxSequence));
        Assert.False(bitmap.TrySet(UdpReliabilityTracker.MaxSequence + 1));
        Assert.False(bitmap.TrySet(-1));
        Assert.False(bitmap.IsSet(UdpReliabilityTracker.MaxSequence + 1));
        Assert.Equal(2, bitmap.OutOfRange);
        Assert.True(bitmap.TryClear(UdpReliabilityTracker.MaxSequence));
        Assert.False(bitmap.IsSet(UdpReliabilityTracker.MaxSequence));
        Assert.False(bitmap.TryClear(UdpReliabilityTracker.MaxSequence));
    }

    private static long Milliseconds(double milliseconds) => (long)(milliseconds / 1000.0 * Stopwatch.Frequency);
}
