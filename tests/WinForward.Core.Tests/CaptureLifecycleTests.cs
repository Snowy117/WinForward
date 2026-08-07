using WinForward.Runtime;
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
    public async Task DisposingBeforeStartClosesCaptureAndModes()
    {
        var modes = new FakeModes([]);
        var capture = new CompletingCapture();
        await using var runtime = new TransactionalCaptureRuntime(modes, capture);

        await runtime.DisposeAsync();

        Assert.Equal(CaptureRuntimeState.Closed, runtime.State);
        Assert.True(capture.Disposed);
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

    private sealed class CompletingCapture : IPacketCaptureLoop
    {
        public bool Disposed { get; private set; }
        public ValueTask RunAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
