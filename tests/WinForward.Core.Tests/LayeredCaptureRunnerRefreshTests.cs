using WinForward.Configuration;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class LayeredCaptureRunnerRefreshTests
{
    [Fact]
    public async Task RefreshRebuildsGenerationOnFreshHandlesWithoutDisposingDurable()
    {
        await using var harness = new CaptureRunnerHarness([CaptureRunnerFakes.AdapterItem("id-a", 101)], CaptureRunnerFakes.UnconstrainedPolicy());
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        harness.Enumeration.SetAdapters(CaptureRunnerFakes.AdapterItem("id-a", 202));
        harness.ChangeSource.Trigger();
        await AsyncTestExtensions.WaitForAsync(() => harness.Generations.Generations.Count == 2).ConfigureAwait(false);
        await harness.WaitForGenerationStartedAsync(1).ConfigureAwait(false);

        Assert.Equal([202], harness.Generation(1).Scope.Select(item => item.Adapter.RuntimeHandle).ToArray());
        Assert.Contains("id-a", CaptureRunnerHarness.FieldValue(harness.RefreshEvents[^1], "changed"), StringComparison.Ordinal);
        Assert.Equal(1, harness.Generation(0).DisposeCount);
        Assert.Equal(0, harness.Generation(1).DisposeCount);
        Assert.Equal(0, harness.DurableDisposeCount);
        Assert.False(harness.RunTask.IsCompleted);
        lock (harness.InstalledScopes)
        {
            Assert.Equal(2, harness.InstalledScopes.Count);
            Assert.Equal([101], harness.InstalledScopes[0].Select(item => item.Adapter.RuntimeHandle).ToArray());
            Assert.Equal([202], harness.InstalledScopes[1].Select(item => item.Adapter.RuntimeHandle).ToArray());
        }

        harness.Cancel.Cancel();
        await harness.RunTask.ConfigureAwait(false);
        Assert.Equal(1, harness.DurableDisposeCount);
    }

    [Fact]
    public async Task RefreshDropsDisappearedAdapterWithWarnAndContinuesOnRemaining()
    {
        await using var harness = new CaptureRunnerHarness(
            [CaptureRunnerFakes.AdapterItem("id-a", 101), CaptureRunnerFakes.AdapterItem("id-b", 202)],
            CaptureRunnerFakes.AdapterConstrainedPolicy("id-a", "id-b"));
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        harness.Enumeration.SetAdapters(CaptureRunnerFakes.AdapterItem("id-a", 101));
        harness.ChangeSource.Trigger();
        await AsyncTestExtensions.WaitForAsync(() => harness.Generations.Generations.Count == 2).ConfigureAwait(false);
        await harness.WaitForGenerationStartedAsync(1).ConfigureAwait(false);

        Assert.Equal(["id-a"], harness.Generation(1).Scope.Select(item => item.StableId).ToArray());
        Assert.Contains(harness.Logger.Lines, line => line.Level == RuntimeLogLevel.Warn && line.Message.Contains("id-b", StringComparison.Ordinal));
        Assert.Contains("id-b", CaptureRunnerHarness.FieldValue(harness.RefreshEvents[^1], "removed"), StringComparison.Ordinal);
        Assert.Equal(0, harness.DurableDisposeCount);
        Assert.False(harness.RunTask.IsCompleted);
    }

    [Fact]
    public async Task RefreshAdoptsNewAdapterForUnconstrainedPolicy()
    {
        await using var harness = new CaptureRunnerHarness([CaptureRunnerFakes.AdapterItem("id-a", 101)], CaptureRunnerFakes.UnconstrainedPolicy());
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        harness.Enumeration.SetAdapters(CaptureRunnerFakes.AdapterItem("id-a", 101), CaptureRunnerFakes.AdapterItem("id-new", 303));
        harness.ChangeSource.Trigger();
        await AsyncTestExtensions.WaitForAsync(() => harness.Generations.Generations.Count == 2).ConfigureAwait(false);
        await harness.WaitForGenerationStartedAsync(1).ConfigureAwait(false);

        Assert.Equal(["id-a", "id-new"], harness.Generation(1).Scope.Select(item => item.StableId).ToArray());
        Assert.Contains("id-new", CaptureRunnerHarness.FieldValue(harness.RefreshEvents[^1], "added"), StringComparison.Ordinal);
        Assert.False(harness.RunTask.IsCompleted);
    }

    [Fact]
    public async Task RapidSignalsCoalesceIntoOneRebuild()
    {
        await using var harness = new CaptureRunnerHarness(
            [CaptureRunnerFakes.AdapterItem("id-a", 101)],
            CaptureRunnerFakes.UnconstrainedPolicy(),
            minimumRefreshInterval: TimeSpan.FromMilliseconds(150));
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        harness.Enumeration.SetAdapters(CaptureRunnerFakes.AdapterItem("id-a", 202));
        harness.ChangeSource.Trigger();
        harness.ChangeSource.Trigger();
        harness.ChangeSource.Trigger();
        await AsyncTestExtensions.WaitForAsync(() => harness.Generations.Generations.Count == 2).ConfigureAwait(false);
        await harness.WaitForGenerationStartedAsync(1).ConfigureAwait(false);
        await Task.Delay(400).ConfigureAwait(false);

        // Three rapid signals executed exactly one rebuild; anything the guard window coalesced
        // resolves as a no-op re-check that never touches the running generation.
        Assert.Equal(2, harness.Generations.Generations.Count);
        Assert.Equal(0, harness.Generation(1).DisposeCount);
    }

    [Fact]
    public async Task AllScopeAdaptersDisappearingPausesInterceptionUntilAdapterReturns()
    {
        await using var harness = new CaptureRunnerHarness([CaptureRunnerFakes.AdapterItem("id-a", 101)], CaptureRunnerFakes.AdapterConstrainedPolicy("id-a"));
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        harness.Enumeration.SetAdapters();
        harness.ChangeSource.Trigger();
        await AsyncTestExtensions.WaitForAsync(() => harness.Generation(0).DisposeCount == 1).ConfigureAwait(false);
        // The paused warn is logged after the generation disposal, so poll for it instead of
        // racing the runner's remaining refresh-pipeline writes.
        await AsyncTestExtensions.WaitForAsync(
            () => harness.Logger.Lines.Any(line => line.Message.Contains("paused", StringComparison.Ordinal))).ConfigureAwait(false);
        Assert.Contains(harness.Logger.Lines, line => line.Message.Contains("paused", StringComparison.Ordinal));
        Assert.Contains("id-a", CaptureRunnerHarness.FieldValue(harness.RefreshEvents[^1], "removed"), StringComparison.Ordinal);

        await Task.Delay(50).ConfigureAwait(false);
        Assert.Single(harness.Generations.Generations);

        harness.Enumeration.SetAdapters(CaptureRunnerFakes.AdapterItem("id-a", 909));
        harness.ChangeSource.Trigger();
        await AsyncTestExtensions.WaitForAsync(() => harness.Generations.Generations.Count == 2).ConfigureAwait(false);
        await harness.WaitForGenerationStartedAsync(1).ConfigureAwait(false);
        Assert.Equal([909], harness.Generation(1).Scope.Select(item => item.Adapter.RuntimeHandle).ToArray());
        Assert.False(harness.RunTask.IsCompleted);
    }
}
