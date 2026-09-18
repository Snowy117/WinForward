using System.Diagnostics;
using WinForward.Benchmarks.Stability;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class GcSoakScenarioTests
{
    [Fact]
    public void GcSoakScenarioDefaultsToThirtyMinutes()
    {
        var options = SoakOptions.Parse(["--scenario", "gc-soak"]);
        Assert.Equal(SoakScenario.GcSoak, options.Scenario);
        Assert.Equal(1_800, SoakOptions.GcSoakDefaultDurationSeconds);
        Assert.Equal(SoakOptions.GcSoakDefaultDurationSeconds, options.DurationSeconds);
    }

    [Fact]
    public void DurationOverridesTheGcSoakDefault()
    {
        Assert.Equal(5, SoakOptions.Parse(["--scenario", "gc-soak", "--duration", "5"]).DurationSeconds);
    }

    [Fact]
    public void QuickOverridesTheGcSoakDefault()
    {
        var options = SoakOptions.Parse(["--scenario", "gc-soak", "--quick"]);
        Assert.True(options.Quick);
        Assert.Equal(15, options.DurationSeconds);
    }

    [Fact]
    public void DurationBeforeTheScenarioStillOverridesTheGcSoakDefault()
    {
        Assert.Equal(5, SoakOptions.Parse(["--duration", "5", "--scenario", "gc-soak"]).DurationSeconds);
    }

    [Fact]
    public void QuickBeforeTheScenarioStillOverridesTheGcSoakDefault()
    {
        Assert.Equal(15, SoakOptions.Parse(["--quick", "--scenario", "gc-soak"]).DurationSeconds);
    }

    [Theory]
    [InlineData("gc-soak")]
    [InlineData("gcsoak")]
    [InlineData("GC-SOAK")]
    public void GcSoakTokenIsAccepted(string token)
    {
        Assert.Equal(SoakScenario.GcSoak, SoakOptions.Parse(["--scenario", token]).Scenario);
    }

    [Fact]
    public void OtherScenariosKeepTheirDefaultDuration()
    {
        Assert.Equal(60, SoakOptions.Parse(["--scenario", "udp"]).DurationSeconds);
        Assert.Equal(60, SoakOptions.Parse([]).DurationSeconds);
        Assert.DoesNotContain(SoakRunner.SelectScenarios(SoakScenario.All), entry => string.Equals(entry.Name, "gcSoak", StringComparison.Ordinal));
    }

    [Fact]
    public void GcSoakSelectionExpandsToTheGcSoakScenario()
    {
        var scenarios = SoakRunner.SelectScenarios(SoakScenario.GcSoak);
        var entry = Assert.Single(scenarios);
        Assert.Equal("gcSoak", entry.Name);
    }

    [Fact]
    public void AllSelectionContainsTheStandardScenariosButNotGcSoak()
    {
        var names = SoakRunner.SelectScenarios(SoakScenario.All).Select(entry => entry.Name).ToArray();
        Assert.Contains("udp", names);
        Assert.Contains("tcp", names);
        Assert.Contains("tcpThroughput", names);
        Assert.Contains("footprint", names);
        Assert.DoesNotContain("gcSoak", names);
    }

    [Theory]
    [InlineData("gc_soak")]
    [InlineData("gcsoakd")]
    [InlineData("soak")]
    public void UnknownScenarioTokensAreRejected(string token)
    {
        Assert.Throws<ArgumentException>(() => SoakOptions.Parse(["--scenario", token]));
    }

    [Fact]
    public void SenderAllocationAllowanceToleratesBclNoiseButCatchesPerDatagramLeaks()
    {
        const long canonicalSends = 432_000;
        // BCL lock-contention noise observed in the gate is a few hundred bytes; it must pass.
        Assert.True(300 <= GcSoakScenario.SenderAllocationAllowance(canonicalSends));
        // A real per-datagram allocation (the 72 B/datagram regression this gate exists to catch)
        // must exceed the allowance by orders of magnitude.
        Assert.True(72 * canonicalSends > GcSoakScenario.SenderAllocationAllowance(canonicalSends));
        // The allowance never drops below the absolute noise ceiling.
        Assert.Equal(GcSoakScenario.SenderAllocationNoiseCeilingBytes, GcSoakScenario.SenderAllocationAllowance(0));
    }

    [Fact]
    public void WorkingSetSlopeIsZeroForAFlatSeries()
    {
        long[] timestamps = [0, 100, 200, 300];
        long[] workingSet = [1_000, 1_000, 1_000, 1_000];
        Assert.Equal(0.0, GcSoakScenario.WorkingSetSlopeBytesPerSecond(timestamps, workingSet), 6);
    }

    [Fact]
    public void WorkingSetSlopeDetectsAGrowthTrend()
    {
        long[] timestamps = [0, Stopwatch.Frequency, 2 * Stopwatch.Frequency, 3 * Stopwatch.Frequency];
        long[] workingSet = [0, 1_000, 2_000, 3_000];
        Assert.Equal(1_000.0, GcSoakScenario.WorkingSetSlopeBytesPerSecond(timestamps, workingSet), 6);
    }

    [Fact]
    public void WorkingSetSlopeDetectsAShrinkTrend()
    {
        long[] timestamps = [0, Stopwatch.Frequency, 2 * Stopwatch.Frequency, 3 * Stopwatch.Frequency];
        long[] workingSet = [3_000, 2_000, 1_000, 0];
        Assert.Equal(-1_000.0, GcSoakScenario.WorkingSetSlopeBytesPerSecond(timestamps, workingSet), 6);
    }

    [Fact]
    public void WorkingSetSlopeRequiresAtLeastTwoPairedSamples()
    {
        Assert.Equal(0.0, GcSoakScenario.WorkingSetSlopeBytesPerSecond([1], [1]), 6);
        Assert.Equal(0.0, GcSoakScenario.WorkingSetSlopeBytesPerSecond([0, 1], [1]), 6);
    }
}
