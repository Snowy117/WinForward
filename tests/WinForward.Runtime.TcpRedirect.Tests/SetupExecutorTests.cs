using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;

namespace WinForward.Runtime.TcpRedirect.Tests;

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
    public async Task SetupExecutorDisposeRacingTheFirstEnqueueLeavesNoItemUnsettled()
    {
        // TryEnqueue reads _disposed == 0, then Dispose can set it, join the workers, drain the
        // (still empty) ring and dispose the shutdown source and the signal while the enqueue is
        // still appending and the worker start is still in flight. Two contracts hang off that one
        // window: a worker must not fault its thread on the disposed shutdown source (it exits like
        // a cancelled one), and an accepted item must settle its completion rather than sit in a
        // ring nobody drains. The interleaving needs no forcing — it fires within the first hundred
        // attempts (measured 2026-09-30: the host aborted on every run without the worker guard,
        // and 1997 of 1998 accepted items were stranded without the post-enqueue recheck).
        const int attempts = 512;
        var accepted = 0;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var executor = new SetupExecutor(workerCount: 1, ringCapacity: 4);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = executor.RentItem(static _ => Task.CompletedTask);
            item._completion = completion;
            var dispose = Task.Run(executor.Dispose);

            var enqueued = executor.TryEnqueue(item);
            await dispose;

            if (enqueued)
            {
                accepted++;
                Assert.True(completion.Task.IsCompleted, "an accepted setup item was stranded by the racing shutdown");
            }
            else
            {
                // The refusal path is explicit: nothing ran the item, so nothing settles it.
                Assert.False(completion.Task.IsCompleted, "a refused setup item settled a completion nothing ran");
            }

            Assert.Equal(0, executor.PendingCount);
        }

        Assert.True(accepted > 0, "the dispose race never accepted an enqueue");
    }

    [Fact]
    public void DefaultRingCapacityCoversBothFiniteProducerCaps()
    {
        // Both finite-capacity producers drain through this ring: the pending-SYN index and the
        // deferred-attribution index. An index that could retain more entries than the ring queues
        // would reject setups at load, so the ring must cover their sum. The load-bearing relation
        // is the inequality — a larger ring is safe. UDP session setup is deliberately excluded:
        // it enqueues per admitted session against a sixteen-thousand-flow capacity, so no
        // satisfiable inequality covers it, and its refusals are counted and fail-closed.
        Assert.True(
            SetupExecutor.DefaultRingCapacity >= TcpPendingSynSetupIndex.DefaultCapacity + FlowAttributionPendingIndex.DefaultCapacity,
            "the setup ring must cover the sum of the TCP pending-SYN and deferred-attribution index caps");
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
