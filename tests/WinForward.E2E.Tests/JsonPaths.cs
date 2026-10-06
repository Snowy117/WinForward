using System.Text.Json;

namespace WinForward.E2E.Tests;

/// <summary>One occurrence of a canonical path in one record.</summary>
/// <param name="Kind">The JSON kind: object, array, null, string, number or boolean.</param>
/// <param name="Value">The scalar for a scalar kind; undefined for an object or an array.</param>
/// <param name="ArrayLength">The element count for an array; 0 otherwise.</param>
internal sealed record JsonPathObservation(string Kind, JsonElement Value, int ArrayLength);

/// <summary>
/// Flattens JSONL records into canonical paths, using the same alphabet as the inventory and the
/// comparison script (<c>benchmarks/WinForward.E2E/scripts/jsonl_paths.py</c>, D7 item 4/D14.6): a
/// path is its member names joined with '/', a dot inside a member name never splits, an array
/// contributes its own path once and its elements are flattened under that same path (no <c>[i]</c>
/// segment), and the document root has no path of its own.
/// </summary>
/// <remarks>
/// The two alphabets must agree, so a test compares an arm's published bytes with what
/// <c>ArmKeys</c> declares; this is the C# side of that pair. The Python side stays the reference for
/// the artifacts an operator reads.
/// </remarks>
internal static class JsonPaths
{
    internal const string KindObject = "object";
    internal const string KindArray = "array";
    internal const string KindNull = "null";

    private const string KindString = "string";
    private const string KindNumber = "number";
    private const string KindBoolean = "boolean";

    /// <summary>Flattens every record of a JSONL text, keyed by canonical path in document order.</summary>
    internal static Dictionary<string, List<JsonPathObservation>> FlattenJsonl(string text)
    {
        var observations = new Dictionary<string, List<JsonPathObservation>>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            Walk(document.RootElement, [], observations);
        }

        return observations;
    }

    /// <summary>The observed paths whose path starts with <paramref name="group"/>, in document order.</summary>
    internal static List<string> Under(IReadOnlyDictionary<string, List<JsonPathObservation>> observations, string group) =>
        [.. observations.Keys.Where(path => path.StartsWith($"{group}/", StringComparison.Ordinal))];

    /// <summary>The observed top-level paths of a record, in document order.</summary>
    internal static List<string> TopLevel(IReadOnlyDictionary<string, List<JsonPathObservation>> observations) =>
        [.. observations.Keys.Where(static path => !path.Contains('/', StringComparison.Ordinal))];

    private static void Walk(
        JsonElement node,
        List<string> segments,
        Dictionary<string, List<JsonPathObservation>> observations)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                Record(segments, KindObject, default, 0, observations);
                var depth = segments.Count;
                foreach (var property in node.EnumerateObject())
                {
                    segments.Add(property.Name);
                    Walk(property.Value, segments, observations);
                    segments.RemoveRange(depth, segments.Count - depth);
                }

                break;
            case JsonValueKind.Array:
                Record(segments, KindArray, default, node.GetArrayLength(), observations);
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, segments, observations);
                }

                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                Record(segments, KindOf(node), node.Clone(), 0, observations);
                break;
            default:
                throw new InvalidOperationException($"'{node.ValueKind}' is not a JSON value");
        }
    }

    private static void Record(
        List<string> segments,
        string kind,
        JsonElement value,
        int arrayLength,
        Dictionary<string, List<JsonPathObservation>> observations)
    {
        if (segments.Count == 0)
        {
            return;
        }

        var path = string.Join('/', segments);
        if (!observations.TryGetValue(path, out var found))
        {
            found = [];
            observations.Add(path, found);
        }

        found.Add(new JsonPathObservation(kind, value, arrayLength));
    }

    private static string KindOf(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => KindNull,
        JsonValueKind.String => KindString,
        JsonValueKind.Number => KindNumber,
        JsonValueKind.True or JsonValueKind.False => KindBoolean,
        _ => throw new InvalidOperationException($"'{node.ValueKind}' is not a JSON scalar"),
    };
}
