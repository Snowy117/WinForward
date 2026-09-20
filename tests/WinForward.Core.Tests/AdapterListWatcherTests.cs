using System.Runtime.Versioning;
using WinForward.Runtime.Capture;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class AdapterListWatcherTests
{
    private const int ParkMs = 100;

    // ---- NdisAdapterListWatcher.WaitForSignal (wait-any core, driver-free) ----

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PresetSignalIsConsumedWithoutBlocking()
    {
        using var signal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var cancel = new EventWaitHandle(initialState: false, EventResetMode.ManualReset);
        signal.Set();

        Assert.True(NdisAdapterListWatcher.WaitForSignal(signal, cancel));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SignalResolvesAParkedWaitAsListChanged()
    {
        using var signal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var cancel = new EventWaitHandle(initialState: false, EventResetMode.ManualReset);
        // ReSharper disable AccessToDisposedClosure // Both handles are consumed by the waiter task, which this test awaits (Assert.True(await waiter)) before the using scope disposes them.
        var waiter = Task.Run(() => NdisAdapterListWatcher.WaitForSignal(signal, cancel));
        // ReSharper restore AccessToDisposedClosure

        await Task.Delay(ParkMs);
        Assert.False(waiter.IsCompleted);

        signal.Set();
        Assert.True(await waiter);
    }

    // ---- NdisAdapterListWatcher lifecycle (internal driver-free ctor seam) ----

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task WaitOneResolvesAsCancelledWhenTheTokenFires()
    {
        using var watcher = new NdisAdapterListWatcher();
        using var cts = new CancellationTokenSource();
        // ReSharper disable AccessToDisposedClosure // The waiter is awaited (Assert.False(await waiter)) after cts cancellation, still inside the scope; watcher and cts are disposed only afterwards.
        var waiter = Task.Run(() => watcher.WaitOne(cts.Token));
        // ReSharper restore AccessToDisposedClosure

        await Task.Delay(ParkMs);
        Assert.False(waiter.IsCompleted);

        await cts.CancelAsync();
        Assert.False(await waiter);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WaitOneReturnsFalseForAnAlreadyCancelledToken()
    {
        using var watcher = new NdisAdapterListWatcher();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(watcher.WaitOne(cts.Token));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DisposeUnblocksAParkedWaitOne()
    {
        var watcher = new NdisAdapterListWatcher();
        // ReSharper disable once AccessToDisposedClosure // Deliberate pattern under test: Dispose is the unblock mechanism for the parked WaitOne that the test awaits right after, so the overlap is the assertion target.
        var waiter = Task.Run(() => watcher.WaitOne(CancellationToken.None));

        await Task.Delay(ParkMs);
        Assert.False(waiter.IsCompleted);

        watcher.Dispose();
        Assert.False(await waiter);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WaitOneReturnsFalseAfterDispose()
    {
        var watcher = new NdisAdapterListWatcher();
        watcher.Dispose();

        Assert.False(watcher.WaitOne(CancellationToken.None));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void DisposeIsIdempotent()
    {
        var watcher = new NdisAdapterListWatcher();
        watcher.Dispose();
        watcher.Dispose();
    }

    // ---- FakeAdapterListChangeSource semantics (the S5 runner seam) ----

    [Fact]
    public void FakeTriggerBeforeWaitIsConsumedExactlyOnce()
    {
        using var source = new FakeAdapterListChangeSource();
        source.Trigger();

        Assert.True(source.WaitOne(CancellationToken.None));
        // ReSharper disable once AccessToDisposedClosure // The detached waiter is meant to stay parked past the 100 ms window; the source's Dispose (using scope) then releases it false, the helper's documented cancel-on-dispose contract.
        Assert.False(Task.Run(() => source.WaitOne(CancellationToken.None)).Wait(ParkMs));
    }

    [Fact]
    public void FakeTriggersCoalesceIntoOnePendingObservation()
    {
        using var source = new FakeAdapterListChangeSource();
        source.Trigger();
        source.Trigger();
        source.Trigger();

        Assert.True(source.WaitOne(CancellationToken.None));
        // ReSharper disable once AccessToDisposedClosure // Same detached-waiter shape as the trigger test above: the abandoned waiter is released false by the using scope's Dispose.
        Assert.False(Task.Run(() => source.WaitOne(CancellationToken.None)).Wait(ParkMs));
    }

    [Fact]
    public async Task FakeTriggerWakesExactlyOneParkedWaiter()
    {
        using var source = new FakeAdapterListChangeSource();
        // ReSharper disable once AccessToDisposedClosure // Both parked waiters outlive the assertions by design; the woken one is awaited true and the parked one is released false by the using scope's Dispose.
        var first = Task.Run(() => source.WaitOne(CancellationToken.None));
        // ReSharper disable once AccessToDisposedClosure // Second parked waiter: the test asserts exactly one is woken by Trigger, and the remaining one is released false by the using scope's Dispose.
        var second = Task.Run(() => source.WaitOne(CancellationToken.None));
        await Task.Delay(ParkMs);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        source.Trigger();

        await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        var resolved = first.IsCompleted ? first : second;
        var parked = first.IsCompleted ? second : first;
        Assert.True(await resolved);
        await Task.Delay(ParkMs);
        Assert.False(parked.IsCompleted);
    }

    [Fact]
    public async Task FakeCancelReleasesParkedWaitersAndFutureWaitsReturnFalse()
    {
        using var source = new FakeAdapterListChangeSource();
        // ReSharper disable once AccessToDisposedClosure // The waiter is resolved by the explicit Cancel below and awaited before the using scope disposes the source.
        var waiter = Task.Run(() => source.WaitOne(CancellationToken.None));
        await Task.Delay(ParkMs);
        Assert.False(waiter.IsCompleted);

        source.Cancel();

        Assert.False(await waiter);
        Assert.False(source.WaitOne(CancellationToken.None));
    }

    [Fact]
    public void FakeWaitOneHonoursAnAlreadyCancelledToken()
    {
        using var source = new FakeAdapterListChangeSource();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(source.WaitOne(cts.Token));
    }

    [Fact]
    public async Task FakeTokenCancellationMidWaitResolvesAsCancelled()
    {
        using var source = new FakeAdapterListChangeSource();
        using var cts = new CancellationTokenSource();
        // ReSharper disable AccessToDisposedClosure // cts cancels the wait and the waiter is awaited before the using scope disposes either object.
        var waiter = Task.Run(() => source.WaitOne(cts.Token));
        // ReSharper restore AccessToDisposedClosure
        await Task.Delay(ParkMs);
        Assert.False(waiter.IsCompleted);

        await cts.CancelAsync();

        Assert.False(await waiter);
    }

    [Fact]
    public void FakeTriggerAfterCancelIsIgnored()
    {
        using var source = new FakeAdapterListChangeSource();
        source.Cancel();
        source.Trigger();

        Assert.False(source.WaitOne(CancellationToken.None));
    }

    [Fact]
    public void FakeDisposeCancelsAndIsIdempotent()
    {
        var source = new FakeAdapterListChangeSource();
        source.Dispose();
        source.Dispose();
        source.Cancel();

        Assert.False(source.WaitOne(CancellationToken.None));
    }
}
