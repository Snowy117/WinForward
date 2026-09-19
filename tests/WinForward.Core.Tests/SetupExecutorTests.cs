using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;

namespace WinForward.Core.Tests;

/// <summary>
/// Setup-slot pool balance (task 09-18 M3): the bounded free list recycles completed work items,
/// so a warm executor rents without fresh allocations, and rent/enqueue/complete counters stay
/// balanced across rounds.
/// </summary>
public sealed class SetupExecutorTests
{
    [Fact]
    public async Task SetupSlotPoolBalancesRentReturnAndKeepsOverflowFlatOnceWarm()
    {
        const int slotCount = 4;
        using var executor = new SetupExecutor(workerCount: 1, ringCapacity: slotCount);

        await RunRoundAsync(executor, slotCount);
        Assert.Equal(slotCount, executor.OverflowAllocations);
        Assert.Equal(slotCount, executor.EnqueuedCount);
        Assert.Equal(slotCount, executor.CompletedCount);
        Assert.Equal(0, executor.PendingCount);
        Assert.Equal(0, executor.RejectedCount);

        // Warm free list: a second round reuses the recycled slots, so the overflow diagnostic
        // does not move.
        await RunRoundAsync(executor, slotCount);
        Assert.Equal(slotCount, executor.OverflowAllocations);
        Assert.Equal(2 * slotCount, executor.EnqueuedCount);
        Assert.Equal(2 * slotCount, executor.CompletedCount);
        Assert.Equal(0, executor.PendingCount);
        Assert.Equal(0, executor.RejectedCount);
    }

    [Fact]
    public async Task SetupExecutorRejectsBeyondRingCapacityAndRecyclesTheRejectedItem()
    {
        using var executor = new SetupExecutor(workerCount: 1, ringCapacity: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = executor.RentItem(_ => gate.Task);
        blocker._completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(executor.TryEnqueue(blocker));

        var rejected = executor.RentItem(static _ => Task.CompletedTask);
        rejected._completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(executor.TryEnqueue(rejected));
        Assert.Equal(1, executor.RejectedCount);
        Assert.Equal(1, executor.PendingCount);

        // The rejected slot is recycled immediately, so a later rent reuses it instead of allocating.
        var overflowBefore = executor.OverflowAllocations;
        Assert.Same(rejected, executor.RentItem(static _ => Task.CompletedTask));
        Assert.Equal(overflowBefore, executor.OverflowAllocations);

        gate.TrySetResult();
        blocker._completion.TrySetResult();
        await WaitForAsync(() => executor.FreeCount > 0);
        Assert.Equal(0, executor.PendingCount);
    }

    [Fact]
    public async Task SetupExecutorFaultsTheItemCompletionAndKeepsDraining()
    {
        using var executor = new SetupExecutor(workerCount: 1, ringCapacity: 4);

        var failing = executor.RentItem(static _ => Task.FromException(new InvalidOperationException("boom")));
        var failedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failing._completion = failedCompletion;
        Assert.True(executor.TryEnqueue(failing));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("boom", thrown.Message);
        Assert.Equal(1, executor.CompletedCount);
        Assert.Equal(0, executor.PendingCount);
        await WaitForAsync(() => executor.FreeCount == 1);

        // A faulted item never kills the worker: later work still runs and the slot was recycled.
        var healthy = executor.RentItem(static _ => Task.CompletedTask);
        var healthyCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        healthy._completion = healthyCompletion;
        Assert.True(executor.TryEnqueue(healthy));
        await healthyCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, executor.CompletedCount);
    }

    [Fact]
    public async Task SetupExecutorDisposeJoinsWorkersDrainsTheRingAndRefusesNewWork()
    {
        // One worker parks on the gate, so the second item provably stays queued until Dispose drains it.
        var executor = new SetupExecutor(workerCount: 1, ringCapacity: 4);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = executor.RentItem(_ => gate.Task);
        running._completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(executor.TryEnqueue(running));

        // A second item is queued behind the blocked worker; it must be drained, not left hanging.
        var queued = executor.RentItem(static _ => Task.CompletedTask);
        var queuedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queued._completion = queuedCompletion;
        Assert.True(executor.TryEnqueue(queued));

        var dispose = Task.Run(executor.Dispose);
        await WaitForAsync(() => executor.IsDisposed);
        gate.TrySetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(queuedCompletion.Task.IsCanceled);
        Assert.Equal(0, executor.PendingCount);

        // Post-dispose enqueues are refused (and the slot recycled), never thrown.
        var late = executor.RentItem(static _ => Task.CompletedTask);
        late._completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(executor.TryEnqueue(late));

        // Dispose is idempotent.
        executor.Dispose();
    }

    [Fact]
    public void DefaultRingCapacityCoversThePendingSynIndexCap()
    {
        // The pending-SYN index drains through this ring, so the ring must not be the smaller of
        // the two: an index that could retain more entries than the ring queues would reject
        // setups at load. The load-bearing relation is the inequality — a larger ring is safe.
        Assert.True(
            SetupExecutor.DefaultRingCapacity >= TcpPendingSynSetupIndex.DefaultCapacity,
            "the setup ring must be at least as large as the TCP pending-SYN index cap");
    }

    private static async Task RunRoundAsync(SetupExecutor executor, int slotCount)
    {
        var items = new SetupWorkItem[slotCount];
        var completions = new Task[slotCount];
        for (var index = 0; index < slotCount; index++)
        {
            items[index] = executor.RentItem(static _ => Task.CompletedTask);
        }

        for (var index = 0; index < slotCount; index++)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            items[index]._completion = completion;
            completions[index] = completion.Task;
            Assert.True(executor.TryEnqueue(items[index]));
        }

        await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => executor.FreeCount == slotCount && executor.PendingCount == 0);
    }
}
