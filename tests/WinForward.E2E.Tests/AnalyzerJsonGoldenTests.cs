using System.Text.Json;
using WinForward.E2E.Analysis.Json;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// Holds the analysis's JSON text to the bytes it publishes: <c>json.dumps(value, indent=2,
/// sort_keys=False)</c> and one newline, which is what the frozen reference wrote for the same
/// documents. Each golden case is a document and that text; the test rebuilds it through the writer's
/// own entry points, so the escaping, the nesting and the insertion order are all exercised on values
/// rather than on fragments.
/// </summary>
public sealed class AnalyzerJsonGoldenTests
{
    [Fact]
    public void EveryGoldenDocumentIsReproduced()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(RepoPaths.AnalyzerGolden("py-json-vectors.json")));

        var cases = 0;
        var floats = 0;
        foreach (var entry in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            var value = entry.GetProperty("value");
            var rendered = Render(value, 0, name);
            if (HoldsFloat(value))
            {
                // A float's digits are this writer's own (`VerbatimJson.Number` is .NET's round-trip
                // text, not Python's `repr`), so a case that carries one is held to the reference's
                // structure and wording with its numbers compared as values. The text itself is pinned
                // in AnalyzerNumberGoldenTests.
                using var parsed = JsonDocument.Parse(rendered);
                Assert.True(JsonElement.DeepEquals(value, parsed.RootElement));
                floats++;
            }
            else
            {
                Assert.Equal(entry.GetProperty("text").GetString(), rendered);
            }

            cases++;
        }

        Assert.True(cases >= 10, $"the vector table holds {cases} case(s)");
        Assert.Equal(1, floats);
    }

    [Fact]
    public void AnObjectKeepsItsInsertionOrderAndItsEmptyForm()
    {
        // Sorted output would still parse equal for a reader that did not care about order, and the
        // differ does care: it re-serializes the parsed subtree with `sort_keys=False`.
        Assert.Equal("{\n  \"b\": 1,\n  \"a\": 2\n}", VerbatimJson.Object(0, ("b", "1"), ("a", "2")));
        Assert.Equal("{}", VerbatimJson.Object(0));
        Assert.Equal("[]", VerbatimJson.Array(0, []));
        Assert.Equal("[]", VerbatimJson.StringArray(0, []));
    }

    [Fact]
    public void TheWriterNeverEscapesWhatJsonAllowsThrough()
    {
        // `'`, `+`, `<`, `>`, `&` and `/` are legal inside a JSON string and are published as
        // themselves: the escape set stops at the characters JSON requires, and the differ reads every
        // one of them back.
        Assert.Equal("\"'+<>&/\"", VerbatimJson.String("'+<>&/"));
    }

    [Fact]
    public void NonAsciiIsWrittenAsAnEscape()
    {
        // The escaped form is this writer's rule: `verdict.json` carries `\u2013`, while `tables.md` is
        // markdown and carries the character itself.
        Assert.Equal("\"\\u2013\"", VerbatimJson.String("–"));
        Assert.Equal("\"\\u2014\"", VerbatimJson.String("—"));
        Assert.Equal("\"\\u4e2d\\u6587\"", VerbatimJson.String("中文"));
        Assert.Equal("\"\\ufffd\"", VerbatimJson.String("\ufffd"));
        Assert.Equal("\"\\ud83d\\ude42\"", VerbatimJson.String("🙂"));
        Assert.Equal("\"\\u007f\"", VerbatimJson.String("\u007f"));
        Assert.Equal("\"\\u0000\"", VerbatimJson.String("\0"));
    }

    [Fact]
    public void TheControlCharactersWithAShortFormUseIt()
    {
        Assert.Equal("\"\\b\\f\\n\\r\\t\"", VerbatimJson.String("\b\f\n\r\t"));
        Assert.Equal("\"\\\"\"", VerbatimJson.String("\""));
        Assert.Equal("\"\\\\\"", VerbatimJson.String("\\"));
    }

    [Fact]
    public void AValueIsWrittenAtTheLevelItIsPlacedAt()
    {
        // The nesting is the whole of the indentation contract: a member's value is rendered one
        // level deeper than the object it belongs to, and a container closes at its own level.
        Assert.Equal(
            "{\n  \"outer\": {\n    \"inner\": [\n      1,\n      2\n    ]\n  }\n}",
            VerbatimJson.Object(
                0,
                ("outer", VerbatimJson.Object(
                    1,
                    ("inner", VerbatimJson.Array(2, [VerbatimJson.Integer(1), VerbatimJson.Integer(2)]))))));
    }

    [Fact]
    public void TheScalarsAreWrittenInTheirJsonSpelling()
    {
        Assert.Equal("null", VerbatimJson.Null);
        Assert.Equal("true", VerbatimJson.Boolean(true));
        Assert.Equal("false", VerbatimJson.Boolean(false));
        Assert.Equal("-7", VerbatimJson.Integer(-7));
        Assert.Equal("10000", VerbatimJson.Integer(10000));
        Assert.Equal("0.5", VerbatimJson.Number(0.5));
        Assert.Equal("1", VerbatimJson.Number(1.0));
    }

    /// <summary>
    /// One parsed JSON value as the writer produces it: the document the vector table holds, rebuilt
    /// through <see cref="VerbatimJson"/> instead of the reference's <c>json.dumps</c>.
    /// </summary>
    private static string Render(JsonElement element, int level, string? name) => element.ValueKind switch
    {
        JsonValueKind.Object => VerbatimJson.Object(
            level,
            [.. element.EnumerateObject().Select(property => (property.Name, Render(property.Value, level + 1, null)))]),
        JsonValueKind.Array => VerbatimJson.Array(
            level,
            [.. element.EnumerateArray().Select(item => Render(item, level + 1, null))]),
        JsonValueKind.String => VerbatimJson.String(element.GetString()!),
        JsonValueKind.Number => IsFloatLiteral(element)
            ? VerbatimJson.Number(element.GetDouble())
            : VerbatimJson.Integer(element.GetInt64()),
        JsonValueKind.True => VerbatimJson.Boolean(true),
        JsonValueKind.False => VerbatimJson.Boolean(false),
        JsonValueKind.Null => VerbatimJson.Null,
        JsonValueKind.Undefined => throw new InvalidOperationException($"case '{name}' holds no value"),
        _ => throw new InvalidOperationException($"case '{name}' holds a value of an unknown kind"),
    };

    /// <summary>
    /// Whether any value under this one is a float literal, which is the distinction Python's `int` and
    /// `float` text makes and a parsed <see cref="JsonElement"/> no longer carries: `100.0` and `1e2`
    /// are floats there.
    /// </summary>
    private static bool HoldsFloat(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(property => HoldsFloat(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HoldsFloat),
        JsonValueKind.Number => IsFloatLiteral(element),
        _ => false,
    };

    /// <summary>
    /// Whether the literal was written as a float in the text it was parsed from.
    /// </summary>
    private static bool IsFloatLiteral(JsonElement element) =>
        element.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') >= 0;
}
