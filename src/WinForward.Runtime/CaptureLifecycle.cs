using WinForward.Core;

namespace WinForward.Runtime;

public enum CaptureRuntimeState
{
    Created,
    Prepared,
    ModesApplied,
    Running,
    Stopping,
    Closed
}

public readonly record struct AdapterModeSnapshot(string AdapterId, uint Flags);

public interface IAdapterModeController : IAsyncDisposable
{
    ValueTask<IReadOnlyList<AdapterModeSnapshot>> SnapshotAsync(CancellationToken cancellationToken);
    ValueTask ApplyCaptureModeAsync(AdapterModeSnapshot adapter, CancellationToken cancellationToken);
    ValueTask RestoreAsync(AdapterModeSnapshot adapter, CancellationToken cancellationToken);
}

public interface IPacketCaptureLoop : IAsyncDisposable
{
    ValueTask RunAsync(CancellationToken cancellationToken);
}

public sealed class TransactionalCaptureRuntime : IAsyncDisposable
{
    private readonly IAdapterModeController _modes;
    private readonly IPacketCaptureLoop _capture;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<AdapterModeSnapshot> _applied = [];
    private readonly Lock _gate = new();
    private CaptureRuntimeState _state = CaptureRuntimeState.Created;
    private Task? _runTask;
    private Task? _cleanupTask;
    private int _captureDisposed;

    public TransactionalCaptureRuntime(IAdapterModeController modes, IPacketCaptureLoop capture)
    {
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(capture);
        _modes = modes;
        _capture = capture;
    }

    public CaptureRuntimeState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state != CaptureRuntimeState.Created) throw new InvalidOperationException($"Cannot start runtime from state {_state}.");
            _state = CaptureRuntimeState.Prepared;
            _runTask = StartCoreAsync(cancellationToken);
            return new ValueTask(_runTask);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var runtimeCancellation = linkedCancellation.Token;
        try
        {
            var snapshots = await _modes.SnapshotAsync(runtimeCancellation).ConfigureAwait(false);
            foreach (var adapter in snapshots)
            {
                await _modes.ApplyCaptureModeAsync(adapter, runtimeCancellation).ConfigureAwait(false);
                _applied.Add(adapter);
            }
            SetActiveState(CaptureRuntimeState.ModesApplied);
            SetActiveState(CaptureRuntimeState.Running);
            await _capture.RunAsync(runtimeCancellation).ConfigureAwait(false);
        }
        finally
        {
            SetStoppingState();
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask StopAsync()
    {
        Task? runTask;
        var cancelShutdown = false;
        lock (_gate)
        {
            if (_state == CaptureRuntimeState.Closed) return;
            if (_state != CaptureRuntimeState.Stopping)
            {
                _state = CaptureRuntimeState.Stopping;
                cancelShutdown = true;
            }
            runTask = _runTask;
        }

        if (cancelShutdown) await _shutdown.CancelAsync().ConfigureAwait(false);
        if (runTask is not null)
        {
            try { await runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            return;
        }

        await CleanupAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => StopAsync();

    private async ValueTask RestoreBestEffortAsync()
    {
        foreach (var adapter in _applied.AsEnumerable().Reverse())
        {
            try { await _modes.RestoreAsync(adapter, CancellationToken.None).ConfigureAwait(false); }
#pragma warning disable RCS1075 // Rollback must continue restoring the remaining adapters.
            catch (Exception)
            {
                // Continue restoring the remaining adapters; shutdown is fail-closed.
            }
#pragma warning restore RCS1075
        }
        _applied.Clear();
        await _modes.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private ValueTask CleanupAsync()
    {
        lock (_gate)
        {
            _cleanupTask ??= CleanupCoreAsync();
            return new ValueTask(_cleanupTask);
        }
    }

    private async Task CleanupCoreAsync()
    {
        try
        {
            await DisposeCaptureAsync().ConfigureAwait(false);
        }
        finally
        {
            try { await RestoreBestEffortAsync().ConfigureAwait(false); }
            finally { SetClosedState(); }
        }
    }

    private void SetActiveState(CaptureRuntimeState state)
    {
        lock (_gate)
        {
            if (_state is CaptureRuntimeState.Stopping or CaptureRuntimeState.Closed) return;
            _state = state;
        }
    }

    private void SetStoppingState()
    {
        lock (_gate)
        {
            if (_state != CaptureRuntimeState.Closed) _state = CaptureRuntimeState.Stopping;
        }
    }

    private void SetClosedState()
    {
        lock (_gate) _state = CaptureRuntimeState.Closed;
    }

    private ValueTask DisposeCaptureAsync() =>
        Interlocked.Exchange(ref _captureDisposed, 1) == 0 ? _capture.DisposeAsync() : ValueTask.CompletedTask;
}
