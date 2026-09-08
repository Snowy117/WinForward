using System.Collections.Concurrent;
using System.Diagnostics;
using WinForward.Configuration;
using WinForward.Runtime;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// Shared plumbing for the stability scenarios: latency distribution and percentile
/// math, stopwatch tick conversion, and the diagnostic product-event census — the
/// Stability counterpart of the Perf benchmarks' <c>BenchmarkShared</c>.
/// </summary>
internal static class StabilityShared
{
    /// <summary>Nearest-rank percentile over an unsorted sample: the ceil(p% × n)-th value (1-based); zero when empty.</summary>
    internal static double Percentile(IReadOnlyList<double> samples, int percentile)
    {
        if (samples.Count == 0) return 0.0;
        var sorted = samples.ToArray();
        Array.Sort(sorted);
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    internal static double TicksToMilliseconds(long deltaTicks) => deltaTicks * 1000.0 / Stopwatch.Frequency;

    internal static double TicksToSeconds(long deltaTicks) => deltaTicks / (double)Stopwatch.Frequency;

    /// <summary>Product trace/debug event names surfaced in the result row; absent names count as zero.</summary>
    private static readonly string[] ProductEventNames =
    [
        "udp.setupqueue.dropped",
        "udp.session.rejected",
        "udp.setup.failed",
        "udp.setup.cooldown",
        "udp.packet.sent",
        "udp.session.created",
        "udp.session.closed",
        "udp.session.expired",
    ];

    internal static Dictionary<string, long> BuildProductEvents(CountingRuntimeLogger logger)
    {
        var snapshot = new Dictionary<string, long>(ProductEventNames.Length, StringComparer.Ordinal);
        foreach (var name in ProductEventNames)
        {
            snapshot[name] = logger.Events.TryGetValue(name, out var count) ? count : 0;
        }

        return snapshot;
    }
}

/// <summary>min/p50/p95/p99/max/mean over a latency sample; all zeros when the sample is empty.</summary>
internal sealed record LatencyDistribution(double Min, double P50, double P95, double P99, double Max, double Mean)
{
    public static LatencyDistribution FromMilliseconds(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) return new LatencyDistribution(0, 0, 0, 0, 0, 0);
        var sorted = samples.ToArray();
        Array.Sort(sorted);
        double total = 0;
        foreach (var value in sorted) total += value;
        return new LatencyDistribution(
            sorted[0],
            Rank(sorted, 50),
            Rank(sorted, 95),
            Rank(sorted, 99),
            sorted[^1],
            total / sorted.Length);
    }

    private static double Rank(double[] sorted, int percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}

/// <summary>
/// Diagnostic-only product-event census: counts every Event() call by name (both Trace
/// and Debug) with no formatting or I/O. Enabling Trace makes the product emit its
/// per-datagram trace events (udp.packet.sent/received), which allocates and slows the
/// send/receive paths — rows produced this way localize loss or establishment failures
/// but are not throughput/latency-comparable with uninstrumented runs.
/// </summary>
internal sealed class CountingRuntimeLogger : IRuntimeLogger
{
    private readonly ConcurrentDictionary<string, long> _events = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, long> Events => _events;

    public bool IsEnabled(RuntimeLogLevel level) => true;

    public void Info(string message) { }

    public void Warn(string message) { }

    public void Error(string message) { }

    public void Event(RuntimeLogLevel level, string eventName, params RuntimeLogField[] fields)
        => _events.AddOrUpdate(eventName, 1, static (_, count) => count + 1);
}
