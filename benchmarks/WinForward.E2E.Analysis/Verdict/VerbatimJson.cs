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
}
