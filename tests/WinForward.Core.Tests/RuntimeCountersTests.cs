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
}
