using System.Globalization;

namespace WinForward.E2E.Analysis.Json;

/// <summary>
/// Python's <c>%.Ne</c>, which one gate cell prints: a lowercase exponent with a sign and at least two
/// digits, and the mantissa rounded to the requested number of places.
/// </summary>
internal static class PythonExponential
{
    /// <summary>One value in the reference's exponential form.</summary>
    internal static string Fixed(double value, int digits)
    {
        var text = value.ToString("E" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var marker = text.IndexOf('E', StringComparison.Ordinal);
        var mantissa = text[..marker];
        var exponent = int.Parse(text[(marker + 1)..], CultureInfo.InvariantCulture);
        var sign = exponent < 0 ? "-" : "+";
        return $"{mantissa}e{sign}{Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture)}";
    }
}
