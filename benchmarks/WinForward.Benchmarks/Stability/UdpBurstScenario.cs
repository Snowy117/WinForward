using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinForward.Benchmarks.Perf;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// UDP flow-establishment burst soak through the real proxy dial path. A flash crowd of
/// <c>--burst-flows</c> new flows fires their first datagrams back-to-back (the pump
/// delivering a wave of DNS-shaped queries) while pre-established background flows keep
/// pacing through control, burst, and post windows. The row reports the per-flow
/// first-response latency distribution, establishment loss shapes, and per-window
/// background degradation, so the head-of-line cost of the establishment window is
/// quantified instead of hidden inside aggregate steady-state numbers.
/// </summary>
internal static class UdpBurstScenario
{
    private static readonly TimeSpan ControlWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PostWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarmupPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan BurstPollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan MinimumBurstWindow = TimeSpan.FromSeconds(30);
    private const int TickMilliseconds = 10;
    private const int SetupLimiterWidth = 8;

    /// <summary>
    /// Opt-in switch for the product-event census, mirroring <see cref="UdpLossScenario"/>'s
    /// pattern: enabling it makes the product emit per-datagram trace events, which
    /// allocates and slows the send/receive paths; default runs stay undistorted and the
    /// productEvents field is omitted from the row.
    /// </summary>
    private const bool CaptureProductEvents = false;

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        var backgroundFlows = options.Flows;
        var burstFlows = options.BurstFlows;
        var associateDelay = TimeSpan.FromMilliseconds(options.DialDelayMs);
        var burstTimeout = ComputeBurstTimeout(burstFlows, associateDelay);

        await using var receiver = new EchoReceiver(backgroundFlows + burstFlows);
        await using var server = new LoopbackSocks5UdpServer(receiver.Endpoint, associateDelay);
        var tracker = new InFlightTracker(Math.Max(1024, options.Pps * 12));
        var sink = new BurstCountingSink(backgroundFlows, burstFlows, tracker);
        var productEvents = CaptureProductEvents ? new CountingRuntimeLogger() : null;
        var coordinator = new UdpProxyCoordinator(
            new Socks5UdpTransportFactory(new SelfTrafficRegistry(), UdpFrameBuilder.DefaultMaximumEthernetFrame),
            sink,
            backgroundFlows + burstFlows,
            logger: productEvents is not null ? productEvents : NullRuntimeLogger.Instance);
        PhaseOutcome outcome;
        try
        {
            var socksServer = new Socks5Server("soak", "127.0.0.1", checked((ushort)server.ControlEndpoint.Port), null, null);
            var backgroundKeys = CreateFlowKeys(0, backgroundFlows);
            var burstKeys = CreateFlowKeys(backgroundFlows, burstFlows);
            outcome = await RunPhasesAsync(coordinator, socksServer, backgroundKeys, burstKeys, sink, tracker, options, burstTimeout)
                .ConfigureAwait(false);
        }
        finally
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
        }

        context.WriteResult(
            "udp.burstEstablishment",
            new
            {
                burstFlows = options.BurstFlows,
                dialDelayMs = options.DialDelayMs,
                backgroundFlows = options.Flows,
                backgroundPps = options.Pps,
                payloadBytes = options.PayloadBytes,
                seed = options.Seed,
            },
            BuildMetrics(outcome, sink, productEvents));
    }

    /// <summary>
    /// The burst window must outlive the serialized setup chain it measures: with a
    /// <see cref="SetupLimiterWidth"/>-wide setup concurrency and a per-flow dial of
    /// <paramref name="associateDelay"/>, the last flow's first response needs roughly
    /// ceil(N/8) × delay; three times that plus the 30 s floor absorbs scheduling noise.
    /// </summary>
    private static TimeSpan ComputeBurstTimeout(int burstFlows, TimeSpan associateDelay) =>
        TimeSpan.FromMilliseconds(Math.Max(
            MinimumBurstWindow.TotalMilliseconds,
            Math.Ceiling(burstFlows / (double)SetupLimiterWidth) * associateDelay.TotalMilliseconds * 3));

    private static FlowKey[] CreateFlowKeys(int flowIdOffset, int count)
    {
        var keys = new FlowKey[count];
        for (var index = 0; index < count; index++) keys[index] = BenchmarkShared.CreateFlowKey(flowIdOffset + index);
        return keys;
    }

    /// <summary>Runs warmup, the three measurement windows, and the drain; disposal stays with the caller.</summary>
    private static async Task<PhaseOutcome> RunPhasesAsync(
        UdpProxyCoordinator coordinator,
        Socks5Server socksServer,
        FlowKey[] backgroundKeys,
        FlowKey[] burstKeys,
        BurstCountingSink sink,
        InFlightTracker tracker,
        SoakOptions options,
        TimeSpan burstTimeout)
    {
        var payload = new byte[options.PayloadBytes];
        for (var flow = 0; flow < backgroundKeys.Length; flow++)
        {
            DatagramHeader.Write(payload, 1, flow);
            _ = await coordinator.TrySendAsync(backgroundKeys[flow], socksServer, payload, CancellationToken.None).ConfigureAwait(false);
        }

        // A flow counts as established once its warmup response has returned through the
        // sink, so no warmup echo can straddle the attribution boundary below.
        var warmupWatch = Stopwatch.StartNew();
        while (sink.WarmupResponses < backgroundKeys.Length && warmupWatch.Elapsed < WarmupTimeout)
        {
            await Task.Delay(WarmupPollInterval).ConfigureAwait(false);
        }

        tracker.BeginAttribution();
        var windowTicks = new long[4];
        windowTicks[0] = Stopwatch.GetTimestamp();
        using var senderCancellation = new CancellationTokenSource();
        var sender = new BackgroundSender(coordinator, socksServer, backgroundKeys, options.PayloadBytes, options.Pps, tracker);
        var senderTask = Task.Run(() => sender.RunLoopAsync(senderCancellation.Token));

        await Task.Delay(ControlWindow).ConfigureAwait(false);
        windowTicks[1] = Stopwatch.GetTimestamp();
        sender.EnterWindow(BackgroundWindow.Burst);
        var burst = await FireBurstAsync(coordinator, socksServer, burstKeys, backgroundKeys.Length, options.PayloadBytes, burstTimeout, sink).ConfigureAwait(false);
        windowTicks[2] = Stopwatch.GetTimestamp();
        sender.EnterWindow(BackgroundWindow.Post);
        await Task.Delay(PostWindow).ConfigureAwait(false);
        windowTicks[3] = Stopwatch.GetTimestamp();
        await senderCancellation.CancelAsync().ConfigureAwait(false);
        await senderTask.ConfigureAwait(false);
        await Task.Delay(DrainTime).ConfigureAwait(false);
        return new PhaseOutcome(burst, sender, windowTicks);
    }

    /// <summary>
    /// Fires the burst: every new flow's first datagram goes out back-to-back in a tight
    /// sequential loop (the serialized pump shape), each stamped with its own issue
    /// timestamp; then waits until every accepted flow's first response is observed or the
    /// adaptive timeout elapses.
    /// </summary>
    private static async Task<BurstResult> FireBurstAsync(
        UdpProxyCoordinator coordinator,
        Socks5Server socksServer,
        FlowKey[] burstKeys,
        int flowIdOffset,
        int payloadBytes,
        TimeSpan timeout,
        BurstCountingSink sink)
    {
        var payload = new byte[payloadBytes];
        var issueTimestamps = new long[burstKeys.Length];
        long accepted = 0;
        long rejected = 0;
        for (var flow = 0; flow < burstKeys.Length; flow++)
        {
            issueTimestamps[flow] = Stopwatch.GetTimestamp();
            DatagramHeader.Write(payload, 1, flowIdOffset + flow);
            if (await coordinator.TrySendAsync(burstKeys[flow], socksServer, payload, CancellationToken.None).ConfigureAwait(false))
            {
                accepted++;
            }
            else
            {
                rejected++;
            }
        }

        var issueEndTicks = Stopwatch.GetTimestamp();
        var stopwatch = Stopwatch.StartNew();
        while (sink.BurstFirstResponses < accepted && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(BurstPollInterval).ConfigureAwait(false);
        }

        var latencies = new List<double>(burstKeys.Length);
        for (var flow = 0; flow < burstKeys.Length; flow++)
        {
            if (sink.TryGetBurstFirstResponseTicks(flow) is long responseTicks)
            {
                latencies.Add(TicksToMilliseconds(responseTicks - issueTimestamps[flow]));
            }
        }

        var firstResponses = (long)latencies.Count;
        var lossRate = accepted == 0 ? 0.0 : Math.Clamp(1.0 - firstResponses / (double)accepted, 0.0, 1.0);
        return new BurstResult(
            accepted,
            rejected,
            firstResponses,
            lossRate,
            LatencyDistribution.FromMilliseconds(latencies),
            TicksToMilliseconds(issueEndTicks - issueTimestamps[0]));
    }

    private static object BuildMetrics(PhaseOutcome outcome, BurstCountingSink sink, CountingRuntimeLogger? productEvents)
    {
        var burst = outcome.Burst;
        var sender = outcome.Sender;
        var ticks = outcome.WindowTicks;
        return new
        {
            burstAccepted = burst.BurstAccepted,
            burstRejected = burst.BurstRejected,
            firstResponses = burst.FirstResponses,
            establishmentLossRate = burst.EstablishmentLossRate,
            firstResponseMs = burst.FirstResponseMs,
            timeToFirstMs = burst.FirstResponseMs.Min,
            timeToLastMs = burst.FirstResponseMs.Max,
            timeToIssueMs = burst.TimeToIssueMs,
            background = new
            {
                control = BuildWindowMetrics(sender.SentIn(BackgroundWindow.Control), sink.InjectedIn(BackgroundWindow.Control), sender.SendLatenciesIn(BackgroundWindow.Control), TicksToSeconds(ticks[1] - ticks[0])),
                burst = BuildWindowMetrics(sender.SentIn(BackgroundWindow.Burst), sink.InjectedIn(BackgroundWindow.Burst), sender.SendLatenciesIn(BackgroundWindow.Burst), TicksToSeconds(ticks[2] - ticks[1])),
                post = BuildWindowMetrics(sender.SentIn(BackgroundWindow.Post), sink.InjectedIn(BackgroundWindow.Post), sender.SendLatenciesIn(BackgroundWindow.Post), TicksToSeconds(ticks[3] - ticks[2])),
            },
            unattributedResponses = sink.UnattributedResponses,
            productEvents = productEvents is not null ? BuildProductEvents(productEvents) : null,
        };
    }

    private static object BuildWindowMetrics(long sent, long injected, IReadOnlyList<double> sendLatencies, double windowSeconds) => new
    {
        sent,
        injected,
        lossRate = sent == 0 ? 0.0 : Math.Clamp(1.0 - injected / (double)sent, 0.0, 1.0),
        sendP95Ms = Percentile(sendLatencies, 95),
        sendMaxMs = sendLatencies.Count == 0 ? 0.0 : sendLatencies.Max(),
        achievedPps = windowSeconds > 0.0 ? sent / windowSeconds : 0.0,
    };

    /// <summary>Nearest-rank percentile over an unsorted sample: the ceil(p% × n)-th value (1-based); zero when empty.</summary>
    private static double Percentile(IReadOnlyList<double> samples, int percentile)
    {
        if (samples.Count == 0) return 0.0;
        var sorted = samples.ToArray();
        Array.Sort(sorted);
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    private static double TicksToMilliseconds(long deltaTicks) => deltaTicks * 1000.0 / Stopwatch.Frequency;

    private static double TicksToSeconds(long deltaTicks) => deltaTicks / (double)Stopwatch.Frequency;

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

    private static Dictionary<string, long> BuildProductEvents(CountingRuntimeLogger logger)
    {
        var snapshot = new Dictionary<string, long>(ProductEventNames.Length, StringComparer.Ordinal);
        foreach (var name in ProductEventNames)
        {
            snapshot[name] = logger.Events.TryGetValue(name, out var count) ? count : 0;
        }

        return snapshot;
    }

    /// <summary>
    /// Diagnostic-only product-event census: counts every Event() call by name (both Trace
    /// and Debug) with no formatting or I/O. Enabling Trace makes the product emit its
    /// per-datagram trace events, which allocates and slows the send/receive paths — rows
    /// produced this way localize establishment failures but are not latency-comparable
    /// with uninstrumented runs.
    /// </summary>
    private sealed class CountingRuntimeLogger : IRuntimeLogger
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

    /// <summary>The measurement windows a background datagram can be attributed to, in phase order.</summary>
    private enum BackgroundWindow
    {
        Control = 0,
        Burst = 1,
        Post = 2,
    }

    /// <summary>A per-datagram in-flight stamp: the window it was sent in and the send timestamp.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct InFlightStamp(byte Window, long SendTicks);

    /// <summary>Everything the three measurement windows produced, plus the sender holding their counters.</summary>
    private sealed record PhaseOutcome(BurstResult Burst, BackgroundSender Sender, long[] WindowTicks);

    /// <summary>Per-burst outcome: admission counts, first-response distribution, and issue-loop cost.</summary>
    private sealed record BurstResult(
        long BurstAccepted,
        long BurstRejected,
        long FirstResponses,
        double EstablishmentLossRate,
        LatencyDistribution FirstResponseMs,
        double TimeToIssueMs);

    /// <summary>min/p50/p95/p99/max/mean over a latency sample; all zeros when the sample is empty.</summary>
    private sealed record LatencyDistribution(double Min, double P50, double P95, double P99, double Max, double Mean)
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
    /// The bounded in-flight stamp table shared by the background sender and the response
    /// sink: the sender stamps every outgoing datagram, the sink matches responses by
    /// (flow, sequence) and removes the stamp. At capacity a new stamp is refused and its
    /// response degrades to the unattributed counter — the send loop is never blocked.
    /// </summary>
    private sealed class InFlightTracker(int capacity)
    {
        private readonly ConcurrentDictionary<long, InFlightStamp> _stamps = new();
        private readonly int _capacity = capacity;
        private long _count;
        private volatile bool _active;

        /// <summary>Warmup datagrams and their responses predate attribution and stay untracked.</summary>
        public void BeginAttribution() => _active = true;

        public bool AttributionActive => _active;

        public void Stamp(int flowId, long sequence, BackgroundWindow window, long sendTicks)
        {
            if (!_active) return;
            if (Interlocked.Increment(ref _count) > _capacity)
            {
                Interlocked.Decrement(ref _count);
                return;
            }

            _stamps[Key(flowId, sequence)] = new InFlightStamp((byte)window, sendTicks);
        }

        public void Remove(int flowId, long sequence)
        {
            if (!_active) return;
            if (_stamps.TryRemove(Key(flowId, sequence), out _)) Interlocked.Decrement(ref _count);
        }

        public bool TryTake(int flowId, long sequence, out InFlightStamp stamp)
        {
            if (_stamps.TryRemove(Key(flowId, sequence), out stamp))
            {
                Interlocked.Decrement(ref _count);
                return true;
            }

            return false;
        }

        private static long Key(int flowId, long sequence) => ((long)flowId << 32) | (uint)sequence;
    }

    /// <summary>
    /// The paced background sender round-robin over the pre-established flows. It runs
    /// through the control, burst, and post windows; per-window sent counts and
    /// send-accept latencies are written only by this sender's loop and read only after
    /// its task completes.
    /// </summary>
    private sealed class BackgroundSender(
        UdpProxyCoordinator coordinator,
        Socks5Server socksServer,
        FlowKey[] flows,
        int payloadBytes,
        int pps,
        InFlightTracker tracker)
    {
        // Sequences continue after the warmup datagram (sequence 1 per flow), keeping the
        // in-flight (flow, sequence) keys globally unique across the whole scenario.
        private readonly long[] _sequences = CreateSequencesAfterWarmup(flows.Length);
        private readonly byte[] _payload = new byte[payloadBytes];
        private readonly List<double>[] _sendLatencies = [new(), new(), new()];
        private readonly long[] _sentPerWindow = new long[3];
        private volatile int _currentWindow = (int)BackgroundWindow.Control;

        private static long[] CreateSequencesAfterWarmup(int flowCount)
        {
            var sequences = new long[flowCount];
            for (var flow = 0; flow < flowCount; flow++) sequences[flow] = 1;
            return sequences;
        }

        public void EnterWindow(BackgroundWindow window) => _currentWindow = (int)window;

        public long SentIn(BackgroundWindow window) => Volatile.Read(ref _sentPerWindow[(int)window]);

        public IReadOnlyList<double> SendLatenciesIn(BackgroundWindow window) => _sendLatencies[(int)window];

        public async Task RunLoopAsync(CancellationToken cancellation)
        {
            var perTick = pps * TickMilliseconds / 1000.0;
            var accumulator = 0.0;
            var tick = TimeSpan.FromMilliseconds(TickMilliseconds);
            var stopwatch = Stopwatch.StartNew();
            var nextDeadline = stopwatch.Elapsed;
            var nextFlow = 0;
            try
            {
                while (true)
                {
                    accumulator += perTick;
                    var datagrams = (int)accumulator;
                    accumulator -= datagrams;
                    for (var index = 0; index < datagrams; index++)
                    {
                        var flow = nextFlow++ % flows.Length;
                        _sequences[flow]++;
                        var window = _currentWindow;
                        var sendTicks = Stopwatch.GetTimestamp();
                        DatagramHeader.Write(_payload, _sequences[flow], flow);
                        tracker.Stamp(flow, _sequences[flow], (BackgroundWindow)window, sendTicks);
                        if (await coordinator.TrySendAsync(flows[flow], socksServer, _payload, cancellation).ConfigureAwait(false))
                        {
                            _sentPerWindow[window]++;
                            _sendLatencies[window].Add(TicksToMilliseconds(Stopwatch.GetTimestamp() - sendTicks));
                        }
                        else
                        {
                            tracker.Remove(flow, _sequences[flow]);
                        }
                    }

                    nextDeadline += tick;
                    var remaining = nextDeadline - stopwatch.Elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, cancellation).ConfigureAwait(false);
                    }
                    else
                    {
                        nextDeadline = stopwatch.Elapsed;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The window sequence completed; the drain phase collects straggler responses.
            }
        }
    }

    /// <summary>
    /// The response sink carrying both measurements: burst flows' first responses are
    /// CAS-recorded per flow against a zero-initialized timestamp array; background
    /// responses are matched against the in-flight stamps for window attribution. Anything
    /// it cannot attribute (overflow-degraded stamps, unexpected flow ids) lands in the
    /// unattributed counter instead of vanishing silently.
    /// </summary>
    private sealed class BurstCountingSink(int backgroundFlows, int burstFlows, InFlightTracker tracker) : IUdpResponseSink
    {
        private readonly long[] _burstFirstResponseTicks = new long[burstFlows];
        private readonly long[] _injectedPerWindow = new long[3];
        private long _burstFirstResponses;
        private long _warmupResponses;
        private long _unattributed;

        public long BurstFirstResponses => Interlocked.Read(ref _burstFirstResponses);

        /// <summary>Warmup datagrams whose response has already returned; the warmup wait polls this.</summary>
        public long WarmupResponses => Interlocked.Read(ref _warmupResponses);

        public long UnattributedResponses => Interlocked.Read(ref _unattributed);

        public long InjectedIn(BackgroundWindow window) => Volatile.Read(ref _injectedPerWindow[(int)window]);

        /// <summary>The burst flow's first-response timestamp, or null when no response was ever observed.</summary>
        public long? TryGetBurstFirstResponseTicks(int burstIndex)
        {
            var ticks = Volatile.Read(ref _burstFirstResponseTicks[burstIndex]);
            return ticks == 0 ? null : ticks;
        }

        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken)
        {
            if (!DatagramHeader.TryRead(payload.Span, out var sequence, out var flowId))
            {
                Interlocked.Increment(ref _unattributed);
            }
            else if (flowId >= backgroundFlows && flowId < backgroundFlows + burstFlows)
            {
                if (Interlocked.CompareExchange(ref _burstFirstResponseTicks[flowId - backgroundFlows], Stopwatch.GetTimestamp(), 0) == 0)
                {
                    Interlocked.Increment(ref _burstFirstResponses);
                }
            }
            else if ((uint)flowId < (uint)backgroundFlows)
            {
                if (!tracker.AttributionActive)
                {
                    Interlocked.Increment(ref _warmupResponses);
                }
                else if (tracker.TryTake(flowId, sequence, out var stamp))
                {
                    Interlocked.Increment(ref _injectedPerWindow[stamp.Window]);
                }
                else
                {
                    // A stamp degraded by the in-flight bound or a lost entry — a real miss.
                    Interlocked.Increment(ref _unattributed);
                }
            }
            else
            {
                Interlocked.Increment(ref _unattributed);
            }

            return ValueTask.CompletedTask;
        }
    }
}
