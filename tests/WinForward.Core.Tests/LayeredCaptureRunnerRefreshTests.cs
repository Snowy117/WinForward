using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class LayeredCaptureRunnerRefreshTests
{
    [Fact]
    public async Task RefreshChurnKeepsPassBatchingAliveAcrossGenerations()
    {
        // The design-review P0-1 leak: the durable executor's pass lanes were keyed by
        // (adapter handle, direction) and never removed, while every adapter-list refresh mints
        // fresh handles — after a handful of refreshes the fixed lane table filled with dead
        // keys and every pass silently degraded to an immediate single send. This regression
        // drives five generations × two adapters (ten distinct handles) through the runner with
        // the production shape of the scope-installed callback (retire lanes outside the
        // installed scope) and proves the final generation still batches.
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);
        CaptureRunnerHarness? harness = null;
        await using var capture = new CaptureRunnerHarness(
            [CaptureRunnerFakes.AdapterItem("id-a", (nint)1001), CaptureRunnerFakes.AdapterItem("id-b", (nint)1002)],
            CaptureRunnerFakes.UnconstrainedPolicy(),
            minimumRefreshInterval: TimeSpan.FromMilliseconds(50),
            onScopeInstalled: scope =>
            {
                var handles = new nint[scope.Count];
                for (var index = 0; index < scope.Count; index++) handles[index] = scope[index].Adapter.RuntimeHandle;
                executor.RetireLanesExcept(handles);
                harness!.AddEvent("retire-lanes");
            });
        harness = capture;
        capture.Start();
        await capture.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        for (var generation = 1; generation <= 4; generation++)
        {
            var firstHandle = (nint)(1001 + generation * 2);
            capture.Enumeration.SetAdapters(
                CaptureRunnerFakes.AdapterItem("id-a", firstHandle),
                CaptureRunnerFakes.AdapterItem("id-b", firstHandle + 1));
            capture.ChangeSource.Trigger();
            await AsyncTestExtensions.WaitForAsync(() => capture.Generations.Generations.Count == generation + 1).ConfigureAwait(false);
            await capture.WaitForGenerationStartedAsync(generation).ConfigureAwait(false);
        }

        // Traffic on the final generation's fresh handles still batches: batched reinjector
        // calls observed, zero immediate sends, zero overflow. Without retirement, ten distinct
        // handle keys would have exhausted the eight-lane table generations ago.
        var finalA = (nint)(1001 + 4 * 2);
        var finalB = finalA + 1;
        await executor.PassAsync(PassPacket(finalA, isOnSend: true), CancellationToken.None);
        await executor.PassAsync(PassPacket(finalA, isOnSend: true), CancellationToken.None);
        await executor.PassAsync(PassPacket(finalB, isOnSend: false), CancellationToken.None);
        await executor.PassAsync(PassPacket(finalB, isOnSend: false), CancellationToken.None);
        executor.FlushPendingPasses(finalA);
        executor.FlushPendingPasses(finalB);

        Assert.Equal(0L, executor.ImmediateSendLaneOverflowCount);
        Assert.Equal(0, reinjector.ToAdapterCount + reinjector.ToMstcpCount);
        Assert.Equal(2, reinjector.BatchCalls.Count);
        Assert.All(reinjector.BatchCalls, call => Assert.Equal(2, call.Frames.Length));

        // Ordering proof at the runner level: every retire ran after the outgoing generation
        // was disposed and before the next generation was created — the between-generations
        // contract RetireLanesExcept depends on. All recorded events come from the runner's
        // sequential refresh pipeline, so the timeline is deterministic.
        var timeline = capture.Events.Where(e => !string.Equals(e, "durable-dispose", StringComparison.Ordinal)).ToArray();
        var expected = new List<string>();
        for (var generation = 0; generation < 5; generation++)
        {
            if (generation > 0) expected.Add($"generation-{generation - 1}-disposed");
            expected.Add($"generation-{generation}-created");
            expected.Add("scope-installed(2)");
            expected.Add("retire-lanes");
        }
        Assert.Equal(expected, timeline);
    }

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

    /// <summary>A materialized pass packet for one (adapter handle, direction) — the shape a pump hands the executor after a rewrite consumer materialized the lease.</summary>
    private static CapturedFlowPacket PassPacket(nint adapterHandle, bool isOnSend)
    {
        var lease = new PacketLease(new byte[] { 0x2A });
        _ = lease.Frame.Length; // materialize, as a rewriting consumer would
        var key = FlowKey.Create(
            Endpoint.From(IPAddressValue.IPv4Any, 1),
            Endpoint.From(IPAddressValue.IPv4Any, 2),
            TransportProtocol.Tcp,
            FlowOriginKind.Host);
        return new CapturedFlowPacket(
            lease,
            new FlowContext(key, null, null, null, null, key.Remote.Port),
            new PacketCaptureMetadata(isOnSend ? NdisApiAbi.PacketFlagOnSend : NdisApiAbi.PacketFlagOnReceive, adapterHandle));
    }
}
