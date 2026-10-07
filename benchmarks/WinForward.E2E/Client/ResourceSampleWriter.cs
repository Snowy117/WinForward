using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Client;

/// <summary>
/// The shape of a <c>sample</c> record and of the <c>samplerError</c> record that reports a tick's
/// failure. Both are written into the arm's own sink, so where a failure is reported is the arm's
/// series and not the console: a sampler fault must not end the run it was observing.
/// </summary>
internal static class ResourceSampleWriter
{
    /// <summary>
    /// Writes a diagnostic to stderr. stderr may be a closed pipe, and a diagnostic must never end the
    /// sampling loop, so both ways that write can fail are swallowed here.
    /// </summary>
    internal static void ReportToStderr(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch (IOException)
        {
            /* stderr may be a closed pipe; a diagnostic must never end the sampling loop */
        }
        catch (ObjectDisposedException)
        {
            /* stderr may be a closed pipe; a diagnostic must never end the sampling loop */
        }
    }

    /// <summary>
    /// The members every sample record opens with: which arm's file it is going into, what the tick was,
    /// which name it observed, and how many processes that name matched.
    /// </summary>
    internal static void WriteSampleHeader(Utf8JsonWriter writer, SamplerTarget target, string process, bool self, long ticks, int matched)
    {
        writer.WriteString(ArmKeys.Common.Record.Type, "sample");
        writer.WriteNumber(ArmKeys.Sample.Ticks, ticks);
        writer.WriteString(ArmKeys.Common.Record.Arm, target.Arm);
        writer.WriteString(ArmKeys.Sample.Process, process);
        writer.WriteBoolean(ArmKeys.Sample.Self, self);
        writer.WriteNumber(ArmKeys.Sample.Matched, matched);
    }

    internal static void WriteCounters(Utf8JsonWriter writer, in ProcessCounters counters)
    {
        writer.WriteNumber(ArmKeys.Sample.Counters.CpuSeconds, NumberFormat.Round(counters.CpuSeconds, 4));
        writer.WriteNumber(ArmKeys.Sample.Counters.PrivateBytes, counters.PrivateBytes);
        writer.WriteNumber(ArmKeys.Sample.Counters.WorkingSetBytes, counters.WorkingSet);
        writer.WriteNumber(ArmKeys.Sample.Counters.PeakWorkingSetBytes, counters.PeakWorkingSet);
        writer.WriteNumber(ArmKeys.Sample.Counters.Threads, counters.Threads);
    }

    /// <summary>
    /// Writes what was summed plus an explicit marker for the processes that could not be read. The
    /// per-process counters are null rather than zero for those, because a zero here is not a measurement.
    /// </summary>
    internal static void WriteProcesses(Utf8JsonWriter writer, IReadOnlyList<SampledProcess> processes)
    {
        var readErrors = 0;
        writer.WriteStartArray(ArmKeys.Sample.Processes);
        foreach (var process in processes)
        {
            writer.WriteStartObject();
            writer.WriteNumber(ArmKeys.Sample.ProcessEntry.Pid, process.Id);
            if (process.StartedUtc is { } startedUtc)
            {
                writer.WriteString(ArmKeys.Sample.ProcessEntry.StartUtc, startedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteNull(ArmKeys.Sample.ProcessEntry.StartUtc);
            }

            writer.WriteBoolean(ArmKeys.Sample.ProcessEntry.CountersRead, process.CountersRead);
            if (process.CountersRead)
            {
                writer.WriteNumber(ArmKeys.Sample.ProcessEntry.CpuSeconds, NumberFormat.Round(process.CpuSeconds, 4));
                writer.WriteNumber(ArmKeys.Sample.ProcessEntry.PrivateBytes, process.PrivateBytes);
            }
            else
            {
                writer.WriteNull(ArmKeys.Sample.ProcessEntry.CpuSeconds);
                writer.WriteNull(ArmKeys.Sample.ProcessEntry.PrivateBytes);
                readErrors++;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber(ArmKeys.Sample.ReadErrors, readErrors);
        if (readErrors > 0)
        {
            writer.WriteBoolean(ArmKeys.Sample.ReadError, value: true);
        }
    }

    /// <summary>
    /// Records a sampling failure where the arm's data lives, so a persistent failure is visible in the
    /// series instead of only in the exception the sampler would otherwise throw at dispose.
    /// </summary>
    internal static async ValueTask ReportSamplingFailureAsync(SamplerTarget target, string process, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            await target.Sink.WriteAsync(
                writer =>
                {
                    writer.WriteString(ArmKeys.Common.Record.Type, "samplerError");
                    writer.WriteNumber(ArmKeys.Sample.Ticks, Stopwatch.GetTimestamp());
                    writer.WriteString(ArmKeys.Common.Record.Arm, target.Arm);
                    writer.WriteString(ArmKeys.Sample.Process, process);
                    writer.WriteString(ArmKeys.Sample.SamplerError.Error, exception.GetType().Name);
                    writer.WriteString(ArmKeys.Sample.SamplerError.Message, exception.Message);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* the arm ended while the failure record was being written */
        }
        catch (Exception sinkFailure) when (sinkFailure is not OperationCanceledException)
        {
            /* the sink itself failed: fall back to stderr, because a lost failure must not end sampling */
            ReportToStderr($"e2e client: resource sampler could not write a failure record for arm {target.Arm}: {sinkFailure.GetType().Name}: {sinkFailure.Message}");
        }
    }
}
