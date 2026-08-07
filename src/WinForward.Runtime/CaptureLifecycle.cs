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
    private CaptureRuntimeState _state = CaptureRuntimeState.Created;
    private int _cleanupStarted;
    private int _captureDisposed;

    public TransactionalCaptureRuntime(IAdapterModeController modes, IPacketCaptureLoop capture)
    {
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(capture);
        _modes = modes;
        _capture = capture;
    }

    public CaptureRuntimeState State => _state;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_state != CaptureRuntimeState.Created) throw new InvalidOperationException($"Cannot start runtime from state {_state}.");
        try
        {
            var snapshots = await _modes.SnapshotAsync(cancellationToken).ConfigureAwait(false);
            _state = CaptureRuntimeState.Prepared;
            foreach (var adapter in snapshots)
            {
                await _modes.ApplyCaptureModeAsync(adapter, cancellationToken).ConfigureAwait(false);
                _applied.Add(adapter);
            }
            _state = CaptureRuntimeState.ModesApplied;
            _state = CaptureRuntimeState.Running;
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            await _capture.RunAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            if (_state != CaptureRuntimeState.Closed) _state = CaptureRuntimeState.Stopping;
            try { await DisposeCaptureAsync().ConfigureAwait(false); }
            finally
            {
                await RestoreBestEffortAsync().ConfigureAwait(false);
                _state = CaptureRuntimeState.Closed;
            }
        }
    }

    public async ValueTask StopAsync()
    {
        if (_state == CaptureRuntimeState.Closed) return;
        if (_state == CaptureRuntimeState.Created)
        {
            _state = CaptureRuntimeState.Stopping;
            try { await DisposeCaptureAsync().ConfigureAwait(false); }
            finally
            {
                await RestoreBestEffortAsync().ConfigureAwait(false);
                _state = CaptureRuntimeState.Closed;
            }
            return;
        }

        _state = CaptureRuntimeState.Stopping;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try { await DisposeCaptureAsync().ConfigureAwait(false); }
        finally
        {
            await RestoreBestEffortAsync().ConfigureAwait(false);
            _state = CaptureRuntimeState.Closed;
        }
    }

    public ValueTask DisposeAsync() => StopAsync();

    private async ValueTask RestoreBestEffortAsync()
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0) return;
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

    private ValueTask DisposeCaptureAsync() =>
        Interlocked.Exchange(ref _captureDisposed, 1) == 0 ? _capture.DisposeAsync() : ValueTask.CompletedTask;
}
