using WinForward.Cli;
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

        var start = Task.Run(async () => await runtime.StartAsync(CancellationToken.None));
        await capture.Started.Task;
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

        var firstStop = Task.Run(async () => await runtime.StopAsync());
        await capture.DisposeStarted.Task;

        var secondStop = Task.Run(async () => await runtime.StopAsync());
        Assert.False(secondStop.IsCompleted);

        capture.CompleteDispose();
        await Task.WhenAll(firstStop, secondStop);

        Assert.Equal(CaptureRuntimeState.Closed, runtime.State);
        Assert.Equal(1, capture.DisposeCount);
    }

    [Fact]
    public async Task CoordinatorShutdownCompositionClosesProxySessionsBeforeRestoringModes()
    {
        var events = new List<string>();
        var modes = new RecordingModes(events);
        var capture = new RecordingCapture(events);
        var sweeper = new RecordingDisposable(events, "sweeper");
        var udpCoordinator = new RecordingDisposable(events, "udp coordinator");
        var tcpCoordinator = new RecordingDisposable(events, "tcp coordinator");
        await using var composition = new Program.CoordinatorShutdownCaptureLoop(capture, sweeper, udpCoordinator, tcpCoordinator);
        await using var runtime = new TransactionalCaptureRuntime(modes, composition);

        await runtime.StartAsync(CancellationToken.None);

        Assert.Equal(
            ["capture run", "sweeper", "capture", "udp coordinator", "tcp coordinator", "restore"],
            events);
    }

    [Fact]
    public async Task CoordinatorShutdownCompositionClosesProxySessionsBeforeRestoringModesOnCaptureFailure()
    {
        var events = new List<string>();
        var modes = new RecordingModes(events);
        var capture = new FailingRecordingCapture(events);
        var sweeper = new RecordingDisposable(events, "sweeper");
        var udpCoordinator = new RecordingDisposable(events, "udp coordinator");
        var tcpCoordinator = new RecordingDisposable(events, "tcp coordinator");
        await using var composition = new Program.CoordinatorShutdownCaptureLoop(capture, sweeper, udpCoordinator, tcpCoordinator);
        await using var runtime = new TransactionalCaptureRuntime(modes, composition);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runtime.StartAsync(CancellationToken.None));

        Assert.Equal(
            ["capture run", "sweeper", "capture", "udp coordinator", "tcp coordinator", "restore"],
            events);
    }

    [Fact]
    public async Task CoordinatorShutdownCompositionWaitsForCaptureRunBeforeClosingProxySessionsOnStop()
    {
        var events = new List<string>();
        var modes = new RecordingModes(events);
        var capture = new BlockingRecordingCapture(events);
        var sweeper = new RecordingDisposable(events, "sweeper");
        var udpCoordinator = new RecordingDisposable(events, "udp coordinator");
        var tcpCoordinator = new RecordingDisposable(events, "tcp coordinator");
        await using var composition = new Program.CoordinatorShutdownCaptureLoop(capture, sweeper, udpCoordinator, tcpCoordinator);
        await using var runtime = new TransactionalCaptureRuntime(modes, composition);

        var start = Task.Run(async () => await runtime.StartAsync(CancellationToken.None));
        await capture.Started.Task;
        var stop = Task.Run(async () => await runtime.StopAsync());

        await capture.CancellationObserved.Task;
        Assert.Equal(["capture run"], events);

        capture.Complete();
        await Task.WhenAll(start, stop);

        Assert.Equal(
            ["capture run", "sweeper", "capture", "udp coordinator", "tcp coordinator", "restore"],
            events);
    }

    private sealed class FakeModes : IAdapterModeController
    {
        private readonly IReadOnlyList<AdapterModeSnapshot> _snapshots;
        private readonly int _failOnApply;
        public FakeModes(IReadOnlyList<AdapterModeSnapshot> snapshots, int failOnApply = -1) { _snapshots = snapshots; _failOnApply = failOnApply; }
        public List<string> Applied { get; } = [];
        public List<string> Restored { get; } = [];
        public ValueTask<IReadOnlyList<AdapterModeSnapshot>> SnapshotAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_snapshots);
        public ValueTask ApplyCaptureModeAsync(AdapterModeSnapshot adapter, CancellationToken cancellationToken)
        {
            if (Applied.Count == _failOnApply) throw new InvalidOperationException("mode apply failed");
            Applied.Add(adapter.AdapterId);
            return ValueTask.CompletedTask;
        }
        public ValueTask RestoreAsync(AdapterModeSnapshot adapter, CancellationToken cancellationToken) { Restored.Add(adapter.AdapterId); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCapture : IPacketCaptureLoop
    {
        public async ValueTask RunAsync(CancellationToken cancellationToken) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingModes(List<string> events) : IAdapterModeController
    {
        public ValueTask<IReadOnlyList<AdapterModeSnapshot>> SnapshotAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AdapterModeSnapshot>>([new("adapter", 1)]);
        public ValueTask ApplyCaptureModeAsync(AdapterModeSnapshot adapter, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RestoreAsync(AdapterModeSnapshot adapter, CancellationToken cancellationToken)
        {
            events.Add("restore");
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingCapture(List<string> events) : IPacketCaptureLoop
    {
        public ValueTask RunAsync(CancellationToken cancellationToken)
        {
            events.Add("capture run");
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            events.Add("capture");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingRecordingCapture(List<string> events) : IPacketCaptureLoop
    {
        public ValueTask RunAsync(CancellationToken cancellationToken)
        {
            events.Add("capture run");
            return ValueTask.FromException(new InvalidOperationException("capture failed"));
        }
        public ValueTask DisposeAsync()
        {
            events.Add("capture");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingRecordingCapture(List<string> events) : IPacketCaptureLoop
    {
        private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask RunAsync(CancellationToken cancellationToken)
        {
            events.Add("capture run");
            Started.TrySetResult();
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            await _complete.Task;
        }

        public void Complete() => _complete.TrySetResult();

        public ValueTask DisposeAsync()
        {
            events.Add("capture");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDisposable(List<string> events, string name) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            events.Add(name);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompletingCapture : IPacketCaptureLoop
    {
        public bool Disposed { get; private set; }
        public ValueTask RunAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
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

    private sealed class BlockingCapture : IPacketCaptureLoop
    {
        private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask RunAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            await _complete.Task;
        }

        public void Complete() => _complete.TrySetResult();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
