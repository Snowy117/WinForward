namespace WinForward.E2E.Contracts.Json;

/// <summary>
/// A count over an elapsed duration, in the shape the records publish it: three decimals, or
/// <see langword="null"/> when the duration is not a positive number of ticks.
/// </summary>
/// <remarks>
/// The type carries the <c>Json</c> prefix because C# forbids a method named after its own type
/// (CS0542) while the callers spell the published name.
/// </remarks>
public static class JsonPerSecond
{
    /// <summary>
    /// <paramref name="count"/> events over <paramref name="ticks"/> of <paramref name="frequency"/>,
    /// or null when no time passed: a duration of zero is the same missing measurement an empty
    /// denominator is for <see cref="JsonRate.Rate"/>, and it is never a measured zero rate.
    /// </summary>
    public static double? PerSecond(long count, long ticks, long frequency) =>
        ticks <= 0 ? null : NumberFormat.Round(count * (double)frequency / ticks);
}
