using System.Globalization;
using WinForward.E2E.Analysis.Json;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// Holds the analysis's numbers to the text it publishes: the fixed form of the table cells, the
/// exponential form of the gate's floor cells, the general form that keeps a small non-zero value
/// visible, and the round-trip text of the floats inside <c>verdict.json</c>. The decisions a gate
/// cannot see are pinned here value by value — the exact binary midpoints in particular — each with the
/// negative control that would move if the formatter used the neighbouring rule instead.
/// </summary>
public sealed class AnalyzerNumberGoldenTests
{
    /// <summary>
    /// The eight exact binary midpoints of the three-digit grid, with the answer beside each. Four of
    /// them round down to an even digit and four round up to one, so a formatter that rounded halves
    /// away from zero would be right about half the table and wrong about the rest.
    /// </summary>
    [Theory]
    [InlineData(0.0625, "0.062")]
    [InlineData(0.1875, "0.188")]
    [InlineData(0.3125, "0.312")]
    [InlineData(0.4375, "0.438")]
    [InlineData(0.5625, "0.562")]
    [InlineData(0.6875, "0.688")]
    [InlineData(0.8125, "0.812")]
    [InlineData(0.9375, "0.938")]
    public void TheMidpointsOfTheGridRoundToTheEvenNeighbour(double value, string expected) =>
        Assert.Equal(expected, VerbatimNumber.Fixed(value, 3));

    [Fact]
    public void TheMidpointTableTellsHalfToEvenFromHalfAwayFromZero()
    {
        // The negative control the table needs to be a test rather than a restatement: if the
        // formatter rounded halves away from zero, exactly these four cells would move. The frozen
        // tables hold such midpoints at no decimals, so the rule is not free to change.
        double[] midpoints = [0.0625, 0.1875, 0.3125, 0.4375, 0.5625, 0.6875, 0.8125, 0.9375];
        var moved = 0;
        foreach (var midpoint in midpoints)
        {
            var awayFromZero = Math.Round(midpoint, 3, MidpointRounding.AwayFromZero)
                .ToString("F3", CultureInfo.InvariantCulture);
            if (!string.Equals(awayFromZero, VerbatimNumber.Fixed(midpoint, 3), StringComparison.Ordinal))
            {
                moved++;
            }
        }

        Assert.Equal(4, moved);
    }

    /// <summary>
    /// The eight exact binary midpoints of the exponential form's grid, with the answer beside each:
    /// four round down to an even digit and four round up to one, the rule the fixed form follows too.
    /// </summary>
    [Theory]
    [InlineData(1.0625, "1.062e+00")]
    [InlineData(1.1875, "1.188e+00")]
    [InlineData(1.3125, "1.312e+00")]
    [InlineData(1.4375, "1.438e+00")]
    [InlineData(1.5625, "1.562e+00")]
    [InlineData(1.6875, "1.688e+00")]
    [InlineData(1.8125, "1.812e+00")]
    [InlineData(1.9375, "1.938e+00")]
    public void TheExponentialMidpointsRoundToTheEvenNeighbour(double value, string expected) =>
        Assert.Equal(expected, VerbatimNumber.Exponential(value, 3));

    [Fact]
    public void TheExponentialFormKeepsTwoExponentDigitsAndTheWritersSpelling()
    {
        // The gate's cell is this text plus its bound — `8.000e-03 < 1e-06`, the shape the frozen
        // tables carry — so the exponent is lower-cased and trimmed from `E3`'s three digits to two.
        Assert.Equal("8.000e-03", VerbatimNumber.Exponential(0.008, 3));
        Assert.Equal("1.000e-06", VerbatimNumber.Exponential(1e-06, 3));
        Assert.Equal("1.234e+03", VerbatimNumber.Exponential(1234.5, 3));
        Assert.Equal("1.000e+100", VerbatimNumber.Exponential(1e100, 3));
        Assert.Equal("0.000e+00", VerbatimNumber.Exponential(0.0, 3));
        Assert.Equal("-0.000e+00", VerbatimNumber.Exponential(-0.0, 3));
        Assert.Equal("NaN", VerbatimNumber.Exponential(double.NaN, 3));
        Assert.Equal("Infinity", VerbatimNumber.Exponential(double.PositiveInfinity, 3));
    }

    [Fact]
    public void TheExponentialFormTellsHalfToEvenFromTheCustomSpecifier()
    {
        // The negative control: `0.000e+00` is the custom specifier this cell used to carry, and it
        // rounds the decimal its own scaling produced away from zero, so exactly these four midpoints
        // would move.
        double[] midpoints = [1.0625, 1.1875, 1.3125, 1.4375, 1.5625, 1.6875, 1.8125, 1.9375];
        var moved = 0;
        foreach (var midpoint in midpoints)
        {
            if (!string.Equals(
                midpoint.ToString("0.000e+00", CultureInfo.InvariantCulture),
                VerbatimNumber.Exponential(midpoint, 3),
                StringComparison.Ordinal))
            {
                moved++;
            }
        }

        Assert.Equal(4, moved);
    }

    [Theory]
    [InlineData(1e-06, "1e-06")]
    [InlineData(1.2345e-07, "1.23e-07")]
    [InlineData(5e-324, "4.94e-324")]
    [InlineData(99999.0, "1e+05")]
    [InlineData(1e100, "1e+100")]
    [InlineData(0.008, "0.008")]
    [InlineData(0.000123456, "0.000123")]
    [InlineData(9.9999e-05, "0.0001")]
    [InlineData(1.7976931348623157e308, "1.8e+308")]
    public void TheGeneralFormChoosesItsShapeFromTheRoundedExponent(double value, string expected) =>
        Assert.Equal(expected, VerbatimNumber.General(value, 3));

    [Fact]
    public void ACellFallsBackToTheGeneralFormRatherThanPrintingANonZeroValueAsZero()
    {
        // The fixed form unless it would print a non-zero value as zero: 0.008 at three digits stays
        // plain because the fixed form keeps it; 0.0004 and 0.4 at no digits do not. The tables carry
        // the same shape in their ranges, so the fallback is what keeps '0 [0–0.5]' from becoming
        // '0 [0–0]' at no decimals.
        Assert.Equal("0.000", VerbatimNumber.Cell(0.0));
        Assert.Equal("-0.000", VerbatimNumber.Cell(-0.0));
        Assert.Equal("0.008", VerbatimNumber.Cell(0.008));
        Assert.Equal("0.0004", VerbatimNumber.Cell(0.0004));
        Assert.Equal("0.4", VerbatimNumber.Cell(0.4, digits: 0));
        Assert.Equal("1e-06", VerbatimNumber.Cell(1e-06));
        Assert.Equal("409600", VerbatimNumber.Cell(409600.0, digits: 0));
        Assert.Equal("12.345 ms", VerbatimNumber.Cell(12.3454, unit: " ms"));
        Assert.Equal("n/a", VerbatimNumber.Cell(null));
    }

    [Fact]
    public void TheFloatTextIsTheShortestThatReadsBack()
    {
        // Where .NET's round-trip text differs from Python's `repr`: an integral value keeps no `.0`,
        // and the exponent switches magnitude and case. Every one of these is a JSON number literal,
        // and the non-finite spellings are the ones JSON's own readers accept.
        Assert.Equal("1", VerbatimNumber.Json(1.0));
        Assert.Equal("-0", VerbatimNumber.Json(-0.0));
        Assert.Equal("100", VerbatimNumber.Json(100.0));
        Assert.Equal("1000000000000000", VerbatimNumber.Json(1e15));
        Assert.Equal("10000000000000000", VerbatimNumber.Json(1e16));
        Assert.Equal("1E+17", VerbatimNumber.Json(1e17));
        Assert.Equal("0.0001", VerbatimNumber.Json(0.0001));
        Assert.Equal("1E-05", VerbatimNumber.Json(0.00001));
        Assert.Equal("0.30000000000000004", VerbatimNumber.Json(0.1 + 0.2));
        Assert.Equal("NaN", VerbatimNumber.Json(double.NaN));
        Assert.Equal("Infinity", VerbatimNumber.Json(double.PositiveInfinity));
        Assert.Equal("-Infinity", VerbatimNumber.Json(double.NegativeInfinity));
    }
}
