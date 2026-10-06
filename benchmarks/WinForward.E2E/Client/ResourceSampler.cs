using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinForward.E2E.Client;

internal sealed record SamplerTarget(JsonlFile Sink, string Arm);

[StructLayout(LayoutKind.Auto)]
internal readonly struct ProcessCounters
{
    internal ProcessCounters(
        double cpuSeconds,
        long privateBytes,
        long workingSet,
        long peakWorkingSet,
        long threads,
        long handles)
    {
        CpuSeconds = cpuSeconds;
        PrivateBytes = privateBytes;
        WorkingSet = workingSet;
        PeakWorkingSet = peakWorkingSet;
        Threads = threads;
        Handles = handles;
    }

    internal double CpuSeconds { get; }

    internal long PrivateBytes { get; }

    internal long WorkingSet { get; }

    internal long PeakWorkingSet { get; }

    internal long Threads { get; }

    internal long Handles { get; }
}

/// <summary>
/// The identity of a summed process is the pair (id, start time): Windows recycles process ids, so the id
/// alone cannot tell a restart from the process that was already there.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct SampledProcess
{
    internal SampledProcess(int id, DateTime? startedUtc, bool countersRead, double cpuSeconds, long privateBytes)
    {
        Id = id;
        StartedUtc = startedUtc;
        CountersRead = countersRead;
        CpuSeconds = cpuSeconds;
        PrivateBytes = privateBytes;
    }

    internal int Id { get; }

    internal DateTime? StartedUtc { get; }

    internal bool CountersRead { get; }

    internal double CpuSeconds { get; }

    internal long PrivateBytes { get; }
}

internal sealed class ProcessTotals
{
    internal List<SampledProcess> Processes { get; } = [];

    internal double CpuSeconds { get; set; }

    internal long PrivateBytes { get; set; }

    internal long WorkingSet { get; set; }

    internal long PeakWorkingSet { get; set; }

    internal long Threads { get; set; }

    internal long Handles { get; set; }
}

internal sealed class ResourceSampler : IAsyncDisposable
{
    private static readonly TimeSpan s_interval = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<string> _processNames;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Task _loop;
    private volatile SamplerTarget? _target;
    private bool _disposed;

    internal ResourceSampler(IReadOnlyList<string> processNames)
    {
        _processNames = processNames;
        _loop = LoopAsync(_shutdown.Token);
    }

    internal void SetTarget(JsonlFile sink, string arm) => _target = new SamplerTarget(sink, arm);

    internal async Task ClearTargetAsync()
    {
        _target = null;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Release();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            /* a sampler fault must not fail the run it was observing; every tick already reported itself */
            ReportToStderr($"e2e client: resource sampler stopped: {exception.GetType().Name}: {exception.Message}");
        }

        _shutdown.Dispose();
        _gate.Dispose();
    }

    private static bool TryRefresh(Process process)
    {
        try
        {
            process.Refresh();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryReadLong(Process process, Func<Process, long> read, out long value)
    {
        try
        {
            value = read(process);
            return true;
        }
        catch (InvalidOperationException)
        {
            value = 0;
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            value = 0;
            return false;
        }
        catch (Win32Exception)
        {
            value = 0;
            return false;
        }
        catch (NotSupportedException)
        {
            value = 0;
            return false;
        }
        catch (IOException)
        {
            value = 0;
            return false;
        }
    }

    private static bool TryReadStartTime(Process process, out DateTime startedUtc)
    {
        try
        {
            startedUtc = process.StartTime;
            return true;
        }
        catch (InvalidOperationException)
        {
            startedUtc = default;
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            startedUtc = default;
            return false;
        }
        catch (Win32Exception)
        {
            startedUtc = default;
            return false;
        }
        catch (NotSupportedException)
        {
            startedUtc = default;
            return false;
        }
        catch (IOException)
        {
            startedUtc = default;
            return false;
        }
    }

    /// <summary>
    /// A false return means the summed counters are incomplete rather than zero; the counters still carry
    /// the reads that did succeed.
    /// </summary>
    private static bool TryReadCounters(Process process, bool hasHandles, out ProcessCounters counters)
    {
        var read = TryRefresh(process);
        read &= TryReadLong(process, static instance => (long)instance.TotalProcessorTime.TotalMilliseconds, out var cpuMilliseconds);
        read &= TryReadLong(process, static instance => instance.PrivateMemorySize64, out var privateBytes);
        read &= TryReadLong(process, static instance => instance.WorkingSet64, out var workingSet);
        read &= TryReadLong(process, static instance => instance.PeakWorkingSet64, out var peakWorkingSet);
        read &= TryReadLong(process, static instance => instance.Threads.Count, out var threads);
        long handles = 0;
        if (hasHandles)
        {
            read &= TryReadLong(process, static instance => instance.HandleCount, out handles);
        }

        counters = new ProcessCounters(cpuMilliseconds / 1000.0, privateBytes, workingSet, peakWorkingSet, threads, handles);
        return read;
    }

    private static void ReportToStderr(string message)
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

    private static void WriteCounters(Utf8JsonWriter writer, in ProcessCounters counters)
    {
        writer.WriteNumber("cpuSeconds", JsonValue.Round(counters.CpuSeconds, 4));
        writer.WriteNumber("privateBytes", counters.PrivateBytes);
        writer.WriteNumber("workingSetBytes", counters.WorkingSet);
        writer.WriteNumber("peakWorkingSetBytes", counters.PeakWorkingSet);
        writer.WriteNumber("threads", counters.Threads);
    }

    /// <summary>
    /// Writes what was summed plus an explicit marker for the processes that could not be read. The
    /// per-process counters are null rather than zero for those, because a zero here is not a measurement.
    /// </summary>
    private static void WriteProcesses(Utf8JsonWriter writer, IReadOnlyList<SampledProcess> processes)
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
                writer.WriteNumber("cpuSeconds", JsonValue.Round(process.CpuSeconds, 4));
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

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var target = _target;
                if (target is null)
                {
                    continue;
                }

                await SampleAllAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                /* the backstop: sampling must survive any single tick's failure and report it, not end */
                ReportToStderr($"e2e client: resource sampler tick failed for arm {_target?.Arm ?? "?"}: {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async ValueTask SampleAllAsync(SamplerTarget target, CancellationToken cancellationToken)
    {
        try
        {
            await SampleSelfAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ReportSamplingFailureAsync(target, "self", exception, cancellationToken).ConfigureAwait(false);
        }

        foreach (var name in _processNames)
        {
            try
            {
                await SampleNamedAsync(target, name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await ReportSamplingFailureAsync(target, name, exception, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Records a sampling failure where the arm's data lives, so a persistent failure is visible in the
    /// series instead of only in the exception the sampler would otherwise throw at dispose.
    /// </summary>
    private static async ValueTask ReportSamplingFailureAsync(SamplerTarget target, string process, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            await target.Sink.WriteAsync(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "samplerError");
                    writer.WriteNumber("ticks", Stopwatch.GetTimestamp());
                    writer.WriteString("arm", target.Arm);
                    writer.WriteString("process", process);
                    writer.WriteString("error", exception.GetType().Name);
                    writer.WriteString("message", exception.Message);
                    writer.WriteEndObject();
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

    private static async ValueTask SampleSelfAsync(SamplerTarget target, CancellationToken cancellationToken)
    {
        using var process = Process.GetCurrentProcess();
        var ticks = Stopwatch.GetTimestamp();
        var hasHandles = OperatingSystem.IsWindows();

        var countersRead = TryReadCounters(process, hasHandles, out var counters);
        var hasStartTime = TryReadStartTime(process, out var startedUtc);
        var environmentWorkingSet = Environment.WorkingSet;
        var processName = process.ProcessName;
        var identity = new SampledProcess(process.Id, hasStartTime ? startedUtc : null, countersRead, counters.CpuSeconds, counters.PrivateBytes);

        await target.Sink.WriteAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "sample");
                writer.WriteNumber("ticks", ticks);
                writer.WriteString("arm", target.Arm);
                writer.WriteString("process", processName);
                writer.WriteBoolean("self", value: true);
                writer.WriteNumber("matched", 1);
                WriteCounters(writer, counters);
                if (hasHandles)
                {
                    writer.WriteNumber("handles", counters.Handles);
                }
                else
                {
                    writer.WriteNull("handles");
                }

                WriteProcesses(writer, [identity]);
                writer.WriteNumber("generatorCpuSeconds", JsonValue.Round(counters.CpuSeconds, 4));
                writer.WriteNumber("envWorkingSetBytes", environmentWorkingSet);
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static ProcessTotals ReadProcessTotals(string name, bool hasHandles)
    {
        var totals = new ProcessTotals();
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch (InvalidOperationException)
        {
            /* the process exited between enumeration and read */
            return totals;
        }
        catch (Win32Exception)
        {
            /* the process exited between enumeration and read */
            return totals;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                var countersRead = TryReadCounters(process, hasHandles, out var counters);
                var hasStartTime = TryReadStartTime(process, out var startedUtc);
                totals.Processes.Add(new SampledProcess(
                    process.Id,
                    hasStartTime ? startedUtc : null,
                    countersRead,
                    counters.CpuSeconds,
                    counters.PrivateBytes));
                totals.CpuSeconds += counters.CpuSeconds;
                totals.PrivateBytes += counters.PrivateBytes;
                totals.WorkingSet += counters.WorkingSet;
                totals.PeakWorkingSet += counters.PeakWorkingSet;
                totals.Threads += counters.Threads;
                totals.Handles += counters.Handles;
            }
        }

        return totals;
    }

    private static async ValueTask SampleNamedAsync(SamplerTarget target, string name, CancellationToken cancellationToken)
    {
        var ticks = Stopwatch.GetTimestamp();
        var totals = new ProcessTotals();
        var hasHandles = OperatingSystem.IsWindows();
        if (!string.IsNullOrEmpty(name))
        {
            totals = ReadProcessTotals(name, hasHandles);
        }

        await target.Sink.WriteAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "sample");
                writer.WriteNumber("ticks", ticks);
                writer.WriteString("arm", target.Arm);
                writer.WriteString("process", name);
                writer.WriteBoolean("self", value: false);
                writer.WriteNumber("matched", totals.Processes.Count);
                if (totals.Processes.Count == 0)
                {
                    writer.WriteBoolean("absent", value: true);
                }
                else
                {
                    var counters = new ProcessCounters(
                        totals.CpuSeconds,
                        totals.PrivateBytes,
                        totals.WorkingSet,
                        totals.PeakWorkingSet,
                        totals.Threads,
                        totals.Handles);
                    WriteCounters(writer, counters);
                    if (hasHandles)
                    {
                        writer.WriteNumber("handles", counters.Handles);
                    }
                    else
                    {
                        writer.WriteNull("handles");
                    }
                }

                WriteProcesses(writer, totals.Processes);
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
    }
}
