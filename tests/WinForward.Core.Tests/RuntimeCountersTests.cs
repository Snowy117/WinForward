using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class RuntimeCountersTests
{
    [Fact]
    public void IncrementReturnsMonotonicValues()
    {
        var counters = new RuntimeCounters();

        Assert.Equal(1, counters.Increment("a"));
        Assert.Equal(2, counters.Increment("a"));
        Assert.Equal(3, counters.Increment("a"));
        Assert.Equal(3, counters.Get("a"));
    }

    [Fact]
    public void UntouchedKeyReadsZero()
    {
        var counters = new RuntimeCounters();

        Assert.Equal(0, counters.Get("never-touched"));
    }

    [Fact]
    public void KeysAreIndependentAndAccumulateViaAdd()
    {
        var counters = new RuntimeCounters();

        counters.Add("a", 5);
        counters.Add("a", 2);
        counters.Increment("b");

        Assert.Equal(7, counters.Get("a"));
        Assert.Equal(1, counters.Get("b"));
    }

    [Fact]
    public void SnapshotCopiesEveryTouchedCounter()
    {
        var counters = new RuntimeCounters();
        counters.Increment("a");
        counters.Increment("a");
        counters.Add("b", 9);

        var snapshot = counters.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(2, snapshot["a"]);
        Assert.Equal(9, snapshot["b"]);
    }

    [Fact]
    public async Task ConcurrentIncrementsAreAllCounted()
    {
        var counters = new RuntimeCounters();
        const int tasks = 8;
        const int incrementsPerTask = 500;
        using var barrier = new Barrier(tasks);

        await Task.WhenAll(Enumerable.Range(0, tasks).Select(_ => Task.Run(() =>
        {
            // ReSharper disable once AccessToDisposedClosure // Every worker is joined by the Task.WhenAll below before the using scope disposes the barrier; this SignalAndWait runs inside the awaited test body.
            barrier.SignalAndWait();
            for (var i = 0; i < incrementsPerTask; i++) counters.Increment("hot");
        })));

        Assert.Equal(tasks * incrementsPerTask, counters.Get("hot"));
    }

    [Fact]
    public void SharedInstanceExposesTheStandardVocabulary()
    {
        // The key names are the heartbeat's vocabulary; keep them stable.
        Assert.Equal("relaySetupFailed", RuntimeCounters.RelaySetupFailed);
        Assert.Equal("udpOriginUnresolved", RuntimeCounters.UdpOriginUnresolved);
        Assert.Equal("udpFailClosedDrop", RuntimeCounters.UdpFailClosedDrop);
        Assert.Equal("flowCapacityBlock", RuntimeCounters.FlowCapacityBlock);
        Assert.Equal("attributionMiss", RuntimeCounters.AttributionMiss);
        Assert.Equal("passReinjectFailed", RuntimeCounters.PassReinjectFailed);
    }

    /// <summary>
    /// The native-pool registry (task 09-18 M0): registered pools record cumulative
    /// rents/returns under pool.&lt;name&gt;.rented/.returned, occupancy is rented − returned,
    /// and the heartbeat aggregates occupancy across every registered pool.
    /// </summary>
    public sealed class PoolRegistryTests
    {
        [Fact]
        public void PoolCounterKeysFollowTheDocumentedVocabulary()
        {
            Assert.Equal("pool.", RuntimeCounters.PoolCounterPrefix);
            Assert.Equal("pool.frame.rented", RuntimeCounters.PoolRentedKey("frame"));
            Assert.Equal("pool.frame.returned", RuntimeCounters.PoolReturnedKey("frame"));
        }

        [Fact]
        public void RegisterPoolIsIdempotentAndPreCreatesTheCounters()
        {
            var counters = new RuntimeCounters();

            counters.RegisterPool("frame");
            counters.RegisterPool("frame");

            var pool = Assert.Single(counters.GetRegisteredPools());
            Assert.Equal("frame", pool);
            // Pre-created counters read zero and appear in snapshots before any activity.
            Assert.Equal(0, counters.Get(RuntimeCounters.PoolRentedKey("frame")));
            Assert.Equal(0, counters.Get(RuntimeCounters.PoolReturnedKey("frame")));
            Assert.Contains(RuntimeCounters.PoolRentedKey("frame"), counters.Snapshot());
        }

        [Fact]
        public void RentReturnActivityDrivesOccupancy()
        {
            var counters = new RuntimeCounters();
            counters.RegisterPool("frame");

            counters.RecordPoolRent("frame");
            counters.RecordPoolRent("frame");
            counters.RecordPoolRent("frame");
            Assert.Equal(3, counters.GetPoolOccupancy("frame"));

            counters.RecordPoolReturn("frame");
            Assert.Equal(2, counters.GetPoolOccupancy("frame"));
            Assert.Equal(3, counters.Get(RuntimeCounters.PoolRentedKey("frame")));
            Assert.Equal(1, counters.Get(RuntimeCounters.PoolReturnedKey("frame")));
        }

        [Fact]
        public void PoolActivityImpliesRegistration()
        {
            var counters = new RuntimeCounters();

            counters.RecordPoolRent("relay");

            Assert.Equal("relay", Assert.Single(counters.GetRegisteredPools()));
            Assert.Equal(1, counters.GetPoolOccupancy("relay"));
        }

        [Fact]
        public void TotalOccupancyAggregatesAcrossPoolsInOrdinalOrder()
        {
            var counters = new RuntimeCounters();
            counters.RegisterPool("udpWindow");
            counters.RegisterPool("frame");
            counters.RecordPoolRent("frame");
            counters.RecordPoolRent("udpWindow");
            counters.RecordPoolRent("udpWindow");
            counters.RecordPoolReturn("udpWindow");

            Assert.Equal(["frame", "udpWindow"], counters.GetRegisteredPools());
            Assert.Equal(2, counters.GetTotalPoolOccupancy());
        }

        [Fact]
        public void UnregisteredPoolOccupancyReadsZeroAndBlankNamesAreRejected()
        {
            var counters = new RuntimeCounters();

            Assert.Equal(0, counters.GetPoolOccupancy("never-registered"));
            Assert.Equal(0, counters.GetTotalPoolOccupancy());
            Assert.Throws<ArgumentException>(() => counters.RegisterPool(" "));
            Assert.Throws<ArgumentException>(() => counters.RecordPoolRent(""));
        }
    }
}
