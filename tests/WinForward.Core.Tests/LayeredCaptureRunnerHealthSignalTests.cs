using WinForward.Configuration;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The capture runner's interception-health integration (task 09-17 R1-B): failure reports
/// crossing a threshold arm a forced refresh demand through the existing task-09-11 forced
/// semantics — a rebuild runs even against an identical enumeration, logs
/// <c>adapter.refresh forced=true</c> (never <c>noop</c>), and the completed install resets
/// the monitor through <see cref="InterceptionHealthMonitor.NoteRefreshCompleted"/>.
/// </summary>
public sealed class LayeredCaptureRunnerHealthSignalTests
{
    [Fact]
    public async Task FailureThresholdForcesARefreshDespiteIdenticalEnumeration()
    {
        await using var harness = new CaptureRunnerHarness(
            [CaptureRunnerFakes.AdapterItem("id-a", 101)],
            CaptureRunnerFakes.UnconstrainedPolicy(),
            minimumRefreshInterval: TimeSpan.FromMilliseconds(50));
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        // Three relay-setup failures inside the 30 s window cross the default threshold. The
        // enumeration never changes below, so the rebuild can only come from the forced flag.
        var signal = harness.Runner.HealthSignal;
        signal.ReportFailure(RuntimeCounters.RelaySetupFailed);
        signal.ReportFailure(RuntimeCounters.RelaySetupFailed);
        signal.ReportFailure(RuntimeCounters.RelaySetupFailed);

        await AsyncTestExtensions.WaitForAsync(() => harness.Generations.Generations.Count == 2, timeoutMs: 5000).ConfigureAwait(false);
        await harness.WaitForGenerationStartedAsync(1).ConfigureAwait(false);

        Assert.False(harness.RunTask.IsCompleted);
        var refresh = harness.RefreshEvents[^1];
        Assert.Equal("true", CaptureRunnerHarness.FieldValue(refresh, "forced"));
        Assert.Null(CaptureRunnerHarness.FieldValue(refresh, "noop"));
        Assert.Equal([101], harness.Generation(1).Scope.Select(item => item.Adapter.RuntimeHandle).ToArray());
        Assert.Equal(1, harness.Generation(0).DisposeCount);
        Assert.Equal(0, harness.DurableDisposeCount);
        var (level, _, fields) = Assert.Single(harness.Logger.Events, entry =>
            string.Equals(entry.Name, "runner.forcedRefresh", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Warn, level);
        Assert.Equal(RuntimeCounters.RelaySetupFailed, CaptureRunnerHarness.FieldValue(fields, "reason"));
        Assert.Equal("1", CaptureRunnerHarness.FieldValue(fields, "consecutive"));

        // The forced install completed, so the demand-processing success hook already reset
        // the monitor's consecutive streak.
        var monitor = Assert.IsType<InterceptionHealthMonitor>(signal);
        Assert.Equal(0, monitor.ConsecutiveForcedTriggers);
        Assert.False(monitor.IsDegraded);
    }

    [Fact]
    public async Task ReportsBelowTheThresholdNeverRaiseADemand()
    {
        await using var harness = new CaptureRunnerHarness(
            [CaptureRunnerFakes.AdapterItem("id-a", 101)],
            CaptureRunnerFakes.UnconstrainedPolicy());
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        harness.Runner.HealthSignal.ReportFailure(RuntimeCounters.RelaySetupFailed);
        harness.Runner.HealthSignal.ReportFailure(RuntimeCounters.RelaySetupFailed);
        await Task.Delay(200).ConfigureAwait(false);

        Assert.Single(harness.Generations.Generations);
        Assert.Empty(harness.RefreshEvents);
        Assert.DoesNotContain(harness.Logger.Events, entry => string.Equals(entry.Name, "runner.forcedRefresh", StringComparison.Ordinal));
        Assert.False(harness.RunTask.IsCompleted);
    }
}
