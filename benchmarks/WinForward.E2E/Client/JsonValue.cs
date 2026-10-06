using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace WinForward.E2E.Client;

internal static class JsonValue
{
    internal static void WriteProperties(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var pair in values)
        {
            writer.WritePropertyName(pair.Key);
            Write(writer, pair.Value);
        }
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                WriteNumber(writer, number);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case IReadOnlyDictionary<string, object?> map:
                writer.WriteStartObject();
                WriteProperties(writer, map);
                writer.WriteEndObject();
                break;
            case IEnumerable items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    internal static double Round(double value, int digits = 3) =>
        double.IsFinite(value) ? Math.Round(value, digits, MidpointRounding.AwayFromZero) : value;

    internal static double Microseconds(long nanoseconds) => Round(nanoseconds / 1000.0);

    /// <summary>
    /// A rate over <paramref name="denominator"/> events, or null when there were none: a product
    /// that carried no datagrams has no loss rate, and rendering that as 0 would publish the best
    /// possible score for a measurement that never happened.
    /// </summary>
    internal static double? Ratio(long numerator, long denominator) =>
        denominator == 0 ? null : Round((double)numerator / denominator, 6);

    internal static double PerSecond(long count, long ticks, long frequency) =>
        ticks <= 0 ? 0 : Round(count * (double)frequency / ticks);

    private static void WriteNumber(Utf8JsonWriter writer, double number)
    {
        if (double.IsFinite(number))
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
