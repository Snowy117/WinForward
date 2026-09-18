using WinForward.Runtime.Capture;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class LayeredCaptureRunnerPeriodicRefreshTests
{
    [Fact]
    public async Task AddressFingerprintChangeAloneRebuildsThroughTheRefreshPipeline()
    {
        await using var harness = new CaptureRunnerHarness(
            [CaptureRunnerFakes.AdapterItem("id-a", 101, addressFingerprint: "192.168.77.2;240c:c001:101::1")],
            CaptureRunnerFakes.UnconstrainedPolicy(),
            minimumRefreshInterval: TimeSpan.FromMilliseconds(50));
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        // The outage shape (task 09-17): handle/MAC/MTU identical, only the unicast addresses
        // rotated; the watcher alone would never notice, the fingerprint diff must.
        harness.Enumeration.SetAdapters(CaptureRunnerFakes.AdapterItem("id-a", 101, addressFingerprint: "192.168.77.2;240c:c001:202::9"));
        harness.ChangeSource.Trigger();
        await AsyncTestExtensions.WaitForAsync(() => harness.Generations.Generations.Count == 2, timeoutMs: 5000).ConfigureAwait(false);
        await harness.WaitForGenerationStartedAsync(1).ConfigureAwait(false);

        Assert.Contains("id-a", CaptureRunnerHarness.FieldValue(harness.RefreshEvents[^1], "changed"), StringComparison.Ordinal);
        Assert.Equal(1, harness.Generation(0).DisposeCount);
        Assert.Equal(0, harness.Generation(1).DisposeCount);
        Assert.Equal(0, harness.DurableDisposeCount);
        Assert.False(harness.RunTask.IsCompleted);
    }

    [Fact]
    public async Task PeriodicTickWithoutChangesStaysNoOpWithoutGenerationChurn()
    {
        await using var harness = new CaptureRunnerHarness(
            [CaptureRunnerFakes.AdapterItem("id-a", 101)],
            CaptureRunnerFakes.UnconstrainedPolicy(),
            minimumRefreshInterval: TimeSpan.FromMilliseconds(20),
            periodicRefreshInterval: TimeSpan.FromMilliseconds(30));
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        await AsyncTestExtensions.WaitForAsync(() => harness.RefreshEvents.Count >= 3, timeoutMs: 5000).ConfigureAwait(false);
        await Task.Delay(120).ConfigureAwait(false);

        // Multiple ticks processed, every one of them the no-op skip: the running generation is
        // never touched and keeps running.
        Assert.All(harness.RefreshEvents, fields => Assert.Equal("true", CaptureRunnerHarness.FieldValue(fields, "noop")));
        Assert.Single(harness.Generations.Generations);
        Assert.Equal(0, harness.Generation(0).DisposeCount);
        Assert.True(harness.Enumeration.EnumerationCount > 1);
        Assert.False(harness.RunTask.IsCompleted);
    }

    [Fact]
    public async Task PeriodicTickWithChangesRebuildsExactlyOnceDespitePendingTicks()
    {
        await using var harness = new CaptureRunnerHarness(
            [CaptureRunnerFakes.AdapterItem("id-a", 101, addressFingerprint: "240c:c001:101::1")],
            CaptureRunnerFakes.UnconstrainedPolicy(),
            minimumRefreshInterval: TimeSpan.FromMilliseconds(150),
            periodicRefreshInterval: TimeSpan.FromMilliseconds(30));
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        harness.Enumeration.SetAdapters(CaptureRunnerFakes.AdapterItem("id-a", 101, addressFingerprint: "240c:c001:202::9"));
        await AsyncTestExtensions.WaitForAsync(() => harness.Generations.Generations.Count == 2, timeoutMs: 5000).ConfigureAwait(false);
        await harness.WaitForGenerationStartedAsync(1).ConfigureAwait(false);

        // Ticks that landed inside the storm window and afterwards coalesce into no-op
        // re-checks against the now-identical enumeration — never into further rebuilds.
        await AsyncTestExtensions.WaitForAsync(
            () => harness.RefreshEvents.Count >= 2 && harness.RefreshEvents.Skip(1).Any(
                fields => string.Equals(CaptureRunnerHarness.FieldValue(fields, "noop"), "true", StringComparison.Ordinal)),
            timeoutMs: 5000).ConfigureAwait(false);
        await Task.Delay(400).ConfigureAwait(false);

        Assert.Equal(2, harness.Generations.Generations.Count);
        Assert.Equal(0, harness.Generation(1).DisposeCount);
        Assert.Equal(0, harness.DurableDisposeCount);
        Assert.False(harness.RunTask.IsCompleted);
    }

    [Fact]
    public async Task ZeroPeriodicIntervalDisablesTheTickEntirely()
    {
        await using var harness = new CaptureRunnerHarness(
            [CaptureRunnerFakes.AdapterItem("id-a", 101)],
            CaptureRunnerFakes.UnconstrainedPolicy(),
            minimumRefreshInterval: TimeSpan.FromMilliseconds(20),
            periodicRefreshInterval: TimeSpan.Zero);
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);
        var enumerationCount = harness.Enumeration.EnumerationCount;

        await Task.Delay(150).ConfigureAwait(false);

        // No tick task ran: no demand was ever raised, so no refresh event and no re-enumeration.
        Assert.Empty(harness.RefreshEvents);
        Assert.Single(harness.Generations.Generations);
        Assert.Equal(enumerationCount, harness.Enumeration.EnumerationCount);
        Assert.False(harness.RunTask.IsCompleted);
    }

    [Fact]
    public void NegativePeriodicIntervalIsRejected()
    {
        var fault = Assert.Throws<ArgumentOutOfRangeException>(() => new LayeredCaptureRunner(
            new FakeAdapterEnumerationProvider([CaptureRunnerFakes.AdapterItem("id-a", 101)]),
            new FakeCaptureGenerationFactory(),
            new FakeAdapterListChangeSource(),
            CaptureRunnerFakes.UnconstrainedPolicy(),
            new RecordingRuntimeLogger(),
            _ => ValueTask.CompletedTask,
            periodicRefreshInterval: TimeSpan.FromSeconds(-1)));
        Assert.Equal("periodicRefreshInterval", fault.ParamName);
    }
}
