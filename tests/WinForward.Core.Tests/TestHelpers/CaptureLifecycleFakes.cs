using WinForward.Runtime.Capture;

namespace WinForward.Core.Tests;

/// <summary>
/// Capture-runtime lifecycle fakes shared by the capture test files: a scriptable mode
/// controller for <see cref="TransactionalCaptureRuntime"/> transaction tests and the two
/// capture-loop shapes (immediately-completing / parked-until-released) its start, stop, and
/// dispose orderings need.
/// </summary>

/// <summary>
/// Scriptable <see cref="IAdapterModeController"/>: serves a fixed snapshot list, records applied
/// and restored adapters in order, and optionally fails the Nth apply (0-based) so startup
/// rollback paths can be exercised deterministically.
/// </summary>
internal sealed class FakeModes(IReadOnlyList<AdapterModeSnapshot> snapshots, int failOnApply = -1) : IAdapterModeController
{
    public List<string> Applied { get; } = [];
    public List<string> Restored { get; } = [];

    public ValueTask<IReadOnlyList<AdapterModeSnapshot>> SnapshotAsync(CancellationToken cancellationToken) => ValueTask.FromResult(snapshots);

    public ValueTask ApplyCaptureModeAsync(AdapterModeSnapshot adapter, CancellationToken cancellationToken)
    {
        if (Applied.Count == failOnApply) throw new InvalidOperationException("mode apply failed");
        Applied.Add(adapter.AdapterId);
        return ValueTask.CompletedTask;
    }

    public ValueTask RestoreAsync(AdapterModeSnapshot adapter, CancellationToken cancellationToken)
    {
        Restored.Add(adapter.AdapterId);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// An <see cref="IPacketCaptureLoop"/> whose run completes immediately; records disposal so tests
/// can assert that normal completion and pre-start disposal still close the capture.
/// </summary>
internal sealed class CompletingCapture : IPacketCaptureLoop
{
    public bool Disposed { get; private set; }

    public ValueTask RunAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// An <see cref="IPacketCaptureLoop"/> that parks its run until the test releases it: signals
/// <see cref="Started"/>, surfaces cancellation into <see cref="CancellationObserved"/> without
/// exiting, and keeps running until <see cref="Complete"/> — so concurrent stop/dispose paths can
/// be ordered deterministically against a run that is provably in flight.
/// </summary>
internal sealed class BlockingCapture : IPacketCaptureLoop
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
