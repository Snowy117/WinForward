using System.Text.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Analysis.Loading;

/// <summary>
/// Reads one arm's file into the records the analysis works from: the arm's <c>result</c>, its
/// <c>armSummary</c> header, its sample series, and its failure records.
/// </summary>
/// <remarks>
/// <para><b>A file's records are classified by their own <c>type</c>.</b> The arm writes different
/// kinds into one file as the run progresses, so the file is a sequence rather than a document; a
/// record of an unknown kind is not an error, it is just not one of the five this model keeps.</para>
/// <para><b>A repeated <c>result</c> replaces the earlier one.</b> That is the reference's behaviour and
/// it is the safe one: a record written twice describes the same arm, and the later one is the one the
/// harness finished with.</para>
/// </remarks>
internal static class ArmLoader
{
    /// <summary>Loads one arm's file; a missing file is an empty arm rather than an error.</summary>
    internal static ArmRecords Load(string name, string path)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);

        var lines = JsonReader.ReadLines(path);
        JsonElement? result = null;
        JsonElement? summary = null;
        var samples = new List<JsonElement>();
        var samplerErrors = new List<JsonElement>();
        var errors = new List<JsonElement>();
        foreach (var record in lines.Records)
        {
            switch (JsonValue.String(record, ArmKeys.Common.Record.Type))
            {
                case ArmRecords.ResultKind:
                    result = record;
                    break;
                case ArmRecords.ArmSummaryKind:
                    summary = record;
                    break;
                case ArmRecords.SampleKind:
                    samples.Add(record);
                    break;
                case ArmRecords.SamplerErrorKind:
                    samplerErrors.Add(record);
                    break;
                case ArmRecords.ErrorKind:
                    errors.Add(record);
                    break;
            }
        }

        return new ArmRecords(
            name,
            Path.GetFileName(path),
            result,
            summary,
            samples,
            samplerErrors,
            errors,
            lines.BadLines);
    }
}
