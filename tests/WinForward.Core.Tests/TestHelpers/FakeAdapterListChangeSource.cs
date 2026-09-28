using WinForward.Runtime.Capture;

namespace WinForward.Core.Tests;

/// <summary>
/// Manual-trigger <see cref="IAdapterListChangeSource"/> for consumer-side tests (design §3.2
/// test impl). Trigger semantics mirror the native auto-reset event: triggers raised while no
/// waiter is parked coalesce into ONE pending observation (the ground truth is the
/// re-enumeration, never the signal count), so any number of triggers between waits consumes as
/// a single <c>WaitOne == true</c>. A trigger arriving while a waiter is parked resolves exactly
/// that waiter with true. <see cref="Cancel"/> (also invoked by <see cref="Dispose"/>) releases
/// every parked waiter with false, makes all future waits return false, and ignores later
/// triggers; cancelling is idempotent.
/// </summary>
internal sealed class FakeAdapterListChangeSource : IAdapterListChangeSource
{
    private readonly Lock _gate = new();
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();
    private bool _pending;
    private bool _cancelled;

    /// <summary>Records one adapter-list-change observation (coalescing while unconsumed).</summary>
    public void Trigger()
    {
        lock (_gate)
        {
            if (_cancelled) return;
            while (_waiters.Count > 0)
            {
                // Skip waiters already resolved by their own cancellation token: the signal
                // must reach the next live waiter, not vanish into a completed one.
                if (_waiters.Dequeue().TrySetResult(true)) return;
            }
            _pending = true;
        }
    }

    /// <summary>Cancels the source: parked waits resolve false; future waits return false.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _cancelled = true;
            _pending = false;
            while (_waiters.Count > 0) _waiters.Dequeue().TrySetResult(false);
        }
    }

    public bool WaitOne(CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> waiter;
        lock (_gate)
        {
            if (_cancelled) return false;
            if (_pending)
            {
                _pending = false;
                return true;
            }
            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false), waiter);
        return waiter.Task.GetAwaiter().GetResult();
    }

    public void Dispose() => Cancel();
}

/// <summary>
/// An <see cref="IAdapterListChangeSource"/> whose <see cref="WaitOne"/> parks until
/// <see cref="Release"/> is called, consulting its cancellation token only once the wait is
/// released. A test can therefore hold a capture runner's monitor inside its blocking wait while
/// teardown runs (task 09-20-structured-concurrency C4, the monitor-lease quiescence test).
/// <see cref="Dispose"/> releases the wait like <see cref="FakeAdapterListChangeSource.Cancel"/>
/// does.
/// </summary>
internal sealed class BlockingAdapterListChangeSource : IAdapterListChangeSource
{
    private readonly TaskCompletionSource _monitoring = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the monitor thread has parked inside <see cref="WaitOne"/>.</summary>
    public Task WaitUntilMonitoringAsync() => _monitoring.Task;

    /// <summary>Releases the parked wait; a cancelled token then makes <see cref="WaitOne"/> return false.</summary>
    public void Release() => _release.TrySetResult();

    public bool WaitOne(CancellationToken cancellationToken)
    {
        _monitoring.TrySetResult();
        _release.Task.GetAwaiter().GetResult();
        return !cancellationToken.IsCancellationRequested;
    }

    public void Dispose() => Release();
}
