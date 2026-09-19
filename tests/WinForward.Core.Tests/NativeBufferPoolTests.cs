using WinForward.Core;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class NativeBufferPoolTests
{
    private static readonly bool[] s_expectedRentalEvents = [true, false, true, false];

    [Fact]
    public void RentDisposeRoundtripReusesTheSameBuffer()
    {
        using var pool = new NativeBufferPool(bufferSize: 64);
        NativeLease first;
        using (first = pool.Rent())
        {
            first.Span[0] = 0xAB;
            Assert.Equal(64, first.Length);
        }
        Assert.Equal(1, pool.Count);

        using var second = pool.Rent();
        Assert.Equal(64, second.Length);
        Assert.Equal(0, pool.Count);

        // The reused lease presents the same native storage: the marker byte written before the
        // release is still observable, proving reuse rather than a fresh allocation.
        Assert.Equal(0xAB, second.Span[0]);
    }

    [Fact]
    public void LeaseMemoryViewRoundTripsWritesAndReads()
    {
        using var pool = new NativeBufferPool(bufferSize: 16);
        using var lease = pool.Rent();

        lease.Memory.Span[0] = 42;
        lease.Memory.Span[15] = 7;

        Assert.Equal(16, lease.Memory.Length);
        Assert.Equal(42, lease.Span[0]);
        Assert.Equal(7, lease.Span[15]);
        Assert.Equal(42, lease.Memory.Span[0]);
    }

    [Fact]
    public void ReusedLeaseMemoryViewAllocatesNoManagedBytes()
    {
        // The MemoryManager bridges native storage to Memory<byte> for async APIs. It is created
        // with the buffer, not with each rent, so a warm pool hands out Memory<byte> views without
        // touching the managed heap.
        using var pool = new NativeBufferPool(bufferSize: 64, capacity: 4);
        pool.Rent().Dispose();

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int rents = 256;
        for (var index = 0; index < rents; index++)
        {
            using var lease = pool.Rent();
            lease.Memory.Span[0] = 1;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(rents, pool.Stats.Rented - 1);
    }

    [Fact]
    public void ReturnsPastCapacityAreReleasedNotPooled()
    {
        using var pool = new NativeBufferPool(bufferSize: 16, capacity: 1);
        var first = pool.Rent();
        var second = pool.Rent();

        first.Dispose();
        second.Dispose();

        // The second return exceeded the capacity and was freed to the native heap; only the
        // first buffer remains pooled, and the balance identity still holds.
        Assert.Equal(1, pool.Count);
        var stats = pool.Stats;
        Assert.Equal(2, stats.OverflowAllocations);
        Assert.Equal(1, stats.DisposedCount);
        Assert.Equal(1, stats.InPool);
        Assert.Equal(0, stats.Outstanding);
    }

    [Fact]
    public async Task ConcurrentRentsHandOutDistinctBuffers()
    {
        using var pool = new NativeBufferPool(bufferSize: 32, capacity: 4);
        var rented = new System.Collections.Concurrent.ConcurrentBag<byte>();

        await Parallel.ForAsync(0, 64, async (index, _) =>
        {
            var lease = pool.Rent();
            lease.Span[0] = (byte)index;
            rented.Add(lease.Span[0]);
            lease.Dispose();
            await Task.Yield();
        });

        // Every renter observed its own storage (distinct first bytes), so no buffer was handed
        // to two renters at once.
        Assert.Equal(Enumerable.Range(0, 64).Select(static value => (byte)value).Order(), rented.Order());
    }

    [Fact]
    public void RepeatDisposeOfALeaseIsANoOp()
    {
        using var pool = new NativeBufferPool(bufferSize: 16);
        var lease = pool.Rent();
        lease.Dispose();
        lease.Dispose();

        // The double dispose must not enqueue the buffer twice: one subsequent rent hands it out,
        // and the pool is empty afterwards rather than holding a duplicate entry.
        Assert.Equal(1, pool.Count);
        using var second = pool.Rent();
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void LeaseCopiesReleaseExactlyOnce()
    {
        using var pool = new NativeBufferPool(bufferSize: 16);
        var lease = pool.Rent();
        var copy = lease;

        copy.Dispose();

        // The original and the copy alias one rental window: the second release attempt is a
        // no-op, exactly like a repeat dispose on the same copy.
        lease.Dispose();
        Assert.Equal(1, pool.Count);
        Assert.Equal(1, pool.Stats.Returned);
    }

    [Fact]
    public void DefaultLeaseDisposeIsANoOp()
    {
        var lease = default(NativeLease);
        lease.Dispose();
        Assert.Equal(0, lease.Length);
    }

    [Fact]
    public void PoolDisposalDrainsIdleBuffersAndRentStillWorks()
    {
        var pool = new NativeBufferPool(bufferSize: 16);
        var first = pool.Rent();
        var second = pool.Rent();
        first.Dispose();
        second.Dispose();
        Assert.Equal(2, pool.Count);

        pool.Dispose();
        Assert.Equal(0, pool.Count);

        // Disposal trims idle buffers but does not disable the pool: a later rent allocates fresh.
        using var third = pool.Rent();
        third.Span[0] = 9;
        Assert.Equal(9, third.Span[0]);
        Assert.Equal(3, pool.Stats.OverflowAllocations);
        Assert.Equal(2, pool.Stats.DisposedCount);
        Assert.Equal(1, pool.Stats.Outstanding);
    }

    [Fact]
    public void BufferSizeAndCapacityAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeBufferPool(bufferSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeBufferPool(bufferSize: 16, capacity: 0));
    }

    [Fact]
    public void StatsStayBalancedAcrossRentReturnAndDispose()
    {
        var pool = new NativeBufferPool(bufferSize: 16, capacity: 16);
        var leases = new NativeLease[8];
        for (var index = 0; index < leases.Length; index++)
        {
            leases[index] = pool.Rent();
            leases[index].Span[0] = (byte)index;
        }

        // Everything is checked out: the pool started empty, so every rent allocated fresh.
        var live = pool.Stats;
        Assert.Equal(8, live.Rented);
        Assert.Equal(0, live.Returned);
        Assert.Equal(8, live.Outstanding);
        Assert.Equal(0, live.InPool);
        Assert.Equal(8, live.OverflowAllocations);

        foreach (var lease in leases) lease.Dispose();

        var pooled = pool.Stats;
        Assert.Equal(8, pooled.Rented);
        Assert.Equal(8, pooled.Returned);
        Assert.Equal(0, pooled.Outstanding);
        Assert.Equal(8, pooled.InPool);
        Assert.Equal(0, pooled.DisposedCount);

        pool.Dispose();

        // Disposal drains the queue to the native heap: allocations and frees balance, nothing
        // is stranded in the pool and nothing is still checked out.
        var drained = pool.Stats;
        Assert.Equal(8, drained.Returned);
        Assert.Equal(0, drained.InPool);
        Assert.Equal(8, drained.DisposedCount);
        Assert.Equal(8, drained.OverflowAllocations);
    }

    [Fact]
    public void StatsSeparateReuseFromFreshAllocation()
    {
        using var pool = new NativeBufferPool(bufferSize: 16);
        for (var round = 0; round < 4; round++)
        {
            var lease = pool.Rent();
            lease.Span[0] = (byte)round;
            lease.Dispose();
        }

        // Four rents against one allocation: the first rent created the buffer, the rest reused
        // the pooled instance, so the overflow counter is the sizing diagnostic, not a rent
        // count.
        var stats = pool.Stats;
        Assert.Equal(4, stats.Rented);
        Assert.Equal(4, stats.Returned);
        Assert.Equal(0, stats.Outstanding);
        Assert.Equal(1, stats.OverflowAllocations);
        Assert.Equal(1, stats.InPool);
        Assert.Equal(0, stats.DisposedCount);
    }

    [Fact]
    public async Task ReturnsRacingDisposeNeverStrandBuffers()
    {
        // L2: a return that passes the disposed check just before Dispose sets its flag can
        // enqueue after the disposer's drain already saw an empty queue. The return's
        // post-enqueue recheck drains on the returner's side, so once every returner and the
        // dispose have completed, every allocated buffer has been freed — none stays stranded in
        // the queue until process exit. The identity assertions are deterministic; the iteration
        // loop widens the window for the racing interleaving itself to occur.
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var pool = new NativeBufferPool(bufferSize: 16, capacity: 4);
            var leases = new NativeLease[16];
            for (var index = 0; index < leases.Length; index++) leases[index] = pool.Rent();

            var returning = Task.WhenAll(leases.Select(lease => Task.Run(lease.Dispose)));
            pool.Dispose();
            await returning;

            Assert.Equal(0, pool.Count);
            var stats = pool.Stats;
            Assert.Equal(leases.Length, stats.Rented);
            Assert.Equal(leases.Length, stats.Returned);
            Assert.Equal(leases.Length, stats.OverflowAllocations);
            Assert.Equal(leases.Length, stats.DisposedCount);
            Assert.Equal(0, stats.InPool);
        }
    }

    [Fact]
    public void AccountingSinkObservesEveryRentAndReturnExactlyOnce()
    {
        using var pool = new NativeBufferPool(bufferSize: 16);
        Assert.Null(pool.AccountingSink);
        var observed = new List<bool>();
        pool.AccountingSink = rented => observed.Add(rented);

        var lease = pool.Rent();
        lease.Dispose();
        var reused = pool.Rent();
        var stale = reused;
        reused.Dispose();
        stale.Dispose();

        // One sink event per hand-out and one per completed return, in order — a reused hand-out
        // counts exactly like a fresh one, and the idempotent second release produces none.
        Assert.Equal(s_expectedRentalEvents, observed);
    }
}
