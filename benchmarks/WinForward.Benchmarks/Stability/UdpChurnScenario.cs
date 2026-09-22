using System.Diagnostics;
using System.Globalization;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// UDP session churn at the design bounds (task 09-21-session-creation-cost, design §4): waves of
/// <c>--burst-flows</c> short-lived sessions through the real dial path, each wave retired through
/// the coordinator's own idle-expiry path (<see cref="UdpProxyCoordinator.RemoveExpiredAsync"/> with
/// a zero timeout — the per-session teardown the periodic sweeper would drive), with per-wave
/// <c>GC.GetTotalAllocatedBytes</c> / GC-collection sampling. Wave mode (<c>--churn-waves K</c>,
/// K ≥ 1) fires K consecutive waves and emits one row per wave; sustained mode (<c>--churn-waves 0</c>)
/// cycles waves back-to-back for <c>--duration</c> seconds and emits one aggregate row, so the
/// limiter's realized session rate (≈ 8 / dial-delay) and the allocation rate over a fixed window
/// are observable. The flow keys are re-offered every wave (a client tuple re-querying after its
/// session expired), so a setup-failure cooldown shows up as that wave's establishment loss
/// instead of being hidden. Latency is reported as ordinals only; allocations and GC counts are
/// the readable outputs.
/// </summary>
internal static class UdpChurnScenario
{
    private const int MaximumSustainedWaves = 100_000;
    private const int SetupLimiterWidth = 8;
    private static readonly TimeSpan s_responsePollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan s_retireTimeout = TimeSpan.FromSeconds(30);

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        var flows = options.BurstFlows;
        var associateDelay = TimeSpan.FromMilliseconds(options.DialDelayMs);
        var waves = options.ChurnWaves;
        var sustained = waves <= 0;
        await using var receiver = new EchoReceiver(flows);
        await using var server = new LoopbackSocks5UdpServer(receiver.Endpoint, associateDelay);
        var sink = new ChurnCountingSink(flows);
        const int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;
        using var setupQueuePool = new NativeBufferPool(maximumFrameSize);
        using var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));
        using var setupExecutor = new SetupExecutor();
        var coordinator = new UdpProxyCoordinator(
            new Socks5UdpTransportFactory(new SelfTrafficRegistry(), maximumFrameSize),
            sink,
            setupQueuePool,
            receiveWindowPool,
            setupExecutor,
            new UdpProxyOptions { Capacity = flows });
        try
        {
            var socksServer = new Socks5Server("churn", "127.0.0.1", checked((ushort)server.ControlEndpoint.Port), Username: null, Password: null);
            var flowKeys = new FlowKey[flows];
            for (var index = 0; index < flowKeys.Length; index++) flowKeys[index] = BenchmarkShared.CreateFlowKey(index);
            var timeout = ComputeWaveTimeout(flows, associateDelay);
            if (sustained)
            {
                await RunSustainedAsync(context, coordinator, sink, socksServer, flowKeys, options, timeout).ConfigureAwait(false);
            }
            else
            {
                await RunWavesAsync(context, coordinator, sink, socksServer, flowKeys, options, waveCount: waves, timeout).ConfigureAwait(false);
            }
        }
        finally
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The wave window must outlive the serialized setup chain it measures: with an 8-wide limiter
    /// and a per-flow dial of <paramref name="associateDelay"/>, the last flow's first response
    /// needs roughly ceil(N/8) × delay; three times that plus the 30 s floor absorbs scheduling
    /// noise (mirrors <c>UdpBurstScenario.ComputeBurstTimeout</c>).
    /// </summary>
    private static TimeSpan ComputeWaveTimeout(int flows, TimeSpan associateDelay) =>
        TimeSpan.FromMilliseconds(Math.Max(
            TimeSpan.FromSeconds(30).TotalMilliseconds,
            Math.Ceiling(flows / (double)SetupLimiterWidth) * associateDelay.TotalMilliseconds * 3));

    private static async Task RunWavesAsync(
        StabilityContext context,
        UdpProxyCoordinator coordinator,
        ChurnCountingSink sink,
        Socks5Server socksServer,
        FlowKey[] flowKeys,
        SoakOptions options,
        int waveCount,
        TimeSpan timeout)
    {
        var payload = new byte[options.PayloadBytes];
        var sequence = await WarmUpAsync(coordinator, sink, socksServer, flowKeys, payload, timeout).ConfigureAwait(false);
        for (var wave = 0; wave < waveCount; wave++)
        {
            var sample = await RunWaveAsync(coordinator, sink, socksServer, flowKeys, payload, ++sequence, wave, timeout).ConfigureAwait(false);
            context.WriteResult(
                "udp.churn",
                new { burstFlows = options.BurstFlows, dialDelayMs = options.DialDelayMs, mode = "waves", wave, waves = waveCount },
                sample.BuildMetrics(payload.Length));
        }
    }

    /// <summary>
    /// Fires and retires one unmeasured wave before any sampling: the first wave of a process pays
    /// first-call JIT/tiering, the executor's worker-thread creation, and socket-stack warmup
    /// (measured ~2x the steady-state per-session allocation), so it would otherwise dominate a
    /// short wave-mode row. The same warmup discipline the burst scenario uses for its background
    /// flows. Returns the payload sequence consumed by the warmup wave.
    /// </summary>
    private static async Task<long> WarmUpAsync(
        UdpProxyCoordinator coordinator,
        ChurnCountingSink sink,
        Socks5Server socksServer,
        FlowKey[] flowKeys,
        byte[] payload,
        TimeSpan timeout)
    {
        _ = await RunWaveAsync(coordinator, sink, socksServer, flowKeys, payload, sequence: 1, wave: -1, timeout).ConfigureAwait(false);
        return 1;
    }

    private static async Task RunSustainedAsync(
        StabilityContext context,
        UdpProxyCoordinator coordinator,
        ChurnCountingSink sink,
        Socks5Server socksServer,
        FlowKey[] flowKeys,
        SoakOptions options,
        TimeSpan timeout)
    {
        var payload = new byte[options.PayloadBytes];
        var sequence = await WarmUpAsync(coordinator, sink, socksServer, flowKeys, payload, timeout).ConfigureAwait(false);
        var window = TimeSpan.FromSeconds(options.DurationSeconds);
        var totalWatch = Stopwatch.StartNew();
        long sessions = 0;
        long accepted = 0;
        long rejected = 0;
        long waveCount = 0;
        var perWaveBytesPerSession = new List<double>();
        var firstResponseLatencies = new List<double>();
        long gen0Before = GC.CollectionCount(0);
        long gen1Before = GC.CollectionCount(1);
        long gen2Before = GC.CollectionCount(2);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        while (totalWatch.Elapsed < window && waveCount < MaximumSustainedWaves)
        {
            var sample = await RunWaveAsync(coordinator, sink, socksServer, flowKeys, payload, ++sequence, wave: (int)(waveCount % uint.MaxValue), timeout).ConfigureAwait(false);
            waveCount++;
            sessions += flowKeys.Length;
            accepted += sample.Accepted;
            rejected += sample.Rejected;
            perWaveBytesPerSession.Add(sample.AllocatedBytes / (double)flowKeys.Length);
            firstResponseLatencies.AddRange(sample.FirstResponseMilliseconds);
        }

        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
        var elapsedSeconds = totalWatch.Elapsed.TotalSeconds;
        context.WriteResult(
            "udp.churn",
            new { burstFlows = options.BurstFlows, dialDelayMs = options.DialDelayMs, mode = "sustained", durationSeconds = options.DurationSeconds },
            new
            {
                waves = waveCount,
                sessions,
                accepted,
                rejected,
                establishmentLossRate = sessions == 0 ? 0.0 : Math.Clamp(1.0 - (accepted / (double)sessions), 0.0, 1.0),
                allocatedBytes,
                bytesPerSession = sessions == 0 ? 0.0 : allocatedBytes / (double)sessions,
                bytesPerSecond = elapsedSeconds <= 0 ? 0.0 : allocatedBytes / elapsedSeconds,
                achievedSessionsPerSecond = elapsedSeconds <= 0 ? 0.0 : sessions / elapsedSeconds,
                gen0Collections = GC.CollectionCount(0) - gen0Before,
                gen1Collections = GC.CollectionCount(1) - gen1Before,
                gen2Collections = GC.CollectionCount(2) - gen2Before,
                waveBytesPerSession = BuildDistribution(perWaveBytesPerSession),
                firstResponseMs = LatencyDistribution.FromMilliseconds(firstResponseLatencies),
                wallSeconds = elapsedSeconds,
            });
    }

    /// <summary>Fires one wave, waits for its first responses, samples allocation/GC around the full cycle (fire → responses → retire), then retires every session through the expiry path.</summary>
    private static async Task<WaveSample> RunWaveAsync(
        UdpProxyCoordinator coordinator,
        ChurnCountingSink sink,
        Socks5Server socksServer,
        FlowKey[] flowKeys,
        byte[] payload,
        long sequence,
        int wave,
        TimeSpan timeout)
    {
        sink.BeginWave();
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var (accepted, rejected, issueTimestamps, issueEnd) = await FireWaveAsync(coordinator, socksServer, flowKeys, payload, sequence).ConfigureAwait(false);
        var responseWatch = Stopwatch.StartNew();
        while (sink.FirstResponses < accepted && responseWatch.Elapsed < timeout)
        {
            await Task.Delay(s_responsePollInterval).ConfigureAwait(false);
        }

        var latencies = CollectFirstResponseLatencies(sink, issueTimestamps);
        var retireWatch = Stopwatch.StartNew();
        var removed = 0;
        while (coordinator.SessionCount > 0 && retireWatch.Elapsed < s_retireTimeout)
        {
            removed += await coordinator.RemoveExpiredAsync(TimeProvider.System.GetUtcNow(), TimeSpan.Zero).ConfigureAwait(false);
        }

        if (coordinator.SessionCount != 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"UDP churn wave {wave} left {coordinator.SessionCount} sessions live after {s_retireTimeout.TotalSeconds:0}s of expiry sweeps."));
        }

        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
        var firstResponses = latencies.Count;
        return new WaveSample(
            wave,
            accepted,
            rejected,
            firstResponses,
            accepted == 0 ? 0.0 : Math.Clamp(1.0 - (firstResponses / (double)accepted), 0.0, 1.0),
            latencies,
            StabilityShared.TicksToMilliseconds(issueEnd - issueTimestamps[0]),
            StabilityShared.TicksToMilliseconds(retireWatch.ElapsedTicks),
            removed,
            allocatedBytes,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before);
    }

    /// <summary>Fires each flow's first datagram back-to-back (the serialized pump shape), stamping each issue and counting admission.</summary>
    private static async Task<(long Accepted, long Rejected, long[] IssueTimestamps, long IssueEndTicks)> FireWaveAsync(
        UdpProxyCoordinator coordinator,
        Socks5Server socksServer,
        FlowKey[] flowKeys,
        byte[] payload,
        long sequence)
    {
        var issueTimestamps = new long[flowKeys.Length];
        long accepted = 0;
        long rejected = 0;
        for (var flow = 0; flow < flowKeys.Length; flow++)
        {
            issueTimestamps[flow] = Stopwatch.GetTimestamp();
            DatagramHeader.Write(payload, sequence, flow);
            if (await coordinator.TrySendSpanAsync(flowKeys[flow], socksServer, payload, default, CancellationToken.None).ConfigureAwait(false))
            {
                accepted++;
            }
            else
            {
                rejected++;
            }
        }

        return (accepted, rejected, issueTimestamps, Stopwatch.GetTimestamp());
    }

    private static List<double> CollectFirstResponseLatencies(ChurnCountingSink sink, long[] issueTimestamps)
    {
        var latencies = new List<double>(issueTimestamps.Length);
        for (var flow = 0; flow < issueTimestamps.Length; flow++)
        {
            if (sink.TryGetFirstResponseTicks(flow) is { } responseTicks)
            {
                latencies.Add(StabilityShared.TicksToMilliseconds(responseTicks - issueTimestamps[flow]));
            }
        }

        return latencies;
    }

    private static object BuildDistribution(List<double> samples)
    {
        if (samples.Count == 0) return new { min = 0.0, p50 = 0.0, p95 = 0.0, max = 0.0 };
        return new
        {
            min = samples.Min(),
            p50 = StabilityShared.Percentile(samples, 50),
            p95 = StabilityShared.Percentile(samples, 95),
            max = samples.Max(),
        };
    }

    /// <summary>One wave's measured cycle: admission counts, first-response latencies, retirement, and the allocation/GC deltas of the complete create→retire cycle.</summary>
    private sealed record WaveSample(
        int Wave,
        long Accepted,
        long Rejected,
        long FirstResponses,
        double EstablishmentLossRate,
        IReadOnlyList<double> FirstResponseMilliseconds,
        double TimeToIssueMs,
        double RetireMs,
        long Removed,
        long AllocatedBytes,
        long Gen0Collections,
        long Gen1Collections,
        long Gen2Collections)
    {
        public object BuildMetrics(int payloadBytes) => new
        {
            accepted = Accepted,
            rejected = Rejected,
            firstResponses = FirstResponses,
            establishmentLossRate = EstablishmentLossRate,
            firstResponseMs = LatencyDistribution.FromMilliseconds(FirstResponseMilliseconds),
            timeToIssueMs = TimeToIssueMs,
            retireMs = RetireMs,
            removed = Removed,
            allocatedBytes = AllocatedBytes,
            bytesPerSession = AllocatedBytes / (double)Math.Max(1, Accepted),
            gen0Collections = Gen0Collections,
            gen1Collections = Gen1Collections,
            gen2Collections = Gen2Collections,
            payloadBytes,
        };
    }

    /// <summary>
    /// The wave's first-response sink: records the first response per flow id for the current wave
    /// only. The payload's sequence is the wave number, so a straggler response from the previous
    /// wave (arriving after <see cref="BeginWave"/>) is ignored instead of being attributed to this
    /// wave's latency sample.
    /// </summary>
    private sealed class ChurnCountingSink(int flows) : IUdpResponseSink
    {
        private long[] _firstResponseTicks = new long[flows];
        private long[]? _spare;
        private long _expectedSequence;
        private long _firstResponses;

        public long FirstResponses => Interlocked.Read(ref _firstResponses);

        public void BeginWave()
        {
            // Swap in a cleared array instead of allocating one per wave (the previous array keeps
            // any straggler's CAS out of this wave's sample; the sequence guard rejects them anyway).
            var fresh = _spare ?? new long[flows];
            _spare = _firstResponseTicks;
            Array.Clear(fresh);
            _firstResponseTicks = fresh;
            Interlocked.Increment(ref _expectedSequence);
            Interlocked.Exchange(ref _firstResponses, 0);
        }

        /// <summary>The flow's first-response timestamp in the current wave, or null when no response was observed.</summary>
        public long? TryGetFirstResponseTicks(int flow)
        {
            var ticks = Volatile.Read(ref _firstResponseTicks[flow]);
            return ticks == 0 ? null : ticks;
        }

        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken)
        {
            // ReSharper disable DuplicatedSequentialIfBodies // Guard-clause chain: parsing, wave-sequence match and flow-index bounds are independent readiness preconditions and each rejection carries its own diagnostic value; merging them into one condition obscures which precondition rejected the response.
            if (!DatagramHeader.TryRead(payload.Span, out var sequence, out var flowId)) return ValueTask.CompletedTask;
            if (sequence != Interlocked.Read(ref _expectedSequence)) return ValueTask.CompletedTask;
            if ((uint)flowId >= (uint)_firstResponseTicks.Length) return ValueTask.CompletedTask;
            // ReSharper restore DuplicatedSequentialIfBodies
            if (Interlocked.CompareExchange(ref _firstResponseTicks[flowId], Stopwatch.GetTimestamp(), 0) == 0)
            {
                Interlocked.Increment(ref _firstResponses);
            }

            return ValueTask.CompletedTask;
        }
    }
}
