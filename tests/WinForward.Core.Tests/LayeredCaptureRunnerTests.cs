using WinForward.Configuration;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class LayeredCaptureRunnerTests
{
    [Fact]
    public async Task StartupScopeResolutionFailurePropagatesFailClosed()
    {
        await using var harness = new CaptureRunnerHarness([CaptureRunnerFakes.AdapterItem("id-a", 101)], CaptureRunnerFakes.AdapterConstrainedPolicy("id-missing"));

        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runner.RunAsync(harness.Cancel.Token)).ConfigureAwait(false);

        Assert.Contains("resolution failed", fault.Message, StringComparison.Ordinal);
        Assert.Contains(harness.Logger.Lines, line => line.Level == RuntimeLogLevel.Error && line.Message.Contains("id-missing", StringComparison.Ordinal));
        Assert.Equal(1, harness.DurableDisposeCount);
        Assert.Empty(harness.Generations.Generations);
    }

    [Fact]
    public async Task StartupEmptyEnumerationFailsClosed()
    {
        await using var harness = new CaptureRunnerHarness([], CaptureRunnerFakes.UnconstrainedPolicy());

        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runner.RunAsync(harness.Cancel.Token)).ConfigureAwait(false);

        Assert.Contains("No MSTCP-bound adapters", fault.Message, StringComparison.Ordinal);
        Assert.Equal(1, harness.DurableDisposeCount);
        Assert.Empty(harness.Generations.Generations);
    }

    [Fact]
    public async Task UserCancelDisposesGenerationBeforeDurableDisposal()
    {
        await using var harness = new CaptureRunnerHarness([CaptureRunnerFakes.AdapterItem("id-a", 101)], CaptureRunnerFakes.UnconstrainedPolicy());
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        harness.Cancel.Cancel();
        await harness.RunTask.ConfigureAwait(false);

        Assert.Equal(1, harness.Generation(0).DisposeCount);
        Assert.Equal(1, harness.DurableDisposeCount);
        var events = harness.Events;
        var generationDisposal = Array.IndexOf(events, "generation-0-disposed");
        var durableDisposal = Array.IndexOf(events, "durable-dispose");
        Assert.True(generationDisposal >= 0, "the generation must be disposed at exit");
        Assert.True(durableDisposal > generationDisposal, $"generation disposal must precede durable disposal, got [{string.Join(", ", events)}]");
    }

    [Fact]
    public async Task GenerationFaultIsFailClosedAndStillDisposesDurable()
    {
        await using var harness = new CaptureRunnerHarness([CaptureRunnerFakes.AdapterItem("id-a", 101)], CaptureRunnerFakes.UnconstrainedPolicy());
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        harness.Generation(0).Fault(new InvalidOperationException("capture fault"));
        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunTask).ConfigureAwait(false);

        Assert.Equal("capture fault", fault.Message);
        Assert.Equal(1, harness.Generation(0).DisposeCount);
        Assert.Equal(1, harness.DurableDisposeCount);
        var faultEvents = harness.Events;
        Assert.True(Array.IndexOf(faultEvents, "generation-0-disposed") >= 0, "the generation must be disposed at exit");
        Assert.True(Array.IndexOf(faultEvents, "durable-dispose") > Array.IndexOf(faultEvents, "generation-0-disposed"),
            $"generation disposal must precede durable disposal, got [{string.Join(", ", faultEvents)}]");
    }

    [Fact]
    public async Task NaturalGenerationEndStopsTheRunAndDisposesGenerationBeforeDurable()
    {
        await using var harness = new CaptureRunnerHarness([CaptureRunnerFakes.AdapterItem("id-a", 101)], CaptureRunnerFakes.UnconstrainedPolicy());
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        harness.Generation(0).Complete();
        await harness.RunTask.ConfigureAwait(false);

        Assert.Contains(harness.Logger.Lines, line => line.Message.Contains("The capture generation ended", StringComparison.Ordinal));
        var events = harness.Events;
        var generationDisposal = Array.IndexOf(events, "generation-0-disposed");
        var durableDisposal = Array.IndexOf(events, "durable-dispose");
        Assert.True(generationDisposal >= 0, "the generation must be disposed at exit");
        Assert.True(durableDisposal > generationDisposal,
            $"generation disposal (pumps + mode restore) must precede durable disposal, got [{string.Join(", ", events)}]");
    }

    [Fact]
    public async Task DegradedError87WithUnchangedEnumerationLogsNoChangeWithoutRebuild()
    {
        await using var harness = new CaptureRunnerHarness([CaptureRunnerFakes.AdapterItem("id-a", 101)], CaptureRunnerFakes.UnconstrainedPolicy());
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);
        var enumerationCountAtStart = harness.Enumeration.EnumerationCount;

        harness.Runner.SignalDegraded(new WindowsAdapter("id-a", "id-a", "id-a", 101, 0), 87);
        await AsyncTestExtensions.WaitForAsync(() => harness.RefreshEvents.Count > 0).ConfigureAwait(false);

        var refresh = harness.RefreshEvents[^1];
        Assert.Equal("true", CaptureRunnerHarness.FieldValue(refresh, "noop"));
        Assert.Equal("id-a=true", CaptureRunnerHarness.FieldValue(refresh, "degraded"));
        Assert.Single(harness.Generations.Generations);
        Assert.Equal(0, harness.Generation(0).DisposeCount);
        Assert.False(harness.RunTask.IsCompleted);

        // Other native errors never feed the refresh channel (R3 gates on 87 only).
        harness.Runner.SignalDegraded(new WindowsAdapter("id-a", "id-a", "id-a", 101, 0), 31);
        await Task.Delay(50).ConfigureAwait(false);
        Assert.Single(harness.RefreshEvents);
        Assert.Equal(enumerationCountAtStart + 1, harness.Enumeration.EnumerationCount);
    }

    [Fact]
    public async Task DegradedError87WithVanishedAdapterReportsNotPresentAndRebuilds()
    {
        await using var harness = new CaptureRunnerHarness(
            [CaptureRunnerFakes.AdapterItem("id-a", 101), CaptureRunnerFakes.AdapterItem("id-b", 202)],
            CaptureRunnerFakes.UnconstrainedPolicy());
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        harness.Enumeration.SetAdapters(CaptureRunnerFakes.AdapterItem("id-a", 101));
        harness.Runner.SignalDegraded(new WindowsAdapter("id-b", "id-b", "id-b", 202, 0), 87);
        await AsyncTestExtensions.WaitForAsync(() => harness.Generations.Generations.Count == 2).ConfigureAwait(false);

        var refresh = harness.RefreshEvents[^1];
        Assert.Null(CaptureRunnerHarness.FieldValue(refresh, "noop"));
        Assert.Contains("id-b", CaptureRunnerHarness.FieldValue(refresh, "removed"), StringComparison.Ordinal);
        Assert.Equal("id-b=false", CaptureRunnerHarness.FieldValue(refresh, "degraded"));
        await harness.WaitForGenerationStartedAsync(1).ConfigureAwait(false);
        Assert.Equal(["id-a"], harness.Generation(1).Scope.Select(item => item.StableId).ToArray());
        Assert.Equal(0, harness.DurableDisposeCount);
        Assert.False(harness.RunTask.IsCompleted);
    }

    [Fact]
    public async Task SignalWithIdenticalEnumerationIsLoggedNoOpAndKeepsGenerationRunning()
    {
        await using var harness = new CaptureRunnerHarness([CaptureRunnerFakes.AdapterItem("id-a", 101)], CaptureRunnerFakes.UnconstrainedPolicy());
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        harness.ChangeSource.Trigger();
        await AsyncTestExtensions.WaitForAsync(() => harness.RefreshEvents.Count > 0).ConfigureAwait(false);

        var refresh = harness.RefreshEvents[^1];
        Assert.Equal("true", CaptureRunnerHarness.FieldValue(refresh, "noop"));
        Assert.Null(CaptureRunnerHarness.FieldValue(refresh, "degraded"));
        Assert.Null(CaptureRunnerHarness.FieldValue(refresh, "added"));
        Assert.Null(CaptureRunnerHarness.FieldValue(refresh, "removed"));
        Assert.Null(CaptureRunnerHarness.FieldValue(refresh, "changed"));
        Assert.Single(harness.Generations.Generations);
        Assert.Equal(0, harness.Generation(0).DisposeCount);
        Assert.False(harness.RunTask.IsCompleted);
    }
}
