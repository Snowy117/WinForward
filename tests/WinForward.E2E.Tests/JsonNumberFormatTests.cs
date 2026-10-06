using WinForward.E2E.Contracts.Json;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// Value snapshots for the two number formats the records publish. Both are read as text by every
/// consumer of the JSONL contract, so these assertions are the contract's digit counts and rounding
/// direction rather than a description of the implementation.
/// </summary>
public sealed class JsonNumberFormatTests
{
    // Midpoints round away from zero: Math.Round's default banker's rounding would answer 2 and -2
    // here, and the difference is visible in every published rate that lands on a midpoint.
    [Theory]
    [InlineData(2.5, 0, 3)]
    [InlineData(-2.5, 0, -3)]
    [InlineData(0.125, 2, 0.13)]
    [InlineData(1.0 / 3.0, 3, 0.333)]
    [InlineData(1.0 / 6.0, 3, 0.167)]
    [InlineData(1.23451, 3, 1.235)]
    [InlineData(1.23449, 3, 1.234)]
    [InlineData(1.0 / 6.0, 6, 0.166667)]
    [InlineData(2.0 / 3.0, 6, 0.666667)]
    [InlineData(1.23456, 4, 1.2346)]
    [InlineData(1.23454, 4, 1.2345)]
    public void RoundUsesThePublishedDigitCount(double value, int decimals, double expected) =>
        Assert.Equal(expected, NumberFormat.Round(value, decimals));

    [Fact]
    public void RoundingDefaultsToThreeDecimals() =>
        Assert.Equal(0.167, NumberFormat.Round(1.0 / 6.0));

    [Fact]
    public void NonFiniteValuesPassThroughInsteadOfThrowingAtTheWriter()
    {
        // The record writer turns a non-finite number into null, so a measurement that overflowed
        // has to reach it as a number rather than as an exception from the rounding step.
        Assert.True(double.IsNaN(NumberFormat.Round(double.NaN)));
        Assert.Equal(double.PositiveInfinity, NumberFormat.Round(double.PositiveInfinity));
        Assert.Equal(double.NegativeInfinity, NumberFormat.Round(double.NegativeInfinity, 6));
        Assert.Equal(double.PositiveInfinity, NumberFormat.Round(1.0 / 0.0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0.001)]
    [InlineData(999, 0.999)]
    [InlineData(1500, 1.5)]
    [InlineData(1234, 1.234)]
    [InlineData(-1500, -1.5)]
    [InlineData(1234567, 1234.567)]
    public void MicrosecondsConvertsNanosecondsToThreeDecimalMicroseconds(long nanoseconds, double expected) =>
        Assert.Equal(expected, NumberFormat.Microseconds(nanoseconds));
}
