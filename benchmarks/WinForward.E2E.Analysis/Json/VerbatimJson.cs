using System.Globalization;
using System.Text;

namespace WinForward.E2E.Analysis.Json;

/// <summary>
/// The JSON text the analysis publishes: one document per file, two-space indent, members in insertion
/// order, a space after each colon, and the file encoded as UTF-8 without a byte-order mark and ending
/// in exactly one newline.
/// </summary>
/// <remarks>
/// <para>The escaping is this writer's own rule rather than the reproduction of another writer's: the
/// two escapes JSON requires, the five short control escapes, <c>\uXXXX</c> for every other code unit
/// outside printable ASCII — an astral code point therefore comes out as the surrogate pair JSON spells
/// it with — and <c>'</c>, <c>+</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and <c>/</c> left as they
/// are. In the oracle's semantic mode both sides are decoded before they are compared, so these bytes
/// are observed only by <c>--mode byte</c> and by a reader that takes the file as text; the rule is kept
/// stable because it is what the frozen reference wrote for the same documents.</para>
/// <para><see cref="System.Text.Json"/>'s encoders each write half of that rule — the default one
/// escapes <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>'</c> and <c>+</c>, and the relaxed one leaves
/// every non-ASCII code point alone — so the escaping is explicit rather than delegated (D20.5).</para>
/// <para>Every value is rendered as text at the nesting level it sits at, and a container renders its
/// own members one level deeper, so the indentation of a whole document falls out of the level each
/// call is given: <c>verdict.json</c>'s top-level keys are values at level 1, and a nested object's
/// members are at level 2.</para>
/// <para>The newline at the end of the file is part of the compared bytes: <c>oracle-diff.py</c> reads
/// the produced file as text and the golden was written by <c>Path.write_text</c> of a string that ends
/// in exactly one <c>\n</c>.</para>
/// <para>Public because the test project drives it against the frozen reference's own text; D20.6 keeps
/// the analyzer free of an <c>InternalsVisibleTo</c>.</para>
/// </remarks>
public static class VerbatimJson
{
    private const int IndentWidth = 2;

    /// <summary>
    /// One string as this writer escapes it: the two mandatory escapes, the five short control escapes,
    /// and <c>\uXXXX</c> for every other code unit outside printable ASCII, with an astral code point
    /// written as its surrogate pair. The characters JSON allows through — including <c>'</c>,
    /// <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and <c>/</c> — are passed through as they are.
    /// </summary>
    /// <param name="value">The string to write.</param>
    /// <returns>The string as a JSON literal, quotes included.</returns>
    public static string String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var text = new StringBuilder(value.Length + 2);
        text.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    text.Append('\\').Append('"');
                    break;
                case '\\':
                    text.Append('\\').Append('\\');
                    break;
                case '\b':
                    text.Append("\\b");
                    break;
                case '\f':
                    text.Append("\\f");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                default:
                    AppendCharacter(text, character);
                    break;
            }
        }

        text.Append('"');
        return text.ToString();
    }

    /// <summary>One integer in its JSON spelling: sign and digits, nothing else.</summary>
    /// <param name="value">The value to write.</param>
    /// <returns>The number's text.</returns>
    public static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>One float in the shortest text that reads back as the same value: see
    /// <see cref="VerbatimNumber.Json"/>.</summary>
    /// <param name="value">The value to write.</param>
    /// <returns>The number's text.</returns>
    public static string Number(double value) => VerbatimNumber.Json(value);

    /// <summary>One boolean as the bare lower-case keyword JSON spells it with.</summary>
    /// <param name="value">The value to write.</param>
    /// <returns>The keyword's text.</returns>
    public static string Boolean(bool value) => value ? "true" : "false";

    /// <summary>
    /// The null literal, which is what the analysis publishes where the harness had no value at all. It
    /// is the serialization of nothing rather than a missing key, and the two are told apart everywhere
    /// else in the analysis.
    /// </summary>
    public static string Null => "null";

    /// <summary>
    /// A list of strings at <paramref name="level"/>: an empty list stays on one line, a non-empty one
    /// puts every element on its own line one level in.
    /// </summary>
    /// <param name="level">How deep the list sits; its elements sit one level deeper.</param>
    /// <param name="values">The strings to write, in insertion order.</param>
    /// <returns>The list's text, without the newline a file ends in.</returns>
    public static string StringArray(int level, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var rendered = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            rendered[index] = String(values[index]);
        }

        return Array(level, rendered);
    }

    /// <summary>
    /// A list of already-rendered values, one level down: each element is written as it is, so its own
    /// indentation is the level it was rendered at.
    /// </summary>
    /// <param name="level">How deep the list sits; its elements sit one level deeper.</param>
    /// <param name="rendered">The elements' JSON text, in insertion order.</param>
    /// <returns>The list's text, without the newline a file ends in.</returns>
    public static string Array(int level, IReadOnlyList<string> rendered)
    {
        ArgumentNullException.ThrowIfNull(rendered);

        if (rendered.Count == 0)
        {
            return "[]";
        }

        var text = new StringBuilder();
        text.Append("[\n");
        for (var index = 0; index < rendered.Count; index++)
        {
            text.Append(Indent(level + 1)).Append(rendered[index]);
            text.Append(index + 1 < rendered.Count ? ",\n" : "\n");
        }

        return text.Append(Indent(level)).Append(']').ToString();
    }

    /// <summary>
    /// An object at <paramref name="level"/>: members in the order they are given, a space after each
    /// colon, and an empty object left on one line.
    /// </summary>
    /// <param name="level">How deep the object sits; its members sit one level deeper.</param>
    /// <param name="members">The members' names and already-rendered values, in insertion order.</param>
    /// <returns>The object's text, without the newline a file ends in.</returns>
    public static string Object(int level, params (string Key, string Value)[] members)
    {
        ArgumentNullException.ThrowIfNull(members);

        if (members.Length == 0)
        {
            return "{}";
        }

        var text = new StringBuilder();
        text.Append("{\n");
        for (var index = 0; index < members.Length; index++)
        {
            text.Append(Indent(level + 1))
                .Append(String(members[index].Key))
                .Append(": ")
                .Append(members[index].Value);
            text.Append(index + 1 < members.Length ? ",\n" : "\n");
        }

        return text.Append(Indent(level)).Append('}').ToString();
    }

    /// <summary>The whitespace one level of nesting is written with.</summary>
    private static string Indent(int level) => new(' ', IndentWidth * level);

    /// <summary>
    /// One code unit, escaped when it is outside printable ASCII. An astral code point is two UTF-16
    /// code units and so becomes the surrogate pair JSON spells it with, with no special case needed.
    /// </summary>
    private static void AppendCharacter(StringBuilder text, char character)
    {
        if (character is >= ' ' and <= '~')
        {
            text.Append(character);
            return;
        }

        text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
    }
}
