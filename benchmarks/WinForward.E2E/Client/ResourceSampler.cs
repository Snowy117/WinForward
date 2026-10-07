using System.Diagnostics;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Client;

/// <summary>The arm the sampler's next tick writes into: that arm's sink, and the name it is booked under.</summary>
internal sealed record SamplerTarget(JsonlSink Sink, string Arm);

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

    internal void SetTarget(JsonlSink sink, string arm) => _target = new SamplerTarget(sink, arm);

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
            ResourceSampleWriter.ReportToStderr($"e2e client: resource sampler stopped: {exception.GetType().Name}: {exception.Message}");
        }

        _shutdown.Dispose();
        _gate.Dispose();
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
                ResourceSampleWriter.ReportToStderr($"e2e client: resource sampler tick failed for arm {_target?.Arm ?? "?"}: {exception.GetType().Name}: {exception.Message}");
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
            await ResourceSampleWriter.ReportSamplingFailureAsync(target, "self", exception, cancellationToken).ConfigureAwait(false);
        }

        foreach (var name in _processNames)
        {
            try
            {
                await SampleNamedAsync(target, name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await ResourceSampleWriter.ReportSamplingFailureAsync(target, name, exception, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask SampleSelfAsync(SamplerTarget target, CancellationToken cancellationToken)
    {
        using var process = Process.GetCurrentProcess();
        var ticks = Stopwatch.GetTimestamp();
        var hasHandles = OperatingSystem.IsWindows();

        var countersRead = ProcessCounterSource.TryReadCounters(process, hasHandles, out var counters);
        var hasStartTime = ProcessCounterSource.TryReadStartTime(process, out var startedUtc);
        var environmentWorkingSet = Environment.WorkingSet;
        var processName = process.ProcessName;
        var identity = new SampledProcess(process.Id, hasStartTime ? startedUtc : null, countersRead, counters.CpuSeconds, counters.PrivateBytes);

        await target.Sink.WriteAsync(
            writer =>
            {
                writer.WriteString("type", "sample");
                writer.WriteNumber("ticks", ticks);
                writer.WriteString("arm", target.Arm);
                writer.WriteString("process", processName);
                writer.WriteBoolean("self", value: true);
                writer.WriteNumber("matched", 1);
                ResourceSampleWriter.WriteCounters(writer, counters);
                if (hasHandles)
                {
                    writer.WriteNumber("handles", counters.Handles);
                }
                else
                {
                    writer.WriteNull("handles");
                }

                ResourceSampleWriter.WriteProcesses(writer, [identity]);
                writer.WriteNumber("generatorCpuSeconds", NumberFormat.Round(counters.CpuSeconds, 4));
                writer.WriteNumber("envWorkingSetBytes", environmentWorkingSet);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SampleNamedAsync(SamplerTarget target, string name, CancellationToken cancellationToken)
    {
        var ticks = Stopwatch.GetTimestamp();
        var totals = new ProcessTotals();
        var hasHandles = OperatingSystem.IsWindows();
        if (!string.IsNullOrEmpty(name))
        {
            totals = ProcessCounterSource.ReadProcessTotals(name, hasHandles);
        }

        await target.Sink.WriteAsync(
            writer =>
            {
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
                    ResourceSampleWriter.WriteCounters(writer, counters);
                    if (hasHandles)
                    {
                        writer.WriteNumber("handles", counters.Handles);
                    }
                    else
                    {
                        writer.WriteNull("handles");
                    }
                }

                ResourceSampleWriter.WriteProcesses(writer, totals.Processes);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
