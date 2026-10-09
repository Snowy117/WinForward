using System.Globalization;

namespace WinForward.E2E.Analysis.Json;

/// <summary>
/// The numbers the analysis publishes: a fixed number of decimals for the table cells, the general form
/// that keeps a small non-zero value visible where the fixed form would print it as zero, and the
/// shortest text that reads back as the same value for the floats inside <c>verdict.json</c>.
/// </summary>
/// <remarks>
/// <para>These are .NET's formatting rules. <see cref="Fixed"/> is .NET's fixed-point formatting, which
/// rounds the exact binary value to the nearest digit with a midpoint going to the even one, and the
/// reference's <c>%.*f</c> rounds the same way — the frozen <c>tables.md</c> reproduces cell for cell,
/// midpoints included. <see cref="General"/> is <c>G</c> with the exponent lower-cased, and
/// <see cref="Json"/> is the round-trip form, which is where the published text of
/// <c>verdict.json</c>'s floats parts company with the reference's <c>repr</c>; the oracle compares
/// those numbers as values.</para>
/// <para>A value that is not a number at all is written the way .NET spells it (<c>NaN</c>,
/// <c>Infinity</c>, <c>-Infinity</c>) rather than the way <c>printf</c> does.</para>
/// <para>Public because the test project drives it against the reference's own vector table
/// (<c>verification/golden/py-number-vectors.json</c>); D20.6 keeps the analyzer free of an
/// <c>InternalsVisibleTo</c>.</para>
/// </remarks>
public static class VerbatimNumber
{
    /// <summary>How many significant digits the general form keeps where it stands in for a fixed form.</summary>
    private const int FallbackPrecision = 3;

    /// <summary>The characters a rendered number consists of when it reads as zero.</summary>
    private const string ZeroText = "0.-";

    /// <summary>One value with <paramref name="digits"/> decimals after the point.</summary>
    /// <param name="value">The value to write.</param>
    /// <param name="digits">How many digits to keep after the point.</param>
    /// <returns>The text, without a unit.</returns>
    public static string Fixed(double value, int digits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(digits);

        var format = "F" + digits.ToString(CultureInfo.InvariantCulture);
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One value in the general form with <paramref name="precision"/> significant digits: the plain
    /// spelling where the exponent allows it and the exponential one otherwise, with the trailing zeros
    /// of the significand dropped and the exponent in lower case with at least two digits
    /// (<c>1e-06</c>, <c>1.23e+03</c>).
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <param name="precision">How many significant digits to keep; zero means one.</param>
    /// <returns>The text, without a unit.</returns>
    public static string General(double value, int precision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(precision);

        var digits = precision == 0 ? 1 : precision;
        var format = "G" + digits.ToString(CultureInfo.InvariantCulture);
        return value.ToString(format, CultureInfo.InvariantCulture).Replace('E', 'e');
    }

    /// <summary>
    /// One number as a table cell: the fixed form, except where that form would print a non-zero value
    /// as zero, in which case the general form keeps the value visible instead of rounding it away.
    /// </summary>
    /// <param name="value">The value to write; a JSON null reading has none.</param>
    /// <param name="digits">How many digits the fixed form keeps after the point.</param>
    /// <param name="unit">The unit to append, with no separator.</param>
    /// <returns>The cell's text.</returns>
    public static string Cell(double? value, int digits = 3, string unit = "")
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (value is null)
        {
            return "n/a";
        }

        var text = Fixed(value.Value, digits);
#pragma warning disable S1244 // An exact zero, either sign: it keeps the fixed form's zeros.
        if (value.Value != 0 && ReadsAsZero(text))
#pragma warning restore S1244
        {
            text = General(value.Value, FallbackPrecision);
        }

        return text + unit;
    }

    /// <summary>
    /// One float as the shortest text that reads back as the same double, which is what a number inside
    /// <c>verdict.json</c> is published as: <c>1</c>, <c>0.5</c>, <c>1E-05</c>, <c>1E+17</c>. A
    /// non-finite value keeps the spelling JSON's own readers accept (<c>NaN</c>, <c>Infinity</c>,
    /// <c>-Infinity</c>).
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <returns>The text, which is a JSON number literal for every finite value.</returns>
    public static string Json(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a rendered number reads as zero: a text built only from zeros, a point and a sign. That
    /// is the same set as the numbers whose digits are all zero, which is all this has to answer yes to.
    /// </summary>
    private static bool ReadsAsZero(string text) => text.AsSpan().Trim(ZeroText).IsEmpty;
}
