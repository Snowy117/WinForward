using System.Diagnostics;
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
        // ReSharper disable HeuristicUnreachableCode, CSharpWarnings::CS0162
        // The documented opt-in census switch above is a compile-time constant so a default run
        // compiles the diagnostic logger out entirely; the false branch "unreachable" code is the
        // switch's enabled state, flipped by editing the constant for a loss-localization session.
        var productEvents = CaptureProductEvents ? new CountingRuntimeLogger() : null;
        // ReSharper restore HeuristicUnreachableCode, CSharpWarnings::CS0162
        var coordinator = new UdpProxyCoordinator(
            new Socks5UdpTransportFactory(new SelfTrafficRegistry(), UdpFrameBuilder.DefaultMaximumEthernetFrame),
            sink,
            new UdpProxyOptions
            {
                Capacity = backgroundFlows + burstFlows,
                Logger = (IRuntimeLogger?)productEvents ?? NullRuntimeLogger.Instance,
            });
        PhaseOutcome outcome;
        try
        {
            var socksServer = new Socks5Server("soak", "127.0.0.1", checked((ushort)server.ControlEndpoint.Port), Username: null, Password: null);
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
            _ = await coordinator.TrySendSpanAsync(backgroundKeys[flow], socksServer, payload, default, CancellationToken.None).ConfigureAwait(false);
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
        var sender = new BackgroundSender(coordinator, socksServer, backgroundKeys, options.PayloadBytes, options.Pps, tracker);
        // ReSharper disable once AccessToDisposedClosure // The sender loop is cancelled and awaited (CancelAsync + await senderTask) inside the using scope, so the token source is disposed only after the loop has returned.
        var senderTask = Task.Run(() => sender.RunLoopAsync(senderCancellation.Token), senderCancellation.Token);

        await Task.Delay(s_controlWindow, senderCancellation.Token).ConfigureAwait(false);
        windowTicks[1] = Stopwatch.GetTimestamp();
        sender.EnterWindow(BackgroundWindow.Burst);
        var burst = await FireBurstAsync(coordinator, socksServer, burstKeys, backgroundKeys.Length, options.PayloadBytes, burstTimeout, sink).ConfigureAwait(false);
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
            if (await coordinator.TrySendSpanAsync(burstKeys[flow], socksServer, payload, default, CancellationToken.None).ConfigureAwait(false))
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
                control = BuildWindowMetrics(sender.SentIn(BackgroundWindow.Control), sink.InjectedIn(BackgroundWindow.Control), sender.SendLatenciesIn(BackgroundWindow.Control), StabilityShared.TicksToSeconds(ticks[1] - ticks[0])),
                burst = BuildWindowMetrics(sender.SentIn(BackgroundWindow.Burst), sink.InjectedIn(BackgroundWindow.Burst), sender.SendLatenciesIn(BackgroundWindow.Burst), StabilityShared.TicksToSeconds(ticks[2] - ticks[1])),
                post = BuildWindowMetrics(sender.SentIn(BackgroundWindow.Post), sink.InjectedIn(BackgroundWindow.Post), sender.SendLatenciesIn(BackgroundWindow.Post), StabilityShared.TicksToSeconds(ticks[3] - ticks[2])),
            },
            unattributedResponses = sink.UnattributedResponses,
            productEvents = productEvents is not null ? StabilityShared.BuildProductEvents(productEvents) : null,
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
