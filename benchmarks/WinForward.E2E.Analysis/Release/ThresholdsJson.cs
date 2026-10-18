using System.Globalization;
using System.Text.Json;

namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// The document reader a thresholds file is walked with: it matches every object's members against the
/// keys the schema declares and turns a malformed value into a message that names its own field.
/// </summary>
/// <remarks>
/// <para><b>Unknown and duplicate keys are refused, not ignored.</b> A thresholds file is the
/// pre-registered contract behind a release claim, so a key the parser does not understand is far more
/// likely to be a misspelt decision than harmless metadata; dropping it silently would let the file say
/// one thing and the run do another. <see cref="JsonElement.EnumerateObject"/> is what makes the check
/// possible — <c>GetProperty</c> would return the first of two spellings and hide the second.</para>
/// <para><b>A field is spelled as a dotted path.</b> <c>caliber.sha256</c> and <c>primary[0].band</c>
/// are the names a caller sees in an error, and they are the paths the file itself can be searched
/// for.</para>
/// </remarks>
internal static class ThresholdsJson
{
    internal const string Prefix = "thresholds: ";

    /// <summary>One field of a dotted path: <c>caliber</c> and <c>seconds</c> joined is <c>caliber.seconds</c>.</summary>
    internal static string Join(string parent, string name) => parent.Length == 0 ? name : $"{parent}.{name}";

    /// <summary>One array element's path: <c>primary[0]</c>.</summary>
    internal static string Indexed(string name, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{name}[{index}]");

    /// <summary>
    /// Reads an object's members, refusing a key the schema does not declare and a key written twice.
    /// </summary>
    /// <param name="element">The JSON value that has to be an object.</param>
    /// <param name="path">The path the object itself was reached at; empty for the document root.</param>
    /// <param name="allowed">The member names the schema declares for this object.</param>
    /// <param name="members">The object's members, when it is accepted.</param>
    /// <param name="error">Why it was refused, when it was.</param>
    internal static bool TryMembers(
        JsonElement element,
        string path,
        string[] allowed,
        out Dictionary<string, JsonElement>? members,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(allowed);

        members = null;
        error = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = path.Length == 0
                ? Prefix + "the document is not a JSON object"
                : $"{Prefix}'{path}' is {Spelling(element)}, not a JSON object";
            return false;
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var member in element.EnumerateObject())
        {
            var memberPath = Join(path, member.Name);
            if (!values.TryAdd(member.Name, member.Value))
            {
                error = $"{Prefix}duplicate key '{memberPath}'";
                return false;
            }

            if (Array.IndexOf(allowed, member.Name) < 0)
            {
                error = $"{Prefix}unknown key '{memberPath}'";
                return false;
            }
        }

        members = values;
        return true;
    }

    /// <summary>One required member of an already-read object.</summary>
    internal static bool TryRequired(
        Dictionary<string, JsonElement> members,
        string name,
        string path,
        out JsonElement value,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(members);

        if (members.TryGetValue(name, out value))
        {
            error = null;
            return true;
        }

        error = $"{Prefix}'{path}' is required";
        return false;
    }

    /// <summary>One required member of a named field that has to be a string, and not the empty string.</summary>
    internal static bool TryReadRequiredString(
        Dictionary<string, JsonElement> members,
        string name,
        string parentPath,
        out string value,
        out string? error)
    {
        var path = Join(parentPath, name);
        if (!TryRequired(members, name, path, out var element, out error)
            || !TryString(element, path, out value, out error))
        {
            value = string.Empty;
            return false;
        }

        if (value.Length > 0)
        {
            return true;
        }

        error = $"{Prefix}'{path}' is empty";
        return false;
    }

    /// <summary>One required member of a named field that has to be a number greater than zero.</summary>
    internal static bool TryReadPositiveNumber(
        Dictionary<string, JsonElement> members,
        string name,
        string parentPath,
        out double value,
        out string? error)
    {
        var path = Join(parentPath, name);
        if (!TryRequired(members, name, path, out var element, out error)
            || !TryNumber(element, path, out value, out error))
        {
            value = 0;
            return false;
        }

        if (value > 0)
        {
            return true;
        }

        error = string.Create(CultureInfo.InvariantCulture, $"{Prefix}'{path}' must be > 0 (was {value})");
        return false;
    }

    /// <summary>An optional member of a named field that has to be a number greater than or equal to zero.</summary>
    internal static bool TryReadOptionalCount(
        Dictionary<string, JsonElement> members,
        string name,
        string path,
        out double? value,
        out string? error)
    {
        value = null;
        error = null;
        if (!members.TryGetValue(name, out var element))
        {
            return true;
        }

        if (!TryNumber(element, path, out var number, out error))
        {
            return false;
        }

        if (number >= 0)
        {
            value = number;
            return true;
        }

        error = string.Create(CultureInfo.InvariantCulture, $"{Prefix}'{path}' must be >= 0 (was {number})");
        return false;
    }

    /// <summary>An optional member of a named field that has to be an integer at or above a floor.</summary>
    internal static bool TryReadOptionalInteger(
        Dictionary<string, JsonElement> members,
        string name,
        string path,
        int minimum,
        out int? value,
        out string? error)
    {
        value = null;
        error = null;
        if (!members.TryGetValue(name, out var element))
        {
            return true;
        }

        if (!TryInt(element, path, out var number, out error))
        {
            return false;
        }

        if (number >= minimum)
        {
            value = number;
            return true;
        }

        error = string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}'{path}' is {number}, below the minimum of {minimum}");
        return false;
    }

    /// <summary>One value that has to be a string.</summary>
    internal static bool TryString(JsonElement element, string path, out string value, out string? error)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            error = null;
            return true;
        }

        value = string.Empty;
        error = $"{Prefix}'{path}' is {Spelling(element)}, which is not a string";
        return false;
    }

    /// <summary>One value that has to be an integer.</summary>
    internal static bool TryInt(JsonElement element, string path, out int value, out string? error)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
        {
            error = null;
            return true;
        }

        value = 0;
        error = $"{Prefix}'{path}' is {Spelling(element)}, which is not an integer";
        return false;
    }

    /// <summary>One value that has to be a finite number.</summary>
    internal static bool TryNumber(JsonElement element, string path, out double value, out string? error)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value) && double.IsFinite(value))
        {
            error = null;
            return true;
        }

        value = 0;
        error = $"{Prefix}'{path}' is {Spelling(element)}, which is not a finite number";
        return false;
    }

    /// <summary>One value that has to be a boolean.</summary>
    internal static bool TryBool(JsonElement element, string path, out bool value, out string? error)
    {
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.ValueKind == JsonValueKind.True;
            error = null;
            return true;
        }

        value = false;
        error = $"{Prefix}'{path}' is {Spelling(element)}, which is not a boolean";
        return false;
    }

    /// <summary>One value that has to be an array.</summary>
    internal static bool RequireArray(JsonElement element, string path, out string? error)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            error = null;
            return true;
        }

        error = $"{Prefix}'{path}' is {Spelling(element)}, not an array";
        return false;
    }

    /// <summary>One value as an error message can quote it.</summary>
    private static string Spelling(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };
}
