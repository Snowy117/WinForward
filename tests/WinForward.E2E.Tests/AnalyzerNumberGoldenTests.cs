using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Analysis.Json;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// Holds the analysis's number formatting to the text CPython produces. Every number in
/// <c>tables.md</c> is compared as text, so the two formatters have to agree on the digit *and* on the
/// shape: `%.*f` rounds halves to the even neighbour on the exact binary value, and `%.*g` picks
/// between the plain and the exponential form by the exponent the rounding produced, not the one the
/// value started with.
/// </summary>
public sealed class AnalyzerNumberGoldenTests
{
    [Fact]
    public void EveryGoldenFixedValueIsReproduced()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(RepoPaths.AnalyzerGolden("py-number-vectors.json")));

        var seen = 0;
        foreach (var entry in document.RootElement.GetProperty("fixed").EnumerateArray())
        {
            var expected = entry.GetProperty("text").GetString()!;
            Assert.Equal(
                expected,
                VerbatimNumber.Fixed(entry.GetProperty("value").GetDouble(), entry.GetProperty("digits").GetInt32()));
            seen++;
        }

        Assert.True(seen >= 200, $"the vector table holds {seen} fixed value(s)");
    }

    [Fact]
    public void EveryGoldenGeneralValueIsReproduced()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(RepoPaths.AnalyzerGolden("py-number-vectors.json")));

        var seen = 0;
        foreach (var entry in document.RootElement.GetProperty("general").EnumerateArray())
        {
            Assert.Equal(
                entry.GetProperty("text").GetString(),
                VerbatimNumber.General(
                    entry.GetProperty("value").GetDouble(),
                    entry.GetProperty("precision").GetInt32()));
            seen++;
        }

        Assert.True(seen >= 120, $"the vector table holds {seen} general value(s)");
    }

    [Fact]
    public void EveryGoldenFloatReprIsReproduced()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(RepoPaths.AnalyzerGolden("py-number-vectors.json")));

        var repr = document.RootElement.GetProperty("repr");
        var seen = 0;
        foreach (var entry in repr.GetProperty("finite").EnumerateArray())
        {
            Assert.Equal(entry.GetProperty("text").GetString(), VerbatimNumber.Json(entry.GetProperty("value").GetDouble()));
            seen++;
        }

        foreach (var entry in repr.GetProperty("named").EnumerateArray())
        {
            var value = double.Parse(entry.GetProperty("value").GetString()!, CultureInfo.InvariantCulture);
            Assert.Equal(entry.GetProperty("text").GetString(), VerbatimNumber.Json(value));
            seen++;
        }

        Assert.True(seen >= 36, $"the vector table holds {seen} float(s)");
    }

    /// <summary>
    /// The eight exact binary midpoints of the three-digit grid, with the reference's answer beside
    /// each. Four of them round down to an even digit and four round up to one, so a formatter that
    /// rounded halves away from zero would be right about half the table and wrong about the rest.
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
        // formatter rounded halves away from zero, exactly these four cells would move.
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
        // `fmt_num`: the fixed form unless it would print a non-zero value as zero. 0.008 at three
        // digits stays plain because the fixed form keeps it; 0.0004 and 0.4 at no digits do not.
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
    public void TheReprKeepsThePointOfAnIntegralValueAndItsOwnExponentRange()
    {
        // The two places .NET's shortest form differs from Python's: it drops the `.0`, and it
        // switches to an exponent a decade earlier.
        Assert.Equal("1.0", VerbatimNumber.Json(1.0));
        Assert.Equal("100.0", VerbatimNumber.Json(100.0));
        Assert.Equal("1000000000000000.0", VerbatimNumber.Json(1e15));
        Assert.Equal("1e+16", VerbatimNumber.Json(1e16));
        Assert.Equal("0.0001", VerbatimNumber.Json(0.0001));
        Assert.Equal("1e-05", VerbatimNumber.Json(0.00001));
        Assert.Equal("-0.0", VerbatimNumber.Json(-0.0));
        Assert.Equal("100.0", VerbatimNumber.Json(1e2));
        Assert.Equal("0.30000000000000004", VerbatimNumber.Json(0.1 + 0.2));
    }
}
