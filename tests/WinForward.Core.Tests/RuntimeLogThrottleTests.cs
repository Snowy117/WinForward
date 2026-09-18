using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class RuntimeLogThrottleTests
{
    [Fact]
    public void FirstCallAlwaysEmits()
    {
        var throttle = new RuntimeLogThrottle(TimeSpan.FromHours(1));

        Assert.True(throttle.ShouldEmit());
    }

    [Fact]
    public void CallsInsideTheWindowAreSuppressed()
    {
        var throttle = new RuntimeLogThrottle(TimeSpan.FromHours(1));

        Assert.True(throttle.ShouldEmit());
        Assert.False(throttle.ShouldEmit());
        Assert.False(throttle.ShouldEmit());
    }

    [Fact]
    public async Task CallsAfterTheWindowEmitAgain()
    {
        var throttle = new RuntimeLogThrottle(TimeSpan.FromMilliseconds(20));

        Assert.True(throttle.ShouldEmit());
        await Task.Delay(80);

        Assert.True(throttle.ShouldEmit());
        Assert.False(throttle.ShouldEmit());
    }

    [Fact]
    public async Task ConcurrentFirstCallersElectExactlyOneWinner()
    {
        var throttle = new RuntimeLogThrottle(TimeSpan.FromHours(1));
        const int callers = 16;
        using var barrier = new Barrier(callers);
        var results = new bool[callers];

        var tasks = Enumerable.Range(0, callers).Select(index => Task.Run(() =>
        {
            barrier.SignalAndWait();
            results[index] = throttle.ShouldEmit();
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(emitted => emitted));
    }

    [Fact]
    public void NonPositiveWindowIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeLogThrottle(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeLogThrottle(TimeSpan.FromSeconds(-1)));
    }
}
