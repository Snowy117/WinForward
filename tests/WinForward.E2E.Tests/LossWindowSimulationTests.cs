using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The client keeps offering its whole schedule when the path drops datagrams (#4). The offer loop is
/// driven through the arm's own admission step (<see cref="LossWindow.Admit"/>) with a fake send path
/// that drops a fixed share, so nothing here touches a socket or a wall clock: the pacing instants are
/// the production <see cref="Pacer"/>'s and the clock is simulated.
/// </summary>
/// <remarks>
/// Counter-proofs — each was applied to the production code and watched this file turn red, and the
/// evidence records the restored hashes (D11):
/// <list type="bullet">
/// <item>delete the <c>Retire</c> call from <see cref="LossWindow.Admit"/>: the 50% case fills the
/// window and stops sending, so <c>sent == supplied</c>, the exact slot count and the overflow
/// assertion all go red (the 0% and 10% cases stay green, which is why all three rates are here: the
/// defect only shows once the drop rate outruns the window).</item>
/// <item>freeze the window by admitting every slot into an in-flight set that never releases
/// (<see cref="NeverRetireWindowTicks"/>): the negative control below asserts the same failure shape
/// on purpose — <c>WindowOverflow &gt; 0</c> with <c>SentOk &lt; Supplied</c>.</item>
/// </list>
/// </remarks>
public sealed class LossWindowSimulationTests
{
    private const int RatePerSecond = 500;
    private const int Seconds = 30;
    // One slot per 1/rate of a second, the first at the start instant, plus the slot whose intended
    // instant lands exactly on the deadline: the arm tests its clock before the pace wait, so that
    // slot is still offered and sent.
    private const int ExpectedSlots = (RatePerSecond * Seconds) + 1;
    private const int Window = 4096;
    private const int PayloadBytes = 200;
    private const double RoundTripSeconds = 0.001;
    private const int Seed = 20261007;

    /// <summary>
    /// A window no real clock reaches, and deliberately not <see cref="long.MaxValue"/>: that value
    /// makes <c>sendTicks + windowTicks</c> wrap negative, which reads as "every slot is already
    /// expired" and retires the whole window at once — the opposite of freezing it.
    /// </summary>
    private const long NeverRetireWindowTicks = long.MaxValue / 2;

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.10)]
    [InlineData(0.50)]
    public void TheWholeScheduleIsSentWhateverThePathDrops(double lossRate)
    {
        var scenario = Simulate(lossRate, WindowMilliseconds(), Window);
        var diagnostic =
            $"loss {lossRate:P0}: supplied {scenario.Supplied} (expected {ExpectedSlots}), sent {scenario.Sent}, overflow {scenario.WindowOverflow}, dropped {scenario.Dropped}, delivered {scenario.Delivered}, first slot sent {scenario.FirstSlotSent}, last slot sent {scenario.LastSlotSent}, arrived {scenario.Arrived}, never {scenario.Never}, undetermined {scenario.Undetermined}";

        Assert.True(scenario.Supplied == ExpectedSlots, diagnostic);
        Assert.True(scenario.Sent == scenario.Supplied, diagnostic);
        Assert.True(scenario.WindowOverflow == 0, diagnostic);

        // "Sent the whole way through", stated as a slot rather than as a total: the last schedule
        // index reached the socket, so the client was still sending when the arm ended.
        Assert.True(scenario is { FirstSlotSent: true, LastSlotSent: true }, diagnostic);

        // One classification each: what the fake path dropped is path loss, what it delivered arrived,
        // and nothing is left unresolved at the horizon.
        Assert.True(scenario.Arrived == scenario.Delivered, diagnostic);
        Assert.True(scenario.Never == scenario.Dropped, diagnostic);
        Assert.True(scenario is { Late: 0, Undetermined: 0 }, diagnostic);
    }

    [Fact]
    public void AWindowThatNeverReleasesStopsTheClientAndSaysSo()
    {
        // Negative control for the two facts above: with the same fake path, a window that never lets a
        // slot go fills up, the offer loop then drops slots as client send loss, and the arm stops
        // reaching the end of its schedule. This is the shape the audit described, produced on purpose
        // from the one input that can produce it.
        var scenario = Simulate(0.50, NeverRetireWindowTicks, Window);
        var diagnostic =
            $"frozen window: supplied {scenario.Supplied}, sent {scenario.Sent}, overflow {scenario.WindowOverflow}, last slot sent {scenario.LastSlotSent}, undetermined {scenario.Undetermined}";

        Assert.True(scenario.Supplied == ExpectedSlots, diagnostic);
        Assert.True(scenario.WindowOverflow > 0, diagnostic);
        Assert.True(scenario.Sent < scenario.Supplied, diagnostic);
        Assert.False(scenario.LastSlotSent, diagnostic);
    }

    private static long WindowMilliseconds() => UdpLossMath.WindowTicks(UdpLossMath.DefaultWindowMilliseconds);

    /// <summary>
    /// Drives the arm's offer loop over a simulated clock. The send path is the fake: an admitted slot
    /// is "handed to the socket" (recorded) and its reply is dropped with probability
    /// <paramref name="lossRate"/>, which is the only fault this simulation injects.
    /// </summary>
    private static Scenario Simulate(double lossRate, long windowTicks, int window)
    {
        var tracker = new UdpReliabilityTracker();
        var pacer = new Pacer(RatePerSecond, startTicks: 0);
        var deadlineTicks = Clock.FromSeconds(Seconds);
        var roundTripTicks = Clock.FromSeconds(RoundTripSeconds);
        var random = new Random(Seed);
        var sent = new bool[ExpectedSlots + 1];
        var now = 0L;
        var lastSendTicks = 0L;
        long index = 0;
        long dropped = 0;
        long delivered = 0;

        while (now < deadlineTicks)
        {
            var intended = pacer.IntendedTicks(index);
            // Pacer.WaitUntil(intended): the loop never runs before the instant it asked for.
            now = Math.Max(now, intended);
            index++;
            if (!LossWindow.Admit(tracker, now, windowTicks, window))
            {
                continue;
            }

            sent[index] = true;
            lastSendTicks = now;
            tracker.MarkSent(index, intended);
            if (random.NextDouble() < lossRate)
            {
                dropped++;
                continue;
            }

            delivered++;
            tracker.MarkArrival(index, now + roundTripTicks, PayloadBytes);
        }

        // The arm's horizon: the drain runs to W after the last real send, so a dropped datagram is
        // classified against the whole window it was owed.
        var counts = tracker.Classify(windowTicks, lastSendTicks + windowTicks);
        return new Scenario(
            tracker.Supplied,
            tracker.SentOk,
            tracker.WindowOverflow,
            dropped,
            delivered,
            sent[1],
            sent[ExpectedSlots],
            counts.Arrived,
            counts.Late,
            counts.Never,
            counts.Undetermined);
    }

    private sealed record Scenario(
        long Supplied,
        long Sent,
        long WindowOverflow,
        long Dropped,
        long Delivered,
        bool FirstSlotSent,
        bool LastSlotSent,
        long Arrived,
        long Late,
        long Never,
        long Undetermined);
}
