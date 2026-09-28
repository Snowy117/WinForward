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

    /// <summary>
    /// Product event names surfaced in the result row — per-datagram trace/debug events plus the
    /// rate-limited or one-shot warn summaries (<c>udp.session.capacity-block</c>,
    /// <c>udp.association.fallback</c>); absent names count as zero.
    /// </summary>
    private static readonly string[] s_productEventNames =
    [
        "udp.setupqueue.dropped",
        "udp.session.rejected",
        "udp.session.capacity-block",
        "udp.setup.failed",
        "udp.setup.cooldown",
        "udp.association.fallback",
        "udp.packet.sent",
        "udp.session.created",
        "udp.session.closed",
        "udp.session.expired",
    ];

    internal static Dictionary<string, long> BuildProductEvents(CountingRuntimeLogger logger)
    {
        var snapshot = new Dictionary<string, long>(s_productEventNames.Length, StringComparer.Ordinal);
        foreach (var name in s_productEventNames)
        {
            snapshot[name] = logger.Events.GetValueOrDefault(name);
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
/// Diagnostic-only product-event census: counts every Event() call by name with no formatting or
/// I/O. With <paramref name="includeVerbose"/> false only <see cref="RuntimeLogLevel.Warn"/> is
/// enabled, so the product's own <c>IsEnabled</c> guards keep the per-datagram trace/debug events out
/// of the send and receive paths — a distortion-free census of the rate-limited and one-shot warns
/// (<c>udp.session.capacity-block</c>, <c>udp.association.fallback</c>) that every stability row can
/// carry. With it true the product also emits its per-datagram trace events
/// (udp.packet.sent/received), which allocates and slows those paths: rows produced that way localize
/// loss or establishment failures but are not throughput/latency-comparable with uninstrumented runs.
/// </summary>
internal sealed class CountingRuntimeLogger(bool includeVerbose = true) : IRuntimeLogger
{
    private readonly ConcurrentDictionary<string, long> _events = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, long> Events => _events;

    public bool IsEnabled(RuntimeLogLevel level) => includeVerbose || level == RuntimeLogLevel.Warn;

    public void Info(string message) { }

    public void Warn(string message) { }

    public void Error(string message) { }

    public void Event(RuntimeLogLevel level, string eventName, params RuntimeLogField[] fields)
        => _events.AddOrUpdate(eventName, 1, static (_, count) => count + 1);
}
