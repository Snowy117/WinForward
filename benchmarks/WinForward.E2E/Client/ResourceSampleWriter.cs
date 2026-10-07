using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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

    internal static void WriteCounters(Utf8JsonWriter writer, in ProcessCounters counters)
    {
        writer.WriteNumber("cpuSeconds", NumberFormat.Round(counters.CpuSeconds, 4));
        writer.WriteNumber("privateBytes", counters.PrivateBytes);
        writer.WriteNumber("workingSetBytes", counters.WorkingSet);
        writer.WriteNumber("peakWorkingSetBytes", counters.PeakWorkingSet);
        writer.WriteNumber("threads", counters.Threads);
    }

    /// <summary>
    /// Writes what was summed plus an explicit marker for the processes that could not be read. The
    /// per-process counters are null rather than zero for those, because a zero here is not a measurement.
    /// </summary>
    internal static void WriteProcesses(Utf8JsonWriter writer, IReadOnlyList<SampledProcess> processes)
    {
        var readErrors = 0;
        writer.WriteStartArray("processes");
        foreach (var process in processes)
        {
            writer.WriteStartObject();
            writer.WriteNumber("pid", process.Id);
            if (process.StartedUtc is { } startedUtc)
            {
                writer.WriteString("startUtc", startedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteNull("startUtc");
            }

            writer.WriteBoolean("countersRead", process.CountersRead);
            if (process.CountersRead)
            {
                writer.WriteNumber("cpuSeconds", NumberFormat.Round(process.CpuSeconds, 4));
                writer.WriteNumber("privateBytes", process.PrivateBytes);
            }
            else
            {
                writer.WriteNull("cpuSeconds");
                writer.WriteNull("privateBytes");
                readErrors++;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("readErrors", readErrors);
        if (readErrors > 0)
        {
            writer.WriteBoolean("readError", value: true);
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
                    writer.WriteString("type", "samplerError");
                    writer.WriteNumber("ticks", Stopwatch.GetTimestamp());
                    writer.WriteString("arm", target.Arm);
                    writer.WriteString("process", process);
                    writer.WriteString("error", exception.GetType().Name);
                    writer.WriteString("message", exception.Message);
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
