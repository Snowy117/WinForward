using System.Runtime.Versioning;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class HighResolutionTimerScopeTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void SuccessfulBeginEnablesScopeAndDisposeEndsPeriodOnce()
    {
        var fake = new FakeWinmmTimer(BeginResult: 0);
        using var scope = new HighResolutionTimerScope(fake.Begin, fake.End);

        Assert.True(scope.IsEnabled);
        Assert.Equal([1u], fake.BeginPeriods);

        // ReSharper disable once DisposeOnUsingVariable // The explicit Dispose is the act under test: the fake timer's recorded End period is asserted right after it; the using declaration only backstops assertion-failure paths.
        scope.Dispose();

        Assert.Equal([1u], fake.EndPeriods);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void FailingBeginLeavesScopeDisabledWithoutEnd()
    {
        var fake = new FakeWinmmTimer(BeginResult: 96);
        using var scope = new HighResolutionTimerScope(fake.Begin, fake.End);

        Assert.False(scope.IsEnabled);

        // ReSharper disable once DisposeOnUsingVariable // The explicit Dispose is the act under test: End must not fire for a failed Begin, asserted right after it; the using declaration only backstops failure paths.
        scope.Dispose();

        Assert.Empty(fake.EndPeriods);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void DoubleDisposeEndsExactlyOnce()
    {
        var fake = new FakeWinmmTimer(BeginResult: 0);
        var scope = new HighResolutionTimerScope(fake.Begin, fake.End);

        scope.Dispose();
        scope.Dispose();

        Assert.Equal([1u], fake.EndPeriods);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ThrowingBeginLeavesScopeDisabledWithoutEnd()
    {
        var fake = new FakeWinmmTimer(BeginResult: 0, ThrowOnBegin: true);
        using var scope = new HighResolutionTimerScope(fake.Begin, fake.End);

        Assert.False(scope.IsEnabled);

        // ReSharper disable once DisposeOnUsingVariable // The explicit Dispose is the act under test: End must not fire for a throwing Begin, asserted right after it; the using declaration only backstops failure paths.
        scope.Dispose();

        Assert.Empty(fake.EndPeriods);
    }

    private sealed record FakeWinmmTimer(uint BeginResult, bool ThrowOnBegin = false)
    {
        public List<uint> BeginPeriods { get; } = [];

        public List<uint> EndPeriods { get; } = [];

        public uint Begin(uint period)
        {
            BeginPeriods.Add(period);
            // ReSharper disable once ConvertIfStatementToReturnStatement // Failure-injection seam: the throw is the injected behavior and must read as a standalone guard (B1 disposition).
            if (ThrowOnBegin) throw new DllNotFoundException("simulated winmm failure");
            return BeginResult;
        }

        public uint End(uint period)
        {
            EndPeriods.Add(period);
            return 0;
        }
    }
}
