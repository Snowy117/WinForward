namespace WinForward.E2E.Contracts.Json;

/// <summary>
/// Formats the numbers the records publish. The digit counts and the handling of a non-finite
/// <see cref="double"/> are contract, not implementation detail: every consumer reads these values
/// as text.
/// </summary>
/// <remarks>
/// The type carries the <c>Json</c> prefix because C# forbids a method named after its own type
/// (CS0542) while the callers spell the published names.
/// </remarks>
public static class NumberFormat
{
    /// <summary>
    /// Rounds to <paramref name="decimals"/> places, away from zero at a midpoint, and returns a
    /// non-finite value unchanged rather than throwing: a measurement that overflowed to infinity
    /// reaches the record writer, which publishes a non-finite number as <see langword="null"/>.
    /// </summary>
    public static double Round(double value, int decimals = 3) =>
        double.IsFinite(value) ? Math.Round(value, decimals, MidpointRounding.AwayFromZero) : value;

    /// <summary>Converts a nanosecond count to microseconds, rounded to three places.</summary>
    public static double Microseconds(long nanoseconds) => Round(nanoseconds / 1000.0);
}
