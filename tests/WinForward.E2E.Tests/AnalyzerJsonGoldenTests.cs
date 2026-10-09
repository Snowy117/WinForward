using WinForward.E2E.Analysis.Json;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// Holds the analysis's JSON text to the bytes it publishes: two spaces of indentation per level, a
/// member's `: ` separator, empty containers on one line, and the escape set that stops at what JSON
/// requires. Each test builds its document through the writer's own entry points, so the escaping, the
/// nesting and the insertion order are exercised on values rather than on fragments.
/// </summary>
public sealed class AnalyzerJsonGoldenTests
{
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
}
