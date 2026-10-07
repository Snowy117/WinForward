using System.Text.Json;

namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// Reads a member out of a parsed JSON object the way the reference's <c>dict.get</c> does: a missing
/// key and a key whose value is JSON null are both "no reading", and a value that is not of the asked-for
/// kind is no reading either.
/// </summary>
/// <remarks>
/// <para><b>Missing and null are the same answer here, and a different answer elsewhere.</b> The
/// reference has two walks: one that cannot tell them apart (used by every cell that falls back to a
/// default) and one that can, because a rate the harness wrote as JSON null is a zero denominator and
/// is printed as an empty cell rather than as a zero. This class serves the first; the second belongs
/// with the metric extractors that need it.</para>
/// <para><b>Truthiness is Python's.</b> <c>sample.get("self") is True</c> is an identity test against
/// the boolean, while <c>not sample.get("readError")</c> accepts any falsy value; both spellings appear
/// in the reference and they are not the same test, so they are not the same method here.</para>
/// </remarks>
internal static class JsonValue
{
    /// <summary>The member as a string, or null when it is missing, null or not a string.</summary>
    internal static string? String(JsonElement? element, string name)
    {
        var member = Member(element, name);
        return member is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    }

    /// <summary>Whether the member is the JSON boolean <see langword="true"/> and nothing else.</summary>
    internal static bool IsTrue(JsonElement? element, string name) =>
        Member(element, name) is { ValueKind: JsonValueKind.True };

    /// <summary>Whether the member is truthy in Python's sense: not <see langword="false"/>, not <see langword="null"/>, not zero, not empty, not absent.</summary>
    internal static bool Truthy(JsonElement? element, string name) => IsTruthy(Member(element, name));

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

    private static bool IsTruthy(JsonElement? element)
    {
        if (element is not { } value)
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => value.GetString() is { Length: > 0 },
            JsonValueKind.Number => value.GetDouble().CompareTo(0.0) != 0,
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => false,
        };
    }
}
