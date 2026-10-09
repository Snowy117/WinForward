using System.Globalization;

namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// One number as the report's messages spell it: the integer part, toward zero.
/// </summary>
/// <remarks>
/// The counts these messages interpolate are published as JSON numbers, so the value arrives as a
/// <see cref="double"/>. The integer part is what the message wants, and truncation toward zero is the
/// published behaviour. A value outside <see cref="long"/>'s range is not a count any campaign can
/// produce, so the narrowing cast is left unchecked.
/// </remarks>
internal static class JsonNumber
{
    /// <summary>The integer part of <paramref name="value"/>, toward zero, in the invariant culture.</summary>
    internal static string IntText(double value) =>
        ((long)Math.Truncate(value)).ToString(CultureInfo.InvariantCulture);
}
