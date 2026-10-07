using System.Text.Json;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Analysis.Loading;

/// <summary>
/// One JSONL file's records, reduced to what a caller can act on without holding every document:
/// how many lines there were, how many were not valid JSON, and how many records of each
/// <c>type</c> the file published.
/// </summary>
/// <param name="Lines">Non-blank lines the file contains.</param>
/// <param name="Malformed">Lines that were not a JSON object; the reference counts these rather than skipping them silently.</param>
/// <param name="ByType">Record count per <c>type</c>, in first-seen order; a record with no <c>type</c> counts under the empty name.</param>
internal sealed record RecordScan(int Lines, int Malformed, IReadOnlyDictionary<string, int> ByType);

/// <summary>
/// The read side of the analysis: JSONL and JSON are read with <see cref="JsonDocument"/> and the
/// contract's own key constants, never with a serializer and never with a generated one (D20.6).
/// </summary>
/// <remarks>
/// <para>The key of a record's family is <see cref="ArmKeys.Common.Record.Type"/>. Spelling it here
/// as a literal would be exactly the drift the shared contract exists to prevent, and reaching for
/// <c>JsonSerializer.Deserialize&lt;T&gt;</c> would put the whole analysis behind reflection: the trim
/// and AOT analyzers reject that call as an error under this repository's warning policy (IL2026,
/// IL3050), which is why the read path is explicit from the start.</para>
/// <para>A malformed line is counted, not thrown on: the comparison between the two implementations
/// is over the analysis's *output*, and an arm file with one truncated line is a shape a campaign can
/// produce.</para>
/// </remarks>
internal static class RecordScanner
{
    /// <summary>Scans one JSONL file. A missing file scans as zero lines rather than throwing.</summary>
    internal static RecordScan ScanJsonl(FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var lines = 0;
        var malformed = 0;
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!file.Exists)
        {
            return new RecordScan(0, 0, byType);
        }

        foreach (var line in File.ReadLines(file.FullName))
        {
            if (line.AsSpan().Trim().IsEmpty)
            {
                continue;
            }

            lines++;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                malformed++;
                continue;
            }

            using (document)
            {
                var type = ReadType(document.RootElement);
                byType[type] = byType.TryGetValue(type, out var seen) ? seen + 1 : 1;
            }
        }

        return new RecordScan(lines, malformed, byType);
    }

    /// <summary>Reads a record's <c>type</c> through the contract's constant, or the empty name.</summary>
    private static string ReadType(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(ArmKeys.Common.Record.Type, out var type)
            && type.ValueKind == JsonValueKind.String)
        {
            return type.GetString() ?? string.Empty;
        }

        return string.Empty;
    }
}
