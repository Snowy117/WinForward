using System.Diagnostics;
using WinForward.E2E.Client;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The reordering count over every arrival order of the first six sequences. RFC 4737 counts a
/// datagram as reordered when it arrives after a higher sequence has already arrived, so each order's
/// expectation is the number of its own descents below a running maximum — computed here rather than
/// tabulated, which turns the spot case into a formula that all 720 orders are checked against.
/// </summary>
/// <remarks>
/// Counter-proof (applied and watched red): replace the <c>_highestArrived</c> comparison in
/// <c>UdpReliabilityTracker.MarkArrival</c> with a comparison against the next expected sequence, and
/// every unordered-looking case here collapses to zero while <c>(1,3,2)</c> stays red at its
/// single-case assertion.
/// </remarks>
public sealed class UdpArrivalOrderTests
{
    private static readonly long s_windowTicks = UdpLossMath.WindowTicks(UdpLossMath.DefaultWindowMilliseconds);
    private static readonly int[] s_sequences = [1, 2, 3, 4, 5, 6];

    [Fact]
    public void EveryArrivalOrderOfAllSixSequencesCountsExactlyItsOwnDescents()
    {
        var orders = 0;

        foreach (var order in OrderedSelections(s_sequences, s_sequences.Length))
        {
            orders++;
            AssertOrderCounts(order, Descents(order), never: 0);
        }

        Assert.Equal(720, orders);
    }

    [Fact]
    public void TheAuditArrivalPopulationIs1956OrderedSelections()
    {
        // 1956 arrival orders, spelled out: every ordered selection of k distinct sequences out of the
        // six, for k = 1..6 (6 + 30 + 120 + 360 + 720 + 720 = 1956). The full permutations are the 720
        // above; the shorter sequences are the same fact for a schedule that stopped arriving early, and
        // both populations are checked against the same formula.
        var sequences = 0;

        for (var length = 1; length <= s_sequences.Length; length++)
        {
            foreach (var order in OrderedSelections(s_sequences, length))
            {
                sequences++;
                AssertOrderCounts(order, Descents(order), never: s_sequences.Length - length);
            }
        }

        Assert.Equal(1956, sequences);
    }

    [Fact]
    public void AnAscendingArrivalOrderIsNeverReordered()
    {
        AssertOrderCounts([1, 2, 3, 4, 5, 6], reordered: 0, never: 0);
    }

    [Fact]
    public void ArrivingOneThreeTwoIsOneReorder()
    {
        // The counted case, on its own: 3 arrives first, 1 arrives after a higher sequence and counts,
        // 2 arrives after the same maximum and counts nothing new.
        AssertOrderCounts([1, 3, 2], reordered: 1, never: 3);
    }

    private static void AssertOrderCounts(int[] arrivalOrder, int reordered, int never)
    {
        var tracker = new UdpReliabilityTracker();
        for (var sequence = 1; sequence <= s_sequences.Length; sequence++)
        {
            tracker.MarkSent(sequence, sendTicks: Milliseconds(sequence));
        }

        foreach (var sequence in arrivalOrder)
        {
            tracker.MarkArrival(sequence, nowTicks: Milliseconds(sequence), payloadBytes: 8);
        }

        var counts = tracker.Classify(s_windowTicks, observationEndTicks: Milliseconds(10_000));
        var diagnostic = $"arrival order [{string.Join(",", arrivalOrder)}]: reordered {counts.Reordered} (expected {reordered}), arrived {counts.Arrived}, late {counts.Late}, never {counts.Never}, undetermined {counts.Undetermined}, duplicate {counts.Duplicate}";

        Assert.True(counts.Reordered == reordered, diagnostic);
        Assert.True(counts.Arrived == arrivalOrder.Length, diagnostic);
        Assert.True(counts.Never == never, diagnostic);
        Assert.True(counts is { Late: 0, Undetermined: 0, Duplicate: 0, CorruptDatagrams: 0 }, diagnostic);
        Assert.True(counts.Arrived + counts.Never == s_sequences.Length, diagnostic);
    }

    private static int Descents(int[] arrivalOrder)
    {
        var highest = 0;
        var reordered = 0;
        foreach (var sequence in arrivalOrder)
        {
            if (sequence < highest)
            {
                reordered++;
            }
            else
            {
                highest = sequence;
            }
        }

        return reordered;
    }

    /// <summary>
    /// Every ordered selection of <paramref name="length"/> distinct values out of
    /// <paramref name="values"/>, which is the permutations for the full length and the shorter
    /// arrival sequences below it.
    /// </summary>
    private static IEnumerable<int[]> OrderedSelections(int[] values, int length)
    {
        if (length == 0)
        {
            yield return [];
            yield break;
        }

        for (var index = 0; index < values.Length; index++)
        {
            var remainder = new int[values.Length - 1];
            Array.Copy(values, 0, remainder, 0, index);
            Array.Copy(values, index + 1, remainder, index, values.Length - index - 1);
            foreach (var tail in OrderedSelections(remainder, length - 1))
            {
                var selection = new int[length];
                selection[0] = values[index];
                tail.CopyTo(selection, 1);
                yield return selection;
            }
        }
    }

    private static long Milliseconds(double milliseconds) => (long)(milliseconds / 1000.0 * Stopwatch.Frequency);
}
