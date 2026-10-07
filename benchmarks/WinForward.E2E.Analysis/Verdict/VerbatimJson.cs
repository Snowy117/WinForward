using System.Globalization;
using System.Text;

namespace WinForward.E2E.Analysis.Verdict;

/// <summary>
/// The reference's JSON text, reproduced: <c>json.dumps(..., indent=2, sort_keys=False)</c> followed by
/// one newline — insertion order, two-space indent, no space before a colon, <c>'</c>/<c>+</c>/<c>&gt;</c>/
/// <c>&lt;</c>/<c>&amp;</c> left unescaped, any existing <c>\uXXXX</c> escape preserved as written,
/// and the file encoded as UTF-8 without a byte-order mark.
/// </summary>
/// <remarks>
/// <para><b>Completed in batch 1b, with unit tests.</b> <see cref="System.Text.Json"/> escapes more
/// than the reference does by default (its relaxed encoder is the closest match, and it still differs
/// on some code points), writes <c>:</c> with a space after it when indenting, and offers no
/// "preserve the escape you read" mode — so the writer is explicit rather than delegated (D20.5).</para>
/// <para>The newline at the end of the file is part of the compared bytes: <c>oracle-diff.py</c> reads
/// the produced file as text and the golden was written by <c>Path.write_text</c> of a string that ends
/// in exactly one <c>\n</c>.</para>
/// </remarks>
internal static class VerbatimJson
{
    /// <summary>What this file still owes, for the run summary a caller reads.</summary>
    internal const string Pending = "1b json.dumps-compatible writer (indent 2, insertion order, UTF-8 no BOM)";

    /// <summary>
    /// One string as <c>json.dumps(value)</c> writes it: the two mandatory escapes, the five short
    /// control escapes, and <c>\uXXXX</c> for everything else outside the printable ASCII range, with
    /// an astral code point written as its surrogate pair. The characters JSON allows through —
    /// including <c>'</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and <c>/</c> — are passed through.
    /// </summary>
    internal static string String(string value)
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

    /// <summary>
    /// A list of strings as <c>json.dumps(values, indent=2)</c> writes it one level down: an empty list
    /// is <c>[]</c> on one line, a non-empty one puts every element on its own line, indented two
    /// spaces past the key that names it.
    /// </summary>
    internal static string StringArray(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return "[]";
        }

        var text = new StringBuilder();
        text.Append("[\n");
        for (var index = 0; index < values.Count; index++)
        {
            text.Append("    ").Append(String(values[index]));
            text.Append(index + 1 < values.Count ? ",\n" : "\n");
        }

        text.Append("  ]");
        return text.ToString();
    }

    /// <summary>
    /// One code unit, escaped when it is outside printable ASCII. An astral code point is two UTF-16
    /// code units and so becomes the surrogate pair Python writes, with no special case needed.
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
