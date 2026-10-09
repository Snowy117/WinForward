namespace WinForward.E2E.Contracts.Json;

/// <summary>
/// A share of a population, in the shape the records publish it: six decimals, or <see langword="null"/> when
/// the population was empty.
/// </summary>
/// <remarks>
/// The type carries the <c>Json</c> prefix because C# forbids a method named after its own type
/// (CS0542) while the callers spell the published name.
/// </remarks>
public static class JsonRate
{
    /// <summary>
    /// A rate over <paramref name="denominator"/> events, or null when there were none: a product
    /// that carried no datagrams has no loss rate, and rendering that as 0 would publish the best
    /// possible score for a measurement that never happened.
    /// </summary>
    public static double? Rate(long numerator, long denominator) =>
        denominator == 0 ? null : NumberFormat.Round((double)numerator / denominator, 6);
}
