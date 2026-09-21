using WinForward.Runtime.Capture;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The capture runner's quiescence contract (task 09-20-structured-concurrency C4, design §2.1):
/// the run scope joins every registered worker before teardown returns. The adapter-list monitor
/// is the worker whose body blocks for the whole run, so a monitor still parked in its blocking
/// wait must keep <see cref="LayeredCaptureRunner.RunAsync"/> pending. The regression this catches
/// is a monitor that is no longer tracked — or no longer joined — which would let teardown return
/// while the dedicated thread was still inside the wait.
/// </summary>
public sealed class LayeredCaptureRunnerQuiescenceTests
{
    [Fact]
    public async Task RunTeardownWaitsForTheMonitorLeaseAndCompletesWhenItEnds()
    {
        var changeSource = new BlockingAdapterListChangeSource();
        var generations = new FakeCaptureGenerationFactory();
        using var cancel = new CancellationTokenSource();
        var runner = new LayeredCaptureRunner(
            new FakeAdapterEnumerationProvider([CaptureRunnerFakes.AdapterItem("id-a", 101)]),
            generations,
            changeSource,
            CaptureRunnerFakes.UnconstrainedPolicy(),
            new RecordingRuntimeLogger(),
            _ => ValueTask.CompletedTask);

        var runTask = runner.RunAsync(cancel.Token);
        try
        {
            await AsyncTestExtensions.WaitForAsync(
                () => generations.Generations.Count == 1 && generations.Generations[0].Started.Task.IsCompleted).ConfigureAwait(false);
            await changeSource.WaitUntilMonitoringAsync().ConfigureAwait(false);

            await cancel.CancelAsync().ConfigureAwait(false);

            // The generation is stopped and the scope is sealed, but the monitor still holds its
            // lease inside the blocking wait: the drain — and therefore the run — must stay pending.
            await AsyncTestExtensions.WaitForAsync(() => generations.Generations[0].DisposeCount == 1).ConfigureAwait(false);
            Assert.False(runTask.IsCompleted, "run teardown must wait for the monitor lease to be released");

            // Releasing the blocking wait ends the monitor loop, releases the lease, and lets the
            // drain and the durable disposal finish.
            changeSource.Release();
            await runTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        finally
        {
            changeSource.Release();
        }
    }
}
