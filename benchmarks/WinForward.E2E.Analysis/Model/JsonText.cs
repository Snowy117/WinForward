using System.Globalization;
using System.Text.Json;

namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// A JSON value as the analysis spells it when a message quotes a record's own field: a number keeps the
/// form it was written in, a boolean is <see langword="true"/>/<see langword="false"/>, null is
/// <see langword="null"/>, and a string is itself.
/// </summary>
internal static class JsonText
{
    /// <summary>The value as JSON spells it, which is what a message interpolating the field carries.</summary>
    internal static string Of(JsonElement? element)
    {
        if (element is not { } value)
        {
            return "null";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => "null",
            JsonValueKind.Number => Number(value),
            _ => value.GetRawText(),
        };
    }

    /// <summary>Whether two values are the same value, comparing scalars by their text.</summary>
    internal static bool Same(JsonElement? left, JsonElement? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(left.Value.GetRawText(), right.Value.GetRawText(), StringComparison.Ordinal);
    }

    private static string Number(JsonElement value)
    {
        var raw = value.GetRawText();
        return raw.Contains('.', StringComparison.Ordinal)
            || raw.Contains('e', StringComparison.Ordinal)
            || raw.Contains('E', StringComparison.Ordinal)
            ? value.GetDouble().ToString("R", CultureInfo.InvariantCulture)
            : raw;
    }
}
