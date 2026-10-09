using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// UDP session churn at the design bounds: waves of
/// <c>--burst-flows</c> short-lived sessions through the real dial path, each wave retired through
/// the coordinator's own idle-expiry path (<see cref="UdpProxyCoordinator.RemoveExpiredAsync(DateTimeOffset, TimeSpan)"/> with
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
        // --socks5-external: the server and the receiver it relays into run in a child process, so
        // their per-connection buffers (64 KiB relay loop, 4 MiB relay socket) no longer land in
        // this process's GC.GetTotalAllocatedBytes readings.
        await using var externalServer = options.Socks5External
            ? await ExternalLoopbackSocks5UdpServer.StartAsync(flows, associateDelay, CancellationToken.None).ConfigureAwait(false)
            : null;
        await using var receiver = externalServer is null ? new EchoReceiver(flows) : null;
        await using var server = receiver is null ? null : new LoopbackSocks5UdpServer(receiver.Endpoint, associateDelay);
        // Hosted only for its own column, whose counters it is the evidence for, so a column that did
        // not dial it omits the field rather than reporting a zero nobody observed.
        await using var uotServer = options.Target == SoakTargetKind.Uot && receiver is not null
            ? new LoopbackSocks5UotServer(receiver.Endpoint, associateDelay)
            : null;
        // Hosted in every column, so all three rows carry the local hop's own counters: the SOCKS5
        // columns observe zero datagrams arriving on it, the local column observes the wave.
        await using var localResponder = new LoopbackLocalUdpResponder();
        var flowKeys = new FlowKey[flows];
        for (var index = 0; index < flowKeys.Length; index++) flowKeys[index] = BenchmarkShared.CreateFlowKey(index);
        var sink = new ChurnCountingSink(flows, flowKeys);
        // The warn-level census is the run's evidence that detection never flipped the server: its
        // events are rate-limited or one-shot, so it costs nothing in the measured wave windows.
        var productEvents = new CountingRuntimeLogger(includeVerbose: false);
        const int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;
        using var setupQueuePool = new NativeBufferPool(maximumFrameSize);
        using var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));
        using var setupExecutor = new SetupExecutor();
        var registry = new SelfTrafficRegistry();
        var coordinator = new UdpProxyCoordinator(
            CreateTransportFactory(options, registry, maximumFrameSize, productEvents),
            sink,
            setupQueuePool,
            receiveWindowPool,
            setupExecutor,
            new UdpProxyOptions { Capacity = flows, Logger = productEvents });
        try
        {
            var target = CreateTarget(server, uotServer, localResponder, externalServer);
            var timeout = ComputeWaveTimeout(flows, associateDelay);
            if (sustained)
            {
                await RunSustainedAsync(context, coordinator, sink, target, flowKeys, options, productEvents, timeout).ConfigureAwait(false);
            }
            else
            {
                await RunWavesAsync(context, coordinator, sink, target, flowKeys, options, productEvents, waveCount: waves, timeout).ConfigureAwait(false);
            }
        }
        finally
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The transport the column sends through: the product composite with both factories for a local
    /// target (<c>--target local</c> prices the wiring the CLI composes), the bare relay factory for
    /// the SOCKS5 columns — the UoT mode rides the target's own field, so it needs no composition of
    /// its own.
    /// </summary>
    private static IUdpProxyTransportFactory CreateTransportFactory(
        SoakOptions options,
        SelfTrafficRegistry registry,
        int maximumFrameSize,
        ILogger logger)
    {
        var socks5 = new Socks5UdpTransportFactory(registry, maximumFrameSize, logger: logger);
        return options.Target == SoakTargetKind.Local
            ? new UdpTransportFactory(socks5, new LocalUdpTransportFactory(registry, maximumFrameSize))
            : socks5;
    }

    /// <summary>
    /// The column's placements: the native server's target for the relay column, the UoT fixture's
    /// listener with the mode's field set for the uot column, and the responder's endpoint for the
    /// local column.
    /// </summary>
    private static ChurnTarget CreateTarget(
        LoopbackSocks5UdpServer? server,
        LoopbackSocks5UotServer? uotServer,
        LoopbackLocalUdpResponder localResponder,
        ExternalLoopbackSocks5UdpServer? externalServer)
    {
        var controlPort = checked((ushort)(externalServer?.ControlEndpoint.Port ?? server!.ControlEndpoint.Port));
        var socks5 = ProxyTarget.FromServer(new Socks5Server("churn", "127.0.0.1", controlPort, Username: null, Password: null));
        ProxyTarget? uot = uotServer is null
            ? null
            : ProxyTarget.FromServer(new Socks5Server("churn", "127.0.0.1", checked((ushort)uotServer.ControlEndpoint.Port), Username: null, Password: null, UdpOverTcp: true));
        return new ChurnTarget(socks5, uot, CreateLocalTarget(localResponder), server, uotServer, localResponder, externalServer);
    }

    /// <summary>
    /// The local column's target: the responder's own endpoint, carried as a <see cref="ProxyTarget"/>
    /// with no SOCKS5 server behind it — the shape the configuration loader produces for a
    /// <c>localTargets</c> entry, which is what the composite dispatches on.
    /// </summary>
    private static ProxyTarget CreateLocalTarget(LoopbackLocalUdpResponder responder)
    {
        var local = new LocalTarget("local", Endpoint.From(responder.Endpoint.Address, checked((ushort)responder.Endpoint.Port)));
        return new ProxyTarget(local.Name, Socks5: null, Local: local);
    }

    /// <summary>
    /// The wave window must outlive the serialized setup chain it measures: with an 8-wide limiter
    /// and a per-flow dial of <paramref name="associateDelay"/>, the last flow's first response
    /// needs roughly ceil(N/8) × delay; three times that plus the 30 s floor absorbs scheduling
    /// noise.
    /// </summary>
    private static TimeSpan ComputeWaveTimeout(int flows, TimeSpan associateDelay) =>
        TimeSpan.FromMilliseconds(Math.Max(
            TimeSpan.FromSeconds(30).TotalMilliseconds,
            Math.Ceiling(flows / (double)SetupLimiterWidth) * associateDelay.TotalMilliseconds * 3));

    private static async Task RunWavesAsync(
        StabilityContext context,
        UdpProxyCoordinator coordinator,
        ChurnCountingSink sink,
        ChurnTarget target,
        FlowKey[] flowKeys,
        SoakOptions options,
        CountingRuntimeLogger productEvents,
        int waveCount,
        TimeSpan timeout)
    {
        var payload = new byte[options.PayloadBytes];
        // The counters open before the unmeasured warmup wave, so each row's difference covers every
        // handshake the run made for these flows up to that row — warmup and earlier waves included.
        var observationStart = TransportObservation.Observe(target.Server, target.UotServer, target.Responder);
        var sequence = await WarmUpAsync(coordinator, sink, target, options, flowKeys, payload, timeout).ConfigureAwait(false);
        for (var wave = 0; wave < waveCount; wave++)
        {
            var sample = await RunWaveAsync(coordinator, sink, target, options, flowKeys, payload, ++sequence, wave, timeout).ConfigureAwait(false);
            context.WriteResult(
                "udp.churn",
                new { burstFlows = options.BurstFlows, dialDelayMs = options.DialDelayMs, mode = "waves", wave, waves = waveCount, target = options.Target },
                sample.BuildMetrics(payload.Length, productEvents, observationStart.Since(target.Server, target.UotServer, target.Responder)));
        }
    }

    /// <summary>
    /// Fires and retires two unmeasured waves for the uot column and one for every other: the first
    /// wave of a process pays first-call JIT/tiering, the executor's worker-thread creation, and
    /// socket-stack warmup, so it would otherwise dominate a short wave-mode row. The uot column
    /// needs the second because its fixture is a second in-process server implementation — a
    /// connection task and a reply loop per flow, which the native fixture's single relay loop does
    /// not have — and that cost lands in the column's first <em>measured</em> row when it is left in
    /// the warm-up's shadow. Returns the payload sequence the warm-up consumed.
    /// </summary>
    private static async Task<long> WarmUpAsync(
        UdpProxyCoordinator coordinator,
        ChurnCountingSink sink,
        ChurnTarget target,
        SoakOptions options,
        FlowKey[] flowKeys,
        byte[] payload,
        TimeSpan timeout)
    {
        var sequence = await RunWarmUpWaveAsync(coordinator, sink, target, options, flowKeys, payload, sequence: 1, timeout).ConfigureAwait(false);
        return target.UotServer is null
            ? sequence
            : await RunWarmUpWaveAsync(coordinator, sink, target, options, flowKeys, payload, sequence + 1, timeout).ConfigureAwait(false);
    }

    private static async Task<long> RunWarmUpWaveAsync(
        UdpProxyCoordinator coordinator,
        ChurnCountingSink sink,
        ChurnTarget target,
        SoakOptions options,
        FlowKey[] flowKeys,
        byte[] payload,
        long sequence,
        TimeSpan timeout)
    {
        _ = await RunWaveAsync(coordinator, sink, target, options, flowKeys, payload, sequence, wave: -1, timeout).ConfigureAwait(false);
        return sequence;
    }

    private static async Task RunSustainedAsync(
        StabilityContext context,
        UdpProxyCoordinator coordinator,
        ChurnCountingSink sink,
        ChurnTarget target,
        FlowKey[] flowKeys,
        SoakOptions options,
        CountingRuntimeLogger productEvents,
        TimeSpan timeout)
    {
        var payload = new byte[options.PayloadBytes];
        var observationStart = TransportObservation.Observe(target.Server, target.UotServer, target.Responder);
        var sequence = await WarmUpAsync(coordinator, sink, target, options, flowKeys, payload, timeout).ConfigureAwait(false);
        var window = TimeSpan.FromSeconds(options.DurationSeconds);
        var totalWatch = Stopwatch.StartNew();
        long sessions = 0;
        long accepted = 0;
        long rejected = 0;
        long waveCount = 0;
        long own = 0;
        long misdelivered = 0;
        long noResponse = 0;
        var perWaveBytesPerSession = new List<double>();
        var firstResponseLatencies = new List<double>();
        var (gen0Before, gen1Before, gen2Before) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        while (totalWatch.Elapsed < window && waveCount < MaximumSustainedWaves)
        {
            var sample = await RunWaveAsync(coordinator, sink, target, options, flowKeys, payload, ++sequence, wave: (int)(waveCount % uint.MaxValue), timeout).ConfigureAwait(false);
            waveCount++;
            sessions += flowKeys.Length;
            accepted += sample.Accepted;
            rejected += sample.Rejected;
            own += sample.Own;
            misdelivered += sample.Misdelivered;
            noResponse += sample.NoResponse;
            perWaveBytesPerSession.Add(sample.AllocatedBytes / (double)flowKeys.Length);
            firstResponseLatencies.AddRange(sample.FirstResponseMilliseconds);
        }

        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
        var elapsedSeconds = totalWatch.Elapsed.TotalSeconds;
        var totals = new SustainedSample(
            Waves: waveCount,
            Sessions: sessions,
            Accepted: accepted,
            Rejected: rejected,
            Own: own,
            Misdelivered: misdelivered,
            NoResponse: noResponse,
            AllocatedBytes: allocatedBytes,
            ElapsedSeconds: elapsedSeconds,
            Gen0Collections: GC.CollectionCount(0) - gen0Before,
            Gen1Collections: GC.CollectionCount(1) - gen1Before,
            Gen2Collections: GC.CollectionCount(2) - gen2Before,
            WaveBytesPerSession: perWaveBytesPerSession,
            FirstResponseLatencies: firstResponseLatencies);
        context.WriteResult(
            "udp.churn",
            new { burstFlows = options.BurstFlows, dialDelayMs = options.DialDelayMs, mode = "sustained", durationSeconds = options.DurationSeconds, target = options.Target },
            BuildSustainedMetrics(totals, observationStart.Since(target.Server, target.UotServer, target.Responder), productEvents));
    }

    /// <summary>The sustained window's aggregate row: the wave totals folded over the window, with the same per-flow accounting and counter blocks a wave row carries.</summary>
    private static object BuildSustainedMetrics(SustainedSample totals, TransportObservation observation, CountingRuntimeLogger productEvents) => new
    {
        waves = totals.Waves,
        sessions = totals.Sessions,
        accepted = totals.Accepted,
        rejected = totals.Rejected,
        own = totals.Own,
        misdelivered = totals.Misdelivered,
        noResponse = totals.NoResponse,
        establishmentLossRate = totals.Sessions == 0 ? 0.0 : Math.Clamp(1.0 - (totals.Accepted / (double)totals.Sessions), 0.0, 1.0),
        allocatedBytes = totals.AllocatedBytes,
        bytesPerSession = totals.Sessions == 0 ? 0.0 : totals.AllocatedBytes / (double)totals.Sessions,
        bytesPerSecond = totals.ElapsedSeconds <= 0 ? 0.0 : totals.AllocatedBytes / totals.ElapsedSeconds,
        achievedSessionsPerSecond = totals.ElapsedSeconds <= 0 ? 0.0 : totals.Sessions / totals.ElapsedSeconds,
        gen0Collections = totals.Gen0Collections,
        gen1Collections = totals.Gen1Collections,
        gen2Collections = totals.Gen2Collections,
        waveBytesPerSession = BuildDistribution(totals.WaveBytesPerSession),
        firstResponseMs = LatencyDistribution.FromMilliseconds(totals.FirstResponseLatencies),
        wallSeconds = totals.ElapsedSeconds,
        socks5Handshakes = observation.Socks5Handshakes,
        uotHandshakes = observation.UotHandshakes,
        localResponder = observation.LocalResponder,
        productEvents = StabilityShared.BuildProductEvents(productEvents),
    };

    /// <summary>Fires one wave, waits for its first responses, samples allocation/GC around the full cycle (fire → responses → retire), then retires every session through the expiry path.</summary>
    private static async Task<WaveSample> RunWaveAsync(
        UdpProxyCoordinator coordinator,
        ChurnCountingSink sink,
        ChurnTarget target,
        SoakOptions options,
        FlowKey[] flowKeys,
        byte[] payload,
        long sequence,
        int wave,
        TimeSpan timeout)
    {
        // A child that died between waves would otherwise turn every remaining wave into a
        // zero-acceptance row instead of failing the instrument.
        target.ExternalServer?.ThrowIfExited();
        sink.BeginWave();
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var (accepted, rejected, issueTimestamps, issueEnd) = await FireWaveAsync(coordinator, target.For(options), flowKeys, payload, sequence).ConfigureAwait(false);
        var responseWatch = Stopwatch.StartNew();
        while (sink.FirstResponses < accepted && responseWatch.Elapsed < timeout)
        {
            target.ExternalServer?.ThrowIfExited();
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
        var own = latencies.Count;
        var misdelivered = sink.Misdelivered;
        return new WaveSample(
            wave,
            accepted,
            rejected,
            own,
            misdelivered,
            Math.Max(0, flowKeys.Length - own - misdelivered),
            accepted == 0 ? 0.0 : Math.Clamp(1.0 - (own / (double)accepted), 0.0, 1.0),
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
        ProxyTarget target,
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
            if (await coordinator.TrySendSpanAsync(flowKeys[flow], target, payload, default, CancellationToken.None).ConfigureAwait(false))
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

    private static object BuildDistribution(IReadOnlyList<double> samples)
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

    /// <summary>
    /// The run's placements and the loopback servers behind them: the SOCKS5 server handle the
    /// relayed column dials, the UoT server the uot column dials with the mode set on its target, the
    /// local target the local column sends to, and the server instances whose own counters become the
    /// row's handshake evidence. The SOCKS5 server and the local responder stay up for every column,
    /// so each row can show what the other transports did with the same flows; the UoT fixture is
    /// hosted only by its own column, so its field is omitted elsewhere. The child's death must fail
    /// the wave rather than let the response wait time out into a silent zero-response row.
    /// </summary>
    private sealed record ChurnTarget(
        ProxyTarget Socks5,
        ProxyTarget? Uot,
        ProxyTarget Local,
        LoopbackSocks5UdpServer? Server,
        LoopbackSocks5UotServer? UotServer,
        LoopbackLocalUdpResponder Responder,
        ExternalLoopbackSocks5UdpServer? ExternalServer)
    {
        public ProxyTarget For(SoakOptions options) => options.Target switch
        {
            SoakTargetKind.Local => Local,
            SoakTargetKind.Uot => Uot ?? throw new InvalidOperationException("The uot column has no UoT target; the fixture was not started."),
            _ => Socks5,
        };
    }

    /// <summary>
    /// The sustained window's totals: the per-wave quantities folded over the whole window, plus the
    /// allocation and GC deltas of the window. <see cref="Own"/>, <see cref="Misdelivered"/> and
    /// <see cref="NoResponse"/> partition the window's flows exactly as the wave record's do.
    /// </summary>
    private sealed record SustainedSample(
        long Waves,
        long Sessions,
        long Accepted,
        long Rejected,
        long Own,
        long Misdelivered,
        long NoResponse,
        long AllocatedBytes,
        double ElapsedSeconds,
        long Gen0Collections,
        long Gen1Collections,
        long Gen2Collections,
        IReadOnlyList<double> WaveBytesPerSession,
        IReadOnlyList<double> FirstResponseLatencies);

    /// <summary>
    /// One wave's measured cycle: admission counts, first-response latencies, retirement, and the
    /// allocation/GC deltas of the complete create→retire cycle. <see cref="Own"/>,
    /// <see cref="Misdelivered"/> and <see cref="NoResponse"/> partition the wave's flows, so the row
    /// shows whether the wave went unanswered rather than only how many replies were injected.
    /// </summary>
    private sealed record WaveSample(
        int Wave,
        long Accepted,
        long Rejected,
        long Own,
        long Misdelivered,
        long NoResponse,
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
        public object BuildMetrics(int payloadBytes, CountingRuntimeLogger productEvents, TransportObservation observation) => new
        {
            accepted = Accepted,
            rejected = Rejected,
            firstResponses = Own,
            misdelivered = Misdelivered,
            noResponse = NoResponse,
            establishmentLossRate = EstablishmentLossRate,
            firstResponseMs = LatencyDistribution.FromMilliseconds(FirstResponseMilliseconds),
            socks5Handshakes = observation.Socks5Handshakes,
            uotHandshakes = observation.UotHandshakes,
            localResponder = observation.LocalResponder,
            timeToIssueMs = TimeToIssueMs,
            retireMs = RetireMs,
            removed = Removed,
            allocatedBytes = AllocatedBytes,
            bytesPerSession = AllocatedBytes / (double)Math.Max(1, Accepted),
            gen0Collections = Gen0Collections,
            gen1Collections = Gen1Collections,
            gen2Collections = Gen2Collections,
            payloadBytes,
            productEvents = StabilityShared.BuildProductEvents(productEvents),
        };
    }
}
