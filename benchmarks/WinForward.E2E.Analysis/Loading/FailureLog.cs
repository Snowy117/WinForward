using System.Text.Json;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Loading;

/// <summary>
/// The campaign's failure record: <c>failures.jsonl</c> when the orchestrator wrote one, otherwise the
/// derived <c>failures.json</c> array, read from beside the pass directories or beside <c>--raw</c>.
/// </summary>
/// <remarks>
/// <para><b>A missing record is not a failure.</b> A tree without one reads exactly as it always
/// did; an unreadable one is reported through <see cref="FailureLogResult.Error"/> so the caller can
/// disclose it rather than treat the campaign as clean.</para>
/// <para><b>JSONL wins over the array.</b> The stream survives a campaign that was killed mid-write;
/// the array is rewritten after every entry and is the fallback for a tree that never got a
/// stream.</para>
/// </remarks>
internal static class FailureLog
{
    private const string StreamFile = "failures.jsonl";

    private const string DocumentFile = "failures.json";

    /// <summary>Reads the record, or reports why it could not be read.</summary>
    /// <param name="raw">The <c>--raw</c> directory the campaign was loaded from.</param>
    internal static FailureLogResult Read(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var stream = new[]
        {
            PosixPathText.Join(raw, StreamFile),
            PosixPathText.Join(PosixPathText.Parent(raw), StreamFile),
        }.FirstOrDefault(File.Exists);
        if (stream is not null)
        {
            JsonLines lines;
            try
            {
                lines = JsonReader.ReadLines(stream);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new FailureLogResult(Failures: [], BadLines: 0, Error: $"{StreamFile} unreadable ({exception.GetType().Name})");
            }

            return ReadStream(lines);
        }

        var document = new[]
        {
            PosixPathText.Join(raw, DocumentFile),
            PosixPathText.Join(PosixPathText.Parent(raw), DocumentFile),
        }.FirstOrDefault(File.Exists);
        if (document is null)
        {
            return new FailureLogResult(Failures: [], BadLines: 0, Error: null);
        }

        var root = JsonReader.ReadFile(document, out var error);
        if (error is not null)
        {
            return new FailureLogResult(Failures: [], BadLines: 0, Error: $"{DocumentFile} unreadable ({error.GetType().Name})");
        }

        if (root is not { ValueKind: JsonValueKind.Array } array)
        {
            return new FailureLogResult(Failures: [], BadLines: 0, Error: $"{DocumentFile} is not a JSON array of failure records");
        }

        var records = new List<CampaignFailure>();
        var bad = 0;
        foreach (var element in array.EnumerateArray())
        {
            if (CampaignFailure.From(element) is { } failure)
            {
                records.Add(failure);
            }
            else
            {
                bad++;
            }
        }

        return new FailureLogResult(Failures: records, BadLines: bad, Error: null);
    }

    /// <summary>
    /// One JSONL line set as failure records. A line that parsed as a JSON object but carries no stage is
    /// a malformed record, not a record: it is counted with the unparsable lines so both the stream and
    /// the array disclose the same thing.
    /// </summary>
    private static FailureLogResult ReadStream(JsonLines lines)
    {
        var failures = new List<CampaignFailure>();
        var bad = lines.BadLines;
        foreach (var record in lines.Records)
        {
            if (CampaignFailure.From(record) is { } failure)
            {
                failures.Add(failure);
            }
            else
            {
                bad++;
            }
        }

        return new FailureLogResult(Failures: failures, BadLines: bad, Error: null);
    }

}

/// <summary>What the failure record said, how many lines were not records, and why it is missing.</summary>
/// <param name="Failures">One entry per record, in file order.</param>
/// <param name="BadLines">Lines that were not a JSON object carrying a stage.</param>
/// <param name="Error">Why the record could not be read, or null.</param>
internal sealed record FailureLogResult(IReadOnlyList<CampaignFailure> Failures, int BadLines, string? Error);
