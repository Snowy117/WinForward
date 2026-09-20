using WinForward.Runtime.Capture;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class CaptureLifecycleTests
{
    [Fact]
    public async Task StartupFailureRestoresEveryAppliedAdapter()
    {
        var modes = new FakeModes([new("a", 7), new("b", 9)], failOnApply: 1);
        await using var runtime = new TransactionalCaptureRuntime(modes, new FakeCapture());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runtime.StartAsync(CancellationToken.None));

        Assert.Equal(CaptureRuntimeState.Closed, runtime.State);
        Assert.Equal(["a"], modes.Applied);
        Assert.Equal(["a"], modes.Restored);
    }

    [Fact]
    public async Task GracefulStopRestoresModesAndClosesCapture()
    {
        var modes = new FakeModes([new("a", 7)]);
        await using var runtime = new TransactionalCaptureRuntime(modes, new FakeCapture());
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runtime.StartAsync(cancellation.Token));
        await runtime.StopAsync();

        Assert.Equal(CaptureRuntimeState.Closed, runtime.State);
        Assert.Equal(["a"], modes.Restored);
    }

    [Fact]
    public async Task CaptureLoopNormalCompletionRestoresModes()
    {
        var modes = new FakeModes([new("a", 7)]);
        var capture = new CompletingCapture();
        await using var runtime = new TransactionalCaptureRuntime(modes, capture);

        await runtime.StartAsync(CancellationToken.None);

        Assert.Equal(CaptureRuntimeState.Closed, runtime.State);
        Assert.Equal(["a"], modes.Restored);
        Assert.True(capture.Disposed);
    }

    [Fact]
    public async Task ConcurrentStopWaitsForCaptureRunBeforeRestoringModes()
    {
        var modes = new FakeModes([new("a", 7)]);
        var capture = new BlockingCapture();
        await using var runtime = new TransactionalCaptureRuntime(modes, capture);

        // ReSharper disable once AccessToDisposedClosure // start is one of the two tasks joined by Task.WhenAll below, still inside the runtime's await using scope; the capture gate keeps the run pending until then.
        var start = Task.Run(async () => await runtime.StartAsync(CancellationToken.None));
        await capture.Started.Task;
        // ReSharper disable once AccessToDisposedClosure // stop is joined by Task.WhenAll(start, stop) before the runtime is disposed.
        var stop = Task.Run(async () => await runtime.StopAsync());

        await capture.CancellationObserved.Task;
        Assert.Empty(modes.Restored);

        capture.Complete();
        await Task.WhenAll(start, stop);

        Assert.Equal(["a"], modes.Restored);
        Assert.Equal(CaptureRuntimeState.Closed, runtime.State);
    }

    [Fact]
    public async Task DisposingBeforeStartClosesCaptureAndModes()
    {
        var modes = new FakeModes([]);
        var capture = new CompletingCapture();
        await using var runtime = new TransactionalCaptureRuntime(modes, capture);

        // ReSharper disable once DisposeOnUsingVariable // The explicit DisposeAsync is the act under test: the Closed state and the captured teardown are asserted immediately after it, before the using scope exits; the await using disposal only backstops assertion-failure paths.
        await runtime.DisposeAsync();

        Assert.Equal(CaptureRuntimeState.Closed, runtime.State);
        Assert.True(capture.Disposed);
    }

    [Fact]
    public async Task ConcurrentStopsBeforeStartWaitForTheSameCaptureCleanup()
    {
        var modes = new FakeModes([]);
        var capture = new BlockingDisposeCapture();
        await using var runtime = new TransactionalCaptureRuntime(modes, capture);

        // ReSharper disable once AccessToDisposedClosure // firstStop is joined by Task.WhenAll(firstStop, secondStop) before the runtime is disposed; the DisposeStarted gate makes the overlap intentional.
        var firstStop = Task.Run(async () => await runtime.StopAsync());
        await capture.DisposeStarted.Task;

        // ReSharper disable once AccessToDisposedClosure // secondStop is joined by the same Task.WhenAll, so no stop touches the runtime after the await using disposal.
        var secondStop = Task.Run(async () => await runtime.StopAsync());
        Assert.False(secondStop.IsCompleted);

        capture.CompleteDispose();
        await Task.WhenAll(firstStop, secondStop);

        Assert.Equal(CaptureRuntimeState.Closed, runtime.State);
        Assert.Equal(1, capture.DisposeCount);
    }

    [Fact]
    public async Task ReachedPumpRunStaysFalseWhenSnapshotFailsDuringStart()
    {
        var modes = new FakeModes([new("a", 7)], failOnSnapshot: true);
        await using var runtime = new TransactionalCaptureRuntime(modes, new FakeCapture());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runtime.StartAsync(CancellationToken.None));

        Assert.False(runtime.ReachedPumpRun);
    }

    [Fact]
    public async Task ReachedPumpRunStaysFalseWhenModeApplyFailsDuringStart()
    {
        var modes = new FakeModes([new("a", 7), new("b", 9)], failOnApply: 1);
        await using var runtime = new TransactionalCaptureRuntime(modes, new FakeCapture());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runtime.StartAsync(CancellationToken.None));

        Assert.False(runtime.ReachedPumpRun);
    }

    [Fact]
    public async Task ReachedPumpRunLatchesTrueOnceTheCaptureLoopRunStarted()
    {
        var modes = new FakeModes([new("a", 7)]);
        var capture = new BlockingCapture();
        await using var runtime = new TransactionalCaptureRuntime(modes, capture);

        // ReSharper disable once AccessToDisposedClosure // start is awaited (await start) after capture.Complete() releases the loop, still inside the await using scope.
        var start = Task.Run(async () => await runtime.StartAsync(CancellationToken.None));
        await capture.Started.Task;
        capture.Complete();
        await start;

        Assert.True(runtime.ReachedPumpRun);
    }

    private sealed class FakeCapture : IPacketCaptureLoop
    {
        public async ValueTask RunAsync(CancellationToken cancellationToken) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingDisposeCapture : IPacketCaptureLoop
    {
        private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }

        public ValueTask RunAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            await _complete.Task;
        }

        public void CompleteDispose() => _complete.TrySetResult();
    }
}
