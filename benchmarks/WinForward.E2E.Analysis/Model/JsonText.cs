using System.Globalization;
using System.Text.Json;

namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// A JSON value as Python's <c>str()</c> spells it, which is how a few reference messages interpolate a
/// record's own field: a number keeps the form it was written in, a boolean is <c>True</c>/<c>False</c>,
/// null is <c>None</c>, and a string is itself.
/// </summary>
internal static class JsonText
{
    /// <summary>The value as the reference's own string interpolation writes it.</summary>
    internal static string Of(JsonElement? element)
    {
        if (element is not { } value)
        {
            return "None";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Null or JsonValueKind.Undefined => "None",
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
