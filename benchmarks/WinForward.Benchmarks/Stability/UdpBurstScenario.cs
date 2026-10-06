using System.Diagnostics;
using Microsoft.Extensions.Logging;
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
    private static readonly TimeSpan s_controlWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_postWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_drainTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_warmupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_warmupPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan s_burstPollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan s_minimumBurstWindow = TimeSpan.FromSeconds(30);
    private const int SetupLimiterWidth = 8;

    /// <summary>
    /// Opt-in switch for the verbose product-event census, mirroring <see cref="UdpLossScenario"/>'s
    /// pattern: the row always carries the warn-level census, whose events are rate-limited or
    /// one-shot and cost nothing on the datagram paths; flipping this adds the per-datagram
    /// trace/debug counts, which allocate and slow the send/receive paths, so it is reserved for a
    /// loss-localization session.
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
        // Hosted only for its own column: the UoT fixture's counters are that column's evidence, so a
        // column that did not dial it omits the field rather than reporting a zero nobody observed.
        await using var uotServer = options.Target == SoakTargetKind.Uot
            ? new LoopbackSocks5UotServer(receiver.Endpoint, associateDelay)
            : null;
        // Hosted in every column, so the rows of all three carry the local hop's own counters: the
        // SOCKS5 columns observe zero datagrams arriving on it, the local column observes the run.
        await using var localResponder = new LoopbackLocalUdpResponder();
        // The counters open before the background flows establish, so the row's totals cover every
        // handshake this run caused — the background warmup included, not the burst window alone.
        var handshakesBefore = Socks5HandshakeCounters.Snapshot(server);
        var responderBefore = LocalResponderCounters.Snapshot(localResponder);
        var uotBefore = UotHandshakeCounters.Snapshot(uotServer);
        // One key array covering both flow-id ranges, so the sink can resolve a reply's sender from
        // the payload's flow id alone.
        var flowKeys = CreateFlowKeys(0, backgroundFlows + burstFlows);
        var backgroundKeys = flowKeys[..backgroundFlows];
        var burstKeys = flowKeys[backgroundFlows..];
        var tracker = new InFlightTracker(Math.Max(1024, options.Pps * 12));
        var sink = new BurstCountingSink(backgroundFlows, burstFlows, tracker, flowKeys);
        var productEvents = new CountingRuntimeLogger(CaptureProductEvents);
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
            new UdpProxyOptions { Capacity = backgroundFlows + burstFlows, Logger = productEvents });
        PhaseOutcome outcome;
        try
        {
            var target = ResolveTarget(options, server, uotServer, localResponder);
            outcome = await RunPhasesAsync(coordinator, target, backgroundKeys, burstKeys, sink, tracker, options, burstTimeout)
                .ConfigureAwait(false);
        }
        finally
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
        }

        var socks5Handshakes = Socks5HandshakeCounters.Delta(Socks5HandshakeCounters.Snapshot(server), handshakesBefore);
        var responderCounters = LocalResponderCounters.Delta(LocalResponderCounters.Snapshot(localResponder), responderBefore);
        var uotHandshakes = UotHandshakeCounters.Delta(UotHandshakeCounters.Snapshot(uotServer), uotBefore);
        context.WriteResult("udp.burstEstablishment", BuildParameters(options), BuildMetrics(outcome, sink, options, productEvents, socks5Handshakes, uotHandshakes, responderCounters));
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
    /// The column's placement: the native server's target for the relay column, the UoT fixture's
    /// listener with the mode's field set for the uot column, and the responder's endpoint for the
    /// local column.
    /// </summary>
    private static ProxyTarget ResolveTarget(
        SoakOptions options,
        LoopbackSocks5UdpServer server,
        LoopbackSocks5UotServer? uotServer,
        LoopbackLocalUdpResponder localResponder) => options.Target switch
        {
            SoakTargetKind.Local => CreateLocalTarget(localResponder),
            SoakTargetKind.Uot => CreateUotTarget(uotServer),
            _ => CreateSocks5Target(server),
        };

    /// <summary>
    /// The relay column's target: the loopback SOCKS5 server's listener with the native UDP carriage
    /// the shipped default measures.
    /// </summary>
    private static ProxyTarget CreateSocks5Target(LoopbackSocks5UdpServer server) =>
        ProxyTarget.FromServer(new Socks5Server("soak", "127.0.0.1", checked((ushort)server.ControlEndpoint.Port), Username: null, Password: null));

    /// <summary>
    /// The uot column's target: the UoT fixture's listener with the mode's field set, which is what
    /// routes the flow through <c>Socks5UotTransport</c> instead of the native carriage.
    /// </summary>
    private static ProxyTarget CreateUotTarget(LoopbackSocks5UotServer? uotServer)
    {
        var uot = uotServer ?? throw new InvalidOperationException("The uot column has no UoT target; the fixture was not started.");
        return ProxyTarget.FromServer(new Socks5Server("soak", "127.0.0.1", checked((ushort)uot.ControlEndpoint.Port), Username: null, Password: null, UdpOverTcp: true));
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

    private static object BuildParameters(SoakOptions options) => new
    {
        burstFlows = options.BurstFlows,
        dialDelayMs = options.DialDelayMs,
        backgroundFlows = options.Flows,
        backgroundPps = options.Pps,
        payloadBytes = options.PayloadBytes,
        seed = options.Seed,
        target = options.Target,
    };

    /// <summary>
    /// The burst window must outlive the serialized setup chain it measures: with a
    /// <see cref="SetupLimiterWidth"/>-wide setup concurrency and a per-flow dial of
    /// <paramref name="associateDelay"/>, the last flow's first response needs roughly
    /// ceil(N/8) × delay; three times that plus the 30 s floor absorbs scheduling noise.
    /// </summary>
    private static TimeSpan ComputeBurstTimeout(int burstFlows, TimeSpan associateDelay) =>
        TimeSpan.FromMilliseconds(Math.Max(
            s_minimumBurstWindow.TotalMilliseconds,
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
        ProxyTarget target,
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
            _ = await coordinator.TrySendSpanAsync(backgroundKeys[flow], target, payload, default, CancellationToken.None).ConfigureAwait(false);
        }

        // A flow counts as established once its warmup response has returned through the
        // sink, so no warmup echo can straddle the attribution boundary below.
        var warmupWatch = Stopwatch.StartNew();
        while (sink.WarmupResponses < backgroundKeys.Length && warmupWatch.Elapsed < s_warmupTimeout)
        {
            await Task.Delay(s_warmupPollInterval).ConfigureAwait(false);
        }

        tracker.BeginAttribution();
        var windowTicks = new long[4];
        windowTicks[0] = Stopwatch.GetTimestamp();
        using var senderCancellation = new CancellationTokenSource();
        var sender = new BackgroundSender(coordinator, target, backgroundKeys, options.PayloadBytes, options.Pps, tracker);
        // ReSharper disable once AccessToDisposedClosure // The sender loop is cancelled and awaited (CancelAsync + await senderTask) inside the using scope, so the token source is disposed only after the loop has returned.
        var senderTask = Task.Run(() => sender.RunLoopAsync(senderCancellation.Token), senderCancellation.Token);

        await Task.Delay(s_controlWindow, senderCancellation.Token).ConfigureAwait(false);
        windowTicks[1] = Stopwatch.GetTimestamp();
        sender.EnterWindow(BackgroundWindow.Burst);
        var burst = await FireBurstAsync(coordinator, target, burstKeys, backgroundKeys.Length, options.PayloadBytes, burstTimeout, sink).ConfigureAwait(false);
        windowTicks[2] = Stopwatch.GetTimestamp();
        sender.EnterWindow(BackgroundWindow.Post);
        await Task.Delay(s_postWindow, senderCancellation.Token).ConfigureAwait(false);
        windowTicks[3] = Stopwatch.GetTimestamp();
        await senderCancellation.CancelAsync().ConfigureAwait(false);
        await senderTask.ConfigureAwait(false);
        await Task.Delay(s_drainTime, CancellationToken.None).ConfigureAwait(false);
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
        ProxyTarget target,
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
            if (await coordinator.TrySendSpanAsync(burstKeys[flow], target, payload, default, CancellationToken.None).ConfigureAwait(false))
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
            await Task.Delay(s_burstPollInterval).ConfigureAwait(false);
        }

        var latencies = new List<double>(burstKeys.Length);
        for (var flow = 0; flow < burstKeys.Length; flow++)
        {
            if (sink.TryGetBurstFirstResponseTicks(flow) is { } responseTicks)
            {
                latencies.Add(StabilityShared.TicksToMilliseconds(responseTicks - issueTimestamps[flow]));
            }
        }

        var firstResponses = (long)latencies.Count;
        var lossRate = accepted == 0 ? 0.0 : Math.Clamp(1.0 - (firstResponses / (double)accepted), 0.0, 1.0);
        return new BurstResult(
            accepted,
            rejected,
            firstResponses,
            lossRate,
            LatencyDistribution.FromMilliseconds(latencies),
            StabilityShared.TicksToMilliseconds(issueEndTicks - issueTimestamps[0]));
    }

    private static object BuildMetrics(
        PhaseOutcome outcome,
        BurstCountingSink sink,
        SoakOptions options,
        CountingRuntimeLogger productEvents,
        Socks5HandshakeCounters? socks5Handshakes,
        UotHandshakeCounters? uotHandshakes,
        LocalResponderCounters localResponder)
    {
        var burst = outcome.Burst;
        var sender = outcome.Sender;
        var ticks = outcome.WindowTicks;
        // The burst population's per-flow accounting: a flow is own when its own echo came back,
        // misdelivered when its echo arrived on a sibling instead, and noResponse when no echo for it
        // was observed at all.
        var misdelivered = sink.MisdeliveredBurstFlows;
        return new
        {
            burstAccepted = burst.BurstAccepted,
            burstRejected = burst.BurstRejected,
            firstResponses = burst.FirstResponses,
            misdelivered,
            noResponse = Math.Max(0, options.BurstFlows - burst.FirstResponses - misdelivered),
            establishmentLossRate = burst.EstablishmentLossRate,
            firstResponseMs = burst.FirstResponseMs,
            timeToFirstMs = burst.FirstResponseMs.Min,
            timeToLastMs = burst.FirstResponseMs.Max,
            timeToIssueMs = burst.TimeToIssueMs,
            socks5Handshakes,
            uotHandshakes,
            localResponder,
            background = new
            {
                control = BuildWindowMetrics(sender.SentIn(BackgroundWindow.Control), sink.InjectedIn(BackgroundWindow.Control), sender.SendLatenciesIn(BackgroundWindow.Control), StabilityShared.TicksToSeconds(ticks[1] - ticks[0])),
                burst = BuildWindowMetrics(sender.SentIn(BackgroundWindow.Burst), sink.InjectedIn(BackgroundWindow.Burst), sender.SendLatenciesIn(BackgroundWindow.Burst), StabilityShared.TicksToSeconds(ticks[2] - ticks[1])),
                post = BuildWindowMetrics(sender.SentIn(BackgroundWindow.Post), sink.InjectedIn(BackgroundWindow.Post), sender.SendLatenciesIn(BackgroundWindow.Post), StabilityShared.TicksToSeconds(ticks[3] - ticks[2])),
                misdeliveredFlows = sink.MisdeliveredBackgroundFlows,
            },
            unattributedResponses = sink.UnattributedResponses,
            productEvents = StabilityShared.BuildProductEvents(productEvents),
        };
    }
    private static object BuildWindowMetrics(long sent, long injected, IReadOnlyList<double> sendLatencies, double windowSeconds) => new
    {
        sent,
        injected,
        lossRate = sent == 0 ? 0.0 : Math.Clamp(1.0 - (injected / (double)sent), 0.0, 1.0),
        sendP95Ms = StabilityShared.Percentile(sendLatencies, 95),
        sendMaxMs = sendLatencies.Count == 0 ? 0.0 : sendLatencies.Max(),
        achievedPps = windowSeconds > 0.0 ? sent / windowSeconds : 0.0,
    };
}
