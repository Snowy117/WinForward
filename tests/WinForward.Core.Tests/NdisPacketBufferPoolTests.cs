using WinForward.NdisApi;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class NdisPacketBufferPoolTests
{
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

        await Parallel.ForAsync(0, 64, async (_, cancellationToken) =>
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
        Assert.Equal(new byte[] { 9 }, buffer.GetFrame().ToArray());
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
}
