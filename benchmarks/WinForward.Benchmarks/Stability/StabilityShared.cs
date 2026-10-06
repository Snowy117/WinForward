using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    /// rate-limited or one-shot warn summaries (<c>udp.session.capacity-block</c>); absent names
    /// count as zero.
    /// </summary>
    private static readonly string[] s_productEventNames =
    [
        "udp.setupqueue.dropped",
        "udp.session.rejected",
        "udp.session.capacity-block",
        "udp.setup.failed",
        "udp.setup.cooldown",
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

/// <summary>
/// The loopback SOCKS5 server's own handshake counters: control connections accepted and UDP
/// ASSOCIATE replies written. A relayed flow pays both before its first datagram can move, so flows
/// that moved without them did not use this server — which is exactly what the local column's row
/// has to show. <see langword="null"/> means the server ran out of process (<c>--socks5-external</c>)
/// and the parent holds no counter to read: the field is omitted rather than reported as a zero
/// nobody observed.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct Socks5HandshakeCounters(long ControlConnections, long AssociateReplies)
{
    /// <summary>The server's counters right now; null when the server ran out of process and the parent holds no counter to read.</summary>
    internal static Socks5HandshakeCounters? Snapshot(LoopbackSocks5UdpServer? server) =>
        server is null ? null : new Socks5HandshakeCounters(server.ControlConnections, server.AssociateReplies);

    /// <summary>The handshakes between two snapshots; null when either side is missing.</summary>
    internal static Socks5HandshakeCounters? Delta(Socks5HandshakeCounters? after, Socks5HandshakeCounters? before) =>
        after is { } end && before is { } start
            ? new Socks5HandshakeCounters(end.ControlConnections - start.ControlConnections, end.AssociateReplies - start.AssociateReplies)
            : null;
}

/// <summary>
/// The local responder's counters: the datagrams that arrived on the local endpoint and the answers
/// it returned. Read beside <see cref="Socks5HandshakeCounters"/>, so one row shows the local
/// column's zero handshakes next to the traffic the local hop really carried, and the SOCKS5
/// columns' non-zero handshakes next to a local hop that carried nothing.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct LocalResponderCounters(long DatagramsReceived, long DatagramsReplied)
{
    internal static LocalResponderCounters Snapshot(LoopbackLocalUdpResponder responder) =>
        new(responder.DatagramsReceived, responder.DatagramsReplied);

    /// <summary>The datagrams handled between two snapshots.</summary>
    internal static LocalResponderCounters Delta(LocalResponderCounters after, LocalResponderCounters before) =>
        new(after.DatagramsReceived - before.DatagramsReceived, after.DatagramsReplied - before.DatagramsReplied);
}

/// <summary>
/// The loopback UoT server's own counters: the TCP connections it accepted (one per flow is the
/// shape the UoT column measures), the CONNECT replies it wrote for them, the datagram frames it
/// read off those connections, the framed echoes it wrote back, and the wire sequences it refused as
/// protocol violations. The first two are the direct analogue of
/// <see cref="Socks5HandshakeCounters"/>'s control connections and ASSOCIATE replies; the frame pair
/// is the traffic half, read beside <see cref="LocalResponderCounters"/> so the column's own
/// transport is visible rather than inferred. <see langword="null"/> means the column did not host
/// the fixture, and the field is then omitted rather than reported as a zero nobody observed.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct UotHandshakeCounters(long Connections, long ConnectReplies, long FramesReceived, long FramesReplied, long ProtocolViolations)
{
    /// <summary>The server's counters right now; null when the column did not host the fixture.</summary>
    internal static UotHandshakeCounters? Snapshot(LoopbackSocks5UotServer? server) =>
        server is null
            ? null
            : new UotHandshakeCounters(server.ControlConnections, server.ConnectReplies, server.FramesReceived, server.FramesReplied, server.ProtocolViolations);

    /// <summary>The counters between two snapshots; null when either side is missing.</summary>
    internal static UotHandshakeCounters? Delta(UotHandshakeCounters? after, UotHandshakeCounters? before) =>
        after is { } end && before is { } start
            ? new UotHandshakeCounters(
                end.Connections - start.Connections,
                end.ConnectReplies - start.ConnectReplies,
                end.FramesReceived - start.FramesReceived,
                end.FramesReplied - start.FramesReplied,
                end.ProtocolViolations - start.ProtocolViolations)
            : null;
}

/// <summary>
/// Every loopback server's counters at one instant. A row reports the difference between the
/// observation taken when its run's load started and the one read when the row was written, so "the
/// local column moved these flows without one SOCKS5 handshake" is a subtraction of two observed
/// numbers rather than a claim about the wiring. The difference is cumulative over the run rather
/// than per window: read the last row of a run for its totals, and subtract two rows for one wave's
/// own cost. A server the column did not host contributes null, so its field is omitted.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct TransportObservation(Socks5HandshakeCounters? Socks5Handshakes, UotHandshakeCounters? UotHandshakes, LocalResponderCounters LocalResponder)
{
    internal static TransportObservation Observe(LoopbackSocks5UdpServer? server, LoopbackSocks5UotServer? uotServer, LoopbackLocalUdpResponder responder) =>
        new(Socks5HandshakeCounters.Snapshot(server), UotHandshakeCounters.Snapshot(uotServer), LocalResponderCounters.Snapshot(responder));

    /// <summary>What each server did between this observation and now.</summary>
    internal TransportObservation Since(LoopbackSocks5UdpServer? server, LoopbackSocks5UotServer? uotServer, LoopbackLocalUdpResponder responder) =>
        new(
            Socks5HandshakeCounters.Delta(Socks5HandshakeCounters.Snapshot(server), Socks5Handshakes),
            UotHandshakeCounters.Delta(UotHandshakeCounters.Snapshot(uotServer), UotHandshakes),
            LocalResponderCounters.Delta(LocalResponderCounters.Snapshot(responder), LocalResponder));
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
/// (<c>udp.session.capacity-block</c>, <c>udp.setup.failed</c>, <c>udp.setup.cooldown</c>) that every
/// stability row can carry. With it true the product also emits its per-datagram trace events
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
