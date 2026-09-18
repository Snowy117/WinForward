using WinForward.Runtime;
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
        var blocker = executor.RentItem();
        blocker.Handler = _ => gate.Task;
        blocker.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(executor.TryEnqueue(blocker));

        var rejected = executor.RentItem();
        rejected.Handler = static _ => Task.CompletedTask;
        rejected.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(executor.TryEnqueue(rejected));
        Assert.Equal(1, executor.RejectedCount);
        Assert.Equal(1, executor.PendingCount);

        // The rejected slot is recycled immediately, so a later rent reuses it instead of allocating.
        var overflowBefore = executor.OverflowAllocations;
        Assert.Same(rejected, executor.RentItem());
        Assert.Equal(overflowBefore, executor.OverflowAllocations);

        gate.TrySetResult();
        blocker.Completion.TrySetResult();
        await WaitForAsync(() => executor.FreeCount > 0);
        Assert.Equal(0, executor.PendingCount);
    }

    private static async Task RunRoundAsync(SetupExecutor executor, int slotCount)
    {
        var items = new SetupWorkItem[slotCount];
        var completions = new Task[slotCount];
        for (var index = 0; index < slotCount; index++)
        {
            items[index] = executor.RentItem();
            items[index].Handler = static _ => Task.CompletedTask;
        }

        for (var index = 0; index < slotCount; index++)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            items[index].Completion = completion;
            completions[index] = completion.Task;
            Assert.True(executor.TryEnqueue(items[index]));
        }

        await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => executor.FreeCount == slotCount && executor.PendingCount == 0);
    }
}
