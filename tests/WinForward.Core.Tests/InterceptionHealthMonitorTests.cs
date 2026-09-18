using WinForward.Configuration;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The interception-health monitor's threshold/cooldown/degrade state machine (task 09-17
/// R1-B), driven entirely through a mutable time provider: window counting, the shared
/// cooldown, the three-trigger degrade budget with its single error event and 5-minute
/// spacing, and the <see cref="InterceptionHealthMonitor.NoteRefreshCompleted"/> reset.
/// </summary>
public sealed class InterceptionHealthMonitorTests
{
    private const string Counter = RuntimeCounters.RelaySetupFailed;

    private sealed record Harness(InterceptionHealthMonitor Monitor, MutableTimeProvider Time, RecordingRuntimeLogger Logger, List<string> Triggers)
    {
        public int TriggerCount
        {
            get { lock (Triggers) return Triggers.Count; }
        }
    }

    private static Harness Create(IReadOnlyDictionary<string, int>? thresholds = null)
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var triggers = new List<string>();
        var logger = new RecordingRuntimeLogger();
        var monitor = new InterceptionHealthMonitor(
            logger,
            counter => { lock (triggers) triggers.Add(counter); },
            time,
            thresholds);
        return new Harness(monitor, time, logger, triggers);
    }

    [Fact]
    public void ThresholdCrossingFiresTheTriggerOncePerCooldown()
    {
        var harness = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Counter] = 3 });

        harness.Monitor.ReportFailure(Counter);
        harness.Monitor.ReportFailure(Counter);
        Assert.Equal(0, harness.TriggerCount);

        harness.Monitor.ReportFailure(Counter);
        Assert.Equal(1, harness.TriggerCount);
        Assert.Equal(Counter, harness.Triggers.Single());
        Assert.Equal(1, harness.Monitor.ConsecutiveForcedTriggers);

        // Reports inside the 60 s cooldown keep counting but never re-trigger.
        for (var index = 0; index < 5; index++) harness.Monitor.ReportFailure(Counter);
        Assert.Equal(1, harness.TriggerCount);
    }

    [Fact]
    public void ReportsDuringCooldownCountTowardTheNextTrigger()
    {
        var harness = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Counter] = 3 });
        for (var index = 0; index < 3; index++) harness.Monitor.ReportFailure(Counter);
        Assert.Equal(1, harness.TriggerCount);

        harness.Time.Advance(TimeSpan.FromSeconds(50));
        harness.Monitor.ReportFailure(Counter);
        harness.Monitor.ReportFailure(Counter);
        Assert.Equal(1, harness.TriggerCount);

        // Cooldown expired; the two in-window reports above already count, so the first
        // post-cooldown report re-crosses the threshold immediately.
        harness.Time.Advance(TimeSpan.FromSeconds(11));
        harness.Monitor.ReportFailure(Counter);
        Assert.Equal(2, harness.TriggerCount);
    }

    [Fact]
    public void SlidingWindowDropsReportsOlderThanTheWindow()
    {
        var harness = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Counter] = 3 });
        harness.Monitor.ReportFailure(Counter);
        harness.Monitor.ReportFailure(Counter);

        harness.Time.Advance(TimeSpan.FromSeconds(31));
        harness.Monitor.ReportFailure(Counter);
        Assert.Equal(0, harness.TriggerCount);

        harness.Monitor.ReportFailure(Counter);
        harness.Monitor.ReportFailure(Counter);
        Assert.Equal(1, harness.TriggerCount);
    }

    [Fact]
    public void ThirdConsecutiveTriggerDegradesWithOneErrorEventAndFiveMinuteSpacing()
    {
        var harness = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Counter] = 3 });
        for (var round = 1; round <= 3; round++)
        {
            for (var index = 0; index < 3; index++) harness.Monitor.ReportFailure(Counter);
            Assert.Equal(round, harness.TriggerCount);
            harness.Time.Advance(TimeSpan.FromSeconds(61));
        }

        Assert.True(harness.Monitor.IsDegraded);
        var degraded = Assert.Single(harness.Logger.Events,
            entry => string.Equals(entry.Name, "runner.forcedRefresh.degraded", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Error, degraded.Level);
        Assert.Equal("3", CaptureRunnerHarness.FieldValue(degraded.Fields, "consecutive"));

        // Degraded spacing replaces the 60 s cooldown: a fresh cluster one minute later stays
        // silent, the same cluster past the 5-minute mark triggers again — with no second error.
        for (var index = 0; index < 3; index++) harness.Monitor.ReportFailure(Counter);
        Assert.Equal(3, harness.TriggerCount);

        harness.Time.Advance(TimeSpan.FromSeconds(300));
        for (var index = 0; index < 3; index++) harness.Monitor.ReportFailure(Counter);
        Assert.Equal(4, harness.TriggerCount);
        Assert.Single(harness.Logger.Events,
            entry => string.Equals(entry.Name, "runner.forcedRefresh.degraded", StringComparison.Ordinal));
    }

    [Fact]
    public void NoteRefreshCompletedResetsStreakDegradedStateAndWindows()
    {
        var harness = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Counter] = 3 });
        for (var round = 0; round < 3; round++)
        {
            for (var index = 0; index < 3; index++) harness.Monitor.ReportFailure(Counter);
            harness.Time.Advance(TimeSpan.FromSeconds(61));
        }
        Assert.True(harness.Monitor.IsDegraded);

        harness.Monitor.NoteRefreshCompleted();
        Assert.Equal(0, harness.Monitor.ConsecutiveForcedTriggers);
        Assert.False(harness.Monitor.IsDegraded);

        // Past the surviving cooldown, the cleared window needs a full fresh cluster again —
        // a saturated pre-reset window would have triggered on the first report below.
        harness.Time.Advance(TimeSpan.FromSeconds(400));
        harness.Monitor.ReportFailure(Counter);
        harness.Monitor.ReportFailure(Counter);
        Assert.Equal(3, harness.TriggerCount);
        harness.Monitor.ReportFailure(Counter);
        Assert.Equal(4, harness.TriggerCount);
        Assert.Equal(1, harness.Monitor.ConsecutiveForcedTriggers);
        Assert.False(harness.Monitor.IsDegraded);
        Assert.Single(harness.Logger.Events,
            entry => string.Equals(entry.Name, "runner.forcedRefresh.degraded", StringComparison.Ordinal));
    }

    [Fact]
    public void SuccessfulRefreshBetweenTriggersResetsTheConsecutiveStreak()
    {
        var harness = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Counter] = 3 });
        for (var index = 0; index < 3; index++) harness.Monitor.ReportFailure(Counter);
        Assert.Equal(1, harness.Monitor.ConsecutiveForcedTriggers);

        harness.Monitor.NoteRefreshCompleted();
        harness.Time.Advance(TimeSpan.FromSeconds(61));
        for (var index = 0; index < 3; index++) harness.Monitor.ReportFailure(Counter);

        Assert.Equal(2, harness.TriggerCount);
        Assert.Equal(1, harness.Monitor.ConsecutiveForcedTriggers);
    }

    [Fact]
    public void CooldownIsSharedAcrossCounters()
    {
        var harness = Create(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [RuntimeCounters.UdpOriginUnresolved] = 2,
            [RuntimeCounters.PassReinjectFailed] = 2,
        });

        harness.Monitor.ReportFailure(RuntimeCounters.UdpOriginUnresolved);
        harness.Monitor.ReportFailure(RuntimeCounters.UdpOriginUnresolved);
        Assert.Equal(1, harness.TriggerCount);

        // A different counter crossing its own threshold inside the cooldown stays silent.
        harness.Monitor.ReportFailure(RuntimeCounters.PassReinjectFailed);
        harness.Monitor.ReportFailure(RuntimeCounters.PassReinjectFailed);
        Assert.Equal(1, harness.TriggerCount);

        harness.Time.Advance(TimeSpan.FromSeconds(61));
        harness.Monitor.ReportFailure(RuntimeCounters.PassReinjectFailed);
        harness.Monitor.ReportFailure(RuntimeCounters.PassReinjectFailed);
        Assert.Equal(2, harness.TriggerCount);
    }

    [Fact]
    public void UnknownCountersNeverTriggerAndKeepTheSnapshotEmpty()
    {
        var harness = Create();

        for (var index = 0; index < 100; index++) harness.Monitor.ReportFailure("notATrackedCounter");

        Assert.Equal(0, harness.TriggerCount);
        Assert.Empty(harness.Monitor.WindowSnapshot());
    }

    [Fact]
    public void NoopSignalAcceptsReportsInertly()
    {
        InterceptionHealthMonitor.Noop.ReportFailure(RuntimeCounters.RelaySetupFailed);
        Assert.Same(InterceptionHealthMonitor.Noop, InterceptionHealthMonitor.Noop);
    }

    [Fact]
    public void AttachTriggerRejectsASecondHandler()
    {
        var monitor = new InterceptionHealthMonitor();
        monitor.AttachTrigger(static _ => { });
        Assert.Throws<InvalidOperationException>(() => monitor.AttachTrigger(static _ => { }));
    }
}
