using System.Collections;
using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Contracts.Json;

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
            case IJsonWritable writable:
                writable.WriteTo(writer);
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
