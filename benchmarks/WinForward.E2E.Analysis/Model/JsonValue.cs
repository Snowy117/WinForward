using System.Text.Json;

namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// Reads a member out of a parsed JSON object the way the reference's <c>dict.get</c> does: a missing
/// key and a key whose value is JSON null are both "no reading", and a value that is not of the asked-for
/// kind is no reading either.
/// </summary>
/// <remarks>
/// <para><b>Missing and null are the same answer in <see cref="Dig"/> and different ones in
/// <see cref="DigPresent"/>.</b> A rate the harness wrote as JSON null is a zero denominator and is
/// printed as an empty cell rather than as a zero, so the two walks are not the same walk.</para>
/// <para><b>A path is split on <c>/</c> only.</b> The harness publishes keys that contain a dot
/// (<c>metrics["udp.sent"]</c>) next to nested objects, so a dotted key stays one step.</para>
/// </remarks>
internal static class JsonValue
{
    /// <summary>The reference's path separator inside one record.</summary>
    internal const char Separator = '/';

    /// <summary>The member as a string, or null when it is missing, null or not a string.</summary>
    internal static string? String(JsonElement? element, string name)
    {
        var member = Member(element, name);
        return member is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    }

    /// <summary>The member as a number, or null when it is missing, null or not a JSON number.</summary>
    internal static double? Number(JsonElement? element, string name) => AsNumber(Member(element, name));

    /// <summary>One value as a number, or null when it is not a JSON number.</summary>
    internal static double? AsNumber(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number) ? number : null;

    /// <summary>Whether the member is the JSON boolean <see langword="true"/> and nothing else.</summary>
    internal static bool IsTrue(JsonElement? element, string name) =>
        Member(element, name) is { ValueKind: JsonValueKind.True };

    /// <summary>The member itself, or null when the object has no such member.</summary>
    internal static JsonElement? Member(JsonElement? element, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var member))
        {
            return member;
        }

        return null;
    }

    /// <summary>The member's elements, or null when it is missing or not an array.</summary>
    internal static List<JsonElement>? Array(JsonElement? element, string name)
    {
        var member = Member(element, name);
        return member is { ValueKind: JsonValueKind.Array } value ? [.. value.EnumerateArray()] : null;
    }

    /// <summary>The value at a <c>/</c>-separated path, with a missing key and a JSON null both reading as no value.</summary>
    internal static JsonElement? Dig(JsonElement? element, string path)
    {
        var (present, value) = DigPresent(element, path);
        return present && value is { ValueKind: not JsonValueKind.Null } found ? found : null;
    }

    /// <summary>The value at a <c>/</c>-separated path, reporting a JSON null as present.</summary>
    internal static (bool Present, JsonElement? Value) DigPresent(JsonElement? element, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var current = element;
        foreach (var part in path.Split(Separator))
        {
            if (current is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(part, out var member))
            {
                return (false, null);
            }

            current = member;
        }

        return (true, current);
    }
}
