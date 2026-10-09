using WinForward.E2E.Contracts.Json;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The two rates the records publish. Both answer <c>null</c> for a measurement that could not be
/// taken, and the difference between a missing rate and a zero rate is the difference between "not
/// measured" and "measured perfectly", which every consumer of the JSONL contract relies on.
/// </summary>
public sealed class JsonRateTests
{
    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(1, 0, null)]
    [InlineData(-1, 0, null)]
    [InlineData(0, 5, 0.0)]
    [InlineData(1, 2, 0.5)]
    [InlineData(1, 3, 0.333333)]
    [InlineData(2, 3, 0.666667)]
    [InlineData(1, 6, 0.166667)]
    [InlineData(1, 7, 0.142857)]
    [InlineData(5, 4, 1.25)]
    public void RateIsSixDecimalsOrNull(long numerator, long denominator, double? expected) =>
        Assert.Equal(expected, JsonRate.Rate(numerator, denominator));

    [Theory]
    [InlineData(1000, 500, 1000, 2000.0)]
    [InlineData(0, 500, 1000, 0.0)]
    [InlineData(1, 6, 1, 0.167)]
    [InlineData(1, 3, 10, 3.333)]
    [InlineData(3, 7, 10, 4.286)]
    [InlineData(123, 1000, 1000, 123.0)]
    public void PerSecondIsTheCountedRate(long count, long ticks, long frequency, double expected) =>
        Assert.Equal(expected, JsonPerSecond.PerSecond(count, ticks, frequency));

    // A rate needs a duration to exist, exactly as Rate needs a population: a run with no elapsed
    // time has no measurement to publish, so the record writes null. Zero is the answer of a run
    // that measured perfectly, which is why the two must not share a value.
    [Theory]
    [InlineData(100, 0, 1000)]
    [InlineData(100, -1, 1000)]
    [InlineData(0, 0, 0)]
    [InlineData(100, 0, 0)]
    public void NoElapsedTimeIsNullRatherThanAZeroRate(long count, long ticks, long frequency) =>
        Assert.Null(JsonPerSecond.PerSecond(count, ticks, frequency));

    [Fact]
    public void PerSecondUsesThreeDecimalsRatherThanTheRatiosSix()
    {
        Assert.Equal(0.167, JsonPerSecond.PerSecond(1, 6, 1));
        Assert.Equal(0.166667, JsonRate.Rate(1, 6));
    }
}
