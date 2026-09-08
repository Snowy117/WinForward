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
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var cancel = new EventWaitHandle(false, EventResetMode.ManualReset);
        signal.Set();

        Assert.True(NdisAdapterListWatcher.WaitForSignal(signal, cancel));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SignalResolvesAParkedWaitAsListChanged()
    {
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var cancel = new EventWaitHandle(false, EventResetMode.ManualReset);
        var waiter = Task.Run(() => NdisAdapterListWatcher.WaitForSignal(signal, cancel));

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
        var waiter = Task.Run(() => watcher.WaitOne(cts.Token));

        await Task.Delay(ParkMs);
        Assert.False(waiter.IsCompleted);

        cts.Cancel();
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
        Assert.False(Task.Run(() => source.WaitOne(CancellationToken.None)).Wait(ParkMs));
    }

    [Fact]
    public void FakeTriggerWakesExactlyOneParkedWaiter()
    {
        using var source = new FakeAdapterListChangeSource();
        var first = Task.Run(() => source.WaitOne(CancellationToken.None));
        var second = Task.Run(() => source.WaitOne(CancellationToken.None));
        Assert.False(first.Wait(ParkMs));
        Assert.False(second.Wait(ParkMs));

        source.Trigger();

        Assert.True(first.Wait(TimeSpan.FromSeconds(5)) || second.Wait(TimeSpan.FromSeconds(5)));
        var resolved = first.IsCompleted ? first : second;
        var parked = first.IsCompleted ? second : first;
        Assert.True(resolved.Result);
        Assert.False(parked.Wait(ParkMs));
    }

    [Fact]
    public async Task FakeCancelReleasesParkedWaitersAndFutureWaitsReturnFalse()
    {
        using var source = new FakeAdapterListChangeSource();
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
        var waiter = Task.Run(() => source.WaitOne(cts.Token));
        await Task.Delay(ParkMs);
        Assert.False(waiter.IsCompleted);

        cts.Cancel();

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
