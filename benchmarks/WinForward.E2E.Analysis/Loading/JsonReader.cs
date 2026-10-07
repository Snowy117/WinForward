using System.Text;
using System.Text.Json;

namespace WinForward.E2E.Analysis.Loading;

/// <summary>
/// One JSONL file's records plus the lines that were not records at all.
/// </summary>
/// <param name="Records">One element per non-blank line, in file order.</param>
/// <param name="BadLines">Lines that were not a single JSON object; the reference counts these rather than skipping them silently.</param>
internal sealed record JsonLines(IReadOnlyList<JsonElement> Records, int BadLines);

/// <summary>
/// The read side of the analysis: JSONL and JSON are read with <see cref="JsonDocument"/> and the
/// contract's own key constants, never with a serializer and never with a generated one.
/// </summary>
/// <remarks>
/// <para><b>Why not <c>JsonSerializer.Deserialize&lt;T&gt;</c>.</b> The trim and AOT analyzers reject
/// that call as an error under this repository's warning policy (IL2026, IL3050), which is measured
/// rather than assumed; reading explicitly is also what keeps a renamed field a compile error instead
/// of an empty cell.</para>
/// <para><b>The input is somebody else's file.</b> A line that is not a JSON object is counted, an
/// unreadable file is reported with the exception's own name, and a byte-order mark is tolerated, all
/// because these files are written by a harness running under load and a campaign can produce a
/// truncated line.</para>
/// <para><b>One value per file or line, and nothing after it.</b> A record followed by more text is
/// rejected, as <c>json.loads</c>'s "Extra data" rejects it, rather than read as its first value. The
/// one input the reference accepts and this reader does not is the named floating-point literal
/// (<c>NaN</c>/<c>Infinity</c>): <see cref="System.Text.Json"/> has no reader option for it. It cannot
/// occur in a tree this harness wrote, because its own writer refuses to serialize those values.</para>
/// <para>The BOM, the replacement of invalid UTF-8 and the treating of every kind of line break as a
/// break all follow <c>utf-8-sig</c> and universal newlines.</para>
/// </remarks>
internal static class JsonReader
{
    private static readonly JsonDocumentOptions s_options = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Reads one JSONL file; a missing file reads as no lines rather than throwing.</summary>
    internal static JsonLines ReadLines(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var records = new List<JsonElement>();
        var badLines = 0;
        if (!File.Exists(path))
        {
            return new JsonLines(records, badLines);
        }

        using var reader = new StreamReader(path, s_utf8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var record = ParseObject(trimmed);
            if (record is null)
            {
                badLines++;
                continue;
            }

            records.Add(record.Value);
        }

        return new JsonLines(records, badLines);
    }

    /// <summary>
    /// Reads one JSON file whole. Returns null when it is missing, unreadable or not JSON, with the
    /// reason on <paramref name="error"/> so the caller can name it the way the reference does.
    /// </summary>
    internal static JsonElement? ReadFile(string path, out Exception? error)
    {
        ArgumentNullException.ThrowIfNull(path);

        error = null;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception;
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(SkipByteOrderMark(bytes), s_options);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            error = exception;
            return null;
        }
    }

    /// <summary>One line as a JSON object, or null when it is not JSON or not an object.</summary>
    private static JsonElement? ParseObject(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line, s_options);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Drops the UTF-8 byte-order mark <c>utf-8-sig</c> would swallow.</summary>
    private static ReadOnlyMemory<byte> SkipByteOrderMark(byte[] bytes)
    {
        var bom = s_utf8.GetPreamble();
        if (bytes.Length >= bom.Length && bytes.AsSpan(0, bom.Length).SequenceEqual(bom))
        {
            return bytes.AsMemory(bom.Length);
        }

        return bytes;
    }
}
