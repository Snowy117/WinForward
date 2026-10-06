using System.Text.Json;

namespace WinForward.E2E.Contracts.Json;

/// <summary>
/// Publishes a reading that is a number or nothing at all: the finite value, or JSON
/// <see langword="null"/> when the value is unknown or overflowed.
/// </summary>
/// <remarks>
/// The rule is the record's, not the caller's: a rate that had no duration behind it is not a measured
/// zero, and a non-finite double is not JSON at all, so both publish the same <see langword="null"/>.
/// </remarks>
internal static class Reading
{
    /// <summary>Writes <paramref name="value"/> under <paramref name="name"/>, or a null value.</summary>
    internal static void Write(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number))
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
