using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Cli;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Client;

/// <summary>
/// The three per-arm records an arm's file carries besides the sampler's ticks: the <c>error</c>
/// record a failed arm owes its reader, the <c>result</c> record the arm measured, and the
/// <c>armSummary</c> record run.json points at. Every method here writes into the arm's own sink and
/// is called inside that arm's failure boundary.
/// </summary>
internal static class ArmRecordWriter
{
    /// <summary>The outcome an arm that failed before building one still gets a record for.</summary>
    internal static ArmOutcome EmptyOutcome() => new()
    {
        Parameters = new ArmParameters(),
        Metrics = new EmptyMetrics(),
    };

    internal static IOException LostRecords(JsonlSink sink) =>
        new($"the arm's record file could not be written: {sink.WriteErrors.ToString(CultureInfo.InvariantCulture)} record(s) lost");

    /// <summary>
    /// Writes the `error` record for an arm whose records could not be written at all. The sink is
    /// the thing that failed, so this last attempt cannot end the run: the failure is reported to
    /// stderr instead, and run.json still carries the arm's `failed` flag.
    /// </summary>
    internal static async ValueTask TryWriteFailureAsync(
        JsonlSink sink,
        ClientOptions options,
        ArmSpec arm,
        Exception failure,
        bool cancelled,
        long startedTicks,
        long endedTicks)
    {
        try
        {
            await WriteFailureAsync(sink, options, arm, failure, cancelled, startedTicks, endedTicks).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(
                $"e2e client: arm {arm.Name} failed and its error record could not be written: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
        }
    }

    internal static async ValueTask WriteFailureAsync(
        JsonlSink sink,
        ClientOptions options,
        ArmSpec arm,
        Exception failure,
        bool cancelled,
        long startedTicks,
        long endedTicks)
    {
        // A token-driven cancellation surfaces as whichever subtype the await that observed it
        // raises (TaskCanceledException and friends), so `error` is reported as the base type; a
        // consumer reads the same name for every way a run can be stopped.
        var error = cancelled ? nameof(OperationCanceledException) : failure.GetType().Name;
        var message = cancelled ? "cancelled" : failure.Message;
        var detail = failure.GetBaseException().GetType().Name;
        await sink.WriteAsync(
            writer =>
            {
                writer.WriteString(ArmKeys.Common.Record.Type, "error");
                writer.WriteString(ArmKeys.Common.Record.Arm, arm.Name);
                writer.WriteString(ArmKeys.Common.Record.Kind, arm.Kind);
                writer.WriteString(ArmKeys.Common.Record.Label, options.Label);
                writer.WriteString(ArmKeys.Common.ErrorRecord.Error, error);
                writer.WriteString(ArmKeys.Common.ErrorRecord.Message, message);
                writer.WriteString(ArmKeys.Common.ErrorRecord.Detail, detail);
                writer.WriteNumber(ArmKeys.Common.Record.StartedTicks, startedTicks);
                writer.WriteNumber(ArmKeys.Common.Record.EndedTicks, endedTicks);
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the arm's <c>result</c> record: the one place a record's skeleton and its
    /// <c>metrics</c> object meet. Every arm reaches this writer with an <see cref="IJsonWritable"/>
    /// for its metrics, so the shape of the published file is decided by the metrics type rather than
    /// by a dictionary the writer has to interpret.
    /// </summary>
    internal static async ValueTask WriteResultAsync(
        JsonlSink sink,
        ClientOptions options,
        ArmSpec arm,
        ArmOutcome outcome,
        LatencySet latency,
        long startedTicks,
        long endedTicks)
    {
        await sink.WriteAsync(
            writer =>
            {
                writer.WriteString(ArmKeys.Common.Record.Type, "result");
                writer.WriteString(ArmKeys.Common.Record.Arm, arm.Name);
                writer.WriteString(ArmKeys.Common.Record.Kind, arm.Kind);
                writer.WriteString(ArmKeys.Common.Record.Label, options.Label);
                writer.WritePropertyName(ArmKeys.Common.Record.Parameters);
                outcome.Parameters.WriteTo(writer);
                writer.WritePropertyName(ArmKeys.Common.Record.Metrics);
                outcome.Metrics.WriteTo(writer);
                latency.WriteTo(writer);
                writer.WritePropertyName(ArmKeys.Common.Record.Gates);
                WriteGates(writer, outcome.Gates);
                writer.WritePropertyName(ArmKeys.Common.Record.Notes);
                writer.WriteStartArray();
                foreach (var note in outcome.Notes)
                {
                    writer.WriteStringValue(note);
                }

                writer.WriteEndArray();
                writer.WriteNumber(ArmKeys.Common.Record.StartedTicks, startedTicks);
                writer.WriteNumber(ArmKeys.Common.Record.EndedTicks, endedTicks);
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    internal static async ValueTask WriteArmSummaryAsync(
        JsonlSink sink,
        ClientOptions options,
        ArmSpec arm,
        ArmOutcome outcome,
        long startedTicks,
        long endedTicks,
        string fileName)
    {
        await sink.WriteAsync(
            writer =>
            {
                writer.WriteString(ArmKeys.Common.Record.Type, "armSummary");
                writer.WriteString(ArmKeys.Common.Record.Arm, arm.Name);
                writer.WriteString(ArmKeys.Common.Record.Kind, arm.Kind);
                writer.WriteString(ArmKeys.Common.Record.Label, options.Label);
                writer.WritePropertyName(ArmKeys.Common.Record.Parameters);
                outcome.Parameters.WriteTo(writer);
                writer.WritePropertyName(ArmKeys.Common.Record.Gates);
                WriteGates(writer, outcome.Gates);
                writer.WriteString(ArmKeys.Common.ArmSummary.ResultFile, fileName);
                writer.WriteNumber(ArmKeys.Common.Record.StartedTicks, startedTicks);
                writer.WriteNumber(ArmKeys.Common.Record.EndedTicks, endedTicks);
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the arm's <c>gates</c> object. Which gates an arm publishes is the arm's business and
    /// their order is its construction order, but every key is a
    /// <see cref="ArmKeys.Common.Gates"/> constant and every value is a JSON number.
    /// </summary>
    private static void WriteGates(Utf8JsonWriter writer, Dictionary<string, double> gates)
    {
        writer.WriteStartObject();
        foreach (var gate in gates)
        {
            writer.WriteNumber(gate.Key, gate.Value);
        }

        writer.WriteEndObject();
    }
}
