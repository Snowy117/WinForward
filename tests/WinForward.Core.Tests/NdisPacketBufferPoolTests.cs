using WinForward.NdisApi;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class NdisPacketBufferPoolTests
{
    private static readonly bool[] s_expectedRentalEvents = [true, false, true, false];

    [Fact]
    public void RentDisposeRoundtripReusesTheSameBuffer()
    {
        using var pool = new NdisPacketBufferPool();
        NdisPacketBuffer first;
        using (first = pool.Rent())
        {
            first.SetFrame([1, 2, 3], NdisApiAbi.PacketFlagOnSend, (nint)7);
            Assert.Equal(3, first.Length);
        }
        Assert.Equal(1, pool.Count);

        using var second = pool.Rent();
        Assert.Same(first, second);
        Assert.Equal(0, pool.Count);

        // A reused buffer presents only its newest frame; the previous contents are not observable
        // through the wrapper until overwritten.
        second.SetFrame([4], NdisApiAbi.PacketFlagOnReceive, (nint)8);
        Assert.Equal(new byte[] { 4 }, second.GetFrame().ToArray());
    }

    [Fact]
    public void ReturnHandsTheBufferBackWithoutDisposal()
    {
        using var pool = new NdisPacketBufferPool();
        var buffer = pool.Rent();
        pool.Return(buffer);
        Assert.Equal(1, pool.Count);

        // A buffer that was already returned is no longer rented from the pool.
        Assert.Throws<ArgumentException>(() => pool.Return(buffer));

        Assert.Same(buffer, pool.Rent());
    }

    [Fact]
    public void ReturnsPastCapacityAreReleasedNotPooled()
    {
        using var pool = new NdisPacketBufferPool(capacity: 1);
        var first = pool.Rent();
        var second = pool.Rent();

        pool.Return(first);
        pool.Return(second);

        // The second return exceeded the capacity and was released to the native heap; only the
        // first buffer remains pooled.
        Assert.Equal(1, pool.Count);
        Assert.Same(first, pool.Rent());
    }

    [Fact]
    public async Task ConcurrentRentsHandOutDistinctBuffers()
    {
        using var pool = new NdisPacketBufferPool(capacity: 4);
        var rented = new System.Collections.Concurrent.ConcurrentBag<NdisPacketBuffer>();

        await Parallel.ForAsync(0, 64, async (_, _) =>
        {
            var buffer = pool.Rent();
            rented.Add(buffer);
            buffer.SetFrame([1, 2, 3], NdisApiAbi.PacketFlagOnSend, (nint)7);
            await Task.Yield();
        });

        Assert.Equal(64, rented.Count);
        Assert.Equal(64, rented.Distinct().Count());
    }

    [Fact]
    public void RepeatDisposeOfARentedBufferIsANoOp()
    {
        using var pool = new NdisPacketBufferPool();
        var buffer = pool.Rent();
        buffer.Dispose();
        buffer.Dispose();

        // The double dispose must not enqueue the buffer twice: one subsequent rent hands it out,
        // and the pool is empty afterwards rather than holding a duplicate entry.
        Assert.Equal(1, pool.Count);
        Assert.Same(buffer, pool.Rent());
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void PoolDisposalDrainsIdleBuffersAndRentStillWorks()
    {
        var pool = new NdisPacketBufferPool();
        var first = pool.Rent();
        var second = pool.Rent();
        first.Dispose();
        second.Dispose();
        Assert.Equal(2, pool.Count);

        pool.Dispose();
        Assert.Equal(0, pool.Count);

        // Disposal trims idle buffers but does not disable the pool: a later rent allocates fresh.
        using var buffer = pool.Rent();
        buffer.SetFrame([9], NdisApiAbi.PacketFlagOnReceive, (nint)1);
        Assert.Equal("\t"u8.ToArray(), buffer.GetFrame().ToArray());
    }

    [Fact]
    public void ReturnRejectsBuffersNotRentedFromThePool()
    {
        using var pool = new NdisPacketBufferPool();
        using var foreign = new NdisPacketBuffer();

        Assert.Throws<ArgumentException>(() => pool.Return(foreign));
    }

    [Fact]
    public void PrivateBuffersKeepFreeOnDisposeSemantics()
    {
        using var buffer = new NdisPacketBuffer();
        buffer.SetFrame([1, 2, 3], NdisApiAbi.PacketFlagOnSend, (nint)7);

        buffer.Dispose();

        // A privately constructed buffer is freed, not pooled: the wrapper refuses further use.
        Assert.Throws<ObjectDisposedException>(() => buffer.GetFrame());
    }

    [Fact]
    public void StatsStayBalancedAcrossRentReturnAndDispose()
    {
        var pool = new NdisPacketBufferPool(capacity: 16);
        var buffers = new NdisPacketBuffer[8];
        for (var index = 0; index < buffers.Length; index++)
        {
            buffers[index] = pool.Rent();
            buffers[index].SetFrame([(byte)index], NdisApiAbi.PacketFlagOnSend, (nint)1);
        }

        // Everything is checked out: the pool started empty, so every rent allocated fresh.
        var live = pool.Stats;
        Assert.Equal(8, live.Rented);
        Assert.Equal(0, live.Returned);
        Assert.Equal(8, live.Outstanding);
        Assert.Equal(0, live.InPool);
        Assert.Equal(8, live.OverflowAllocations);

        foreach (var buffer in buffers) pool.Return(buffer);

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
        using var pool = new NdisPacketBufferPool();
        for (var round = 0; round < 4; round++)
        {
            var buffer = pool.Rent();
            buffer.SetFrame([(byte)round], NdisApiAbi.PacketFlagOnSend, (nint)1);
            pool.Return(buffer);
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
            var pool = new NdisPacketBufferPool(capacity: 4);
            var buffers = new NdisPacketBuffer[16];
            for (var index = 0; index < buffers.Length; index++) buffers[index] = pool.Rent();

            var returning = Task.WhenAll(buffers.Select(buffer => Task.Run(buffer.Dispose)));
            pool.Dispose();
            await returning;

            Assert.Equal(0, pool.Count);
            var stats = pool.Stats;
            Assert.Equal(buffers.Length, stats.Rented);
            Assert.Equal(buffers.Length, stats.Returned);
            Assert.Equal(buffers.Length, stats.OverflowAllocations);
            Assert.Equal(buffers.Length, stats.DisposedCount);
            Assert.Equal(0, stats.InPool);
        }
    }

    [Fact]
    public void AccountingSinkObservesEveryRentAndReturnExactlyOnce()
    {
        using var pool = new NdisPacketBufferPool();
        Assert.Null(pool.AccountingSink);
        var observed = new List<bool>();
        pool.AccountingSink = rented => observed.Add(rented);

        var buffer = pool.Rent();
        pool.Return(buffer);
        var reused = pool.Rent();
        reused.Dispose();

        // One sink event per hand-out and one per completed return, in order — a reused hand-out
        // counts exactly like a fresh one (the sink observes rental events, not allocations).
        Assert.Equal(s_expectedRentalEvents, observed);
    }
}
