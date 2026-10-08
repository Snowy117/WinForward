using System.Text.Json;
using WinForward.E2E.Analysis.Json;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// Holds the analysis's JSON text to <c>json.dumps(value, indent=2, sort_keys=False)</c>. Each golden
/// case is a document and the text CPython wrote for it; the test rebuilds the text through the
/// writer's own entry points, so the escaping, the nesting and the insertion order are all exercised
/// on values rather than on fragments.
/// </summary>
public sealed class AnalyzerJsonGoldenTests
{
    [Fact]
    public void EveryGoldenDocumentIsReproduced()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(RepoPaths.AnalyzerGolden("py-json-vectors.json")));

        var cases = 0;
        foreach (var entry in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            Assert.Equal(entry.GetProperty("text").GetString(), Render(entry.GetProperty("value"), 0, name));
            cases++;
        }

        Assert.True(cases >= 10, $"the vector table holds {cases} case(s)");
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
    public void TheWriterNeverEscapesWhatTheReferenceLeavesAlone()
    {
        // `'`, `+`, `<`, `>`, `&` and `/` look like they should be escaped and are not: the golden
        // `verdict.json` carries all six as themselves, and the differ compares text.
        Assert.Equal("\"'+<>&/\"", VerbatimJson.String("'+<>&/"));
    }

    [Fact]
    public void NonAsciiIsWrittenBackAsTheEscapeTheReferenceUses()
    {
        // `ensure_ascii=True` is per file, not per repository: `tables.md` carries the character
        // itself where `verdict.json` carries `\u2013`.
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
    public void TheScalarsAreWrittenTheWayTheReferenceWritesThem()
    {
        Assert.Equal("null", VerbatimJson.Null);
        Assert.Equal("true", VerbatimJson.Boolean(true));
        Assert.Equal("false", VerbatimJson.Boolean(false));
        Assert.Equal("-7", VerbatimJson.Integer(-7));
        Assert.Equal("10000", VerbatimJson.Integer(10000));
        Assert.Equal("0.5", VerbatimJson.Number(0.5));
        Assert.Equal("1.0", VerbatimJson.Number(1.0));
    }

    /// <summary>
    /// One parsed JSON value as the writer produces it: the same tree the reference handed to
    /// <c>json.dumps</c>, rebuilt through <see cref="VerbatimJson"/> instead.
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
    /// Whether the literal was a float, which is the distinction Python's `int` and `float` repr make
    /// and a parsed <see cref="JsonElement"/> no longer carries: `100.0` and `1e2` are floats there.
    /// </summary>
    private static bool IsFloatLiteral(JsonElement element) =>
        element.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') >= 0;
}
