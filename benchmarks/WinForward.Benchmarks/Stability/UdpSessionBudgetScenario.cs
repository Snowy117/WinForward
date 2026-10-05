using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The UDP session-budget soak (PRD acceptance 1, design §6/R4): a sustained new-flow churn at
/// <c>--rate</c> flows/s for <c>--churn-seconds</c>, then a <c>--drain-seconds</c> window with no new
/// flows, with the coordinator's idle expiry driven on the production sweeper cadence for the
/// validated 30 s UDP idle retention. Every ~5 s one row reports the live sessions, the proxy's own
/// descriptors, the estimated kernel receive buffer, the pool's associations/leases, the
/// accepted/rejected/expired counters, the datagram loss, and the per-session descriptor/byte ratios;
/// the final row is the run's verdict.
/// <para>
/// The assertions are the acceptance: the churn window loses no datagram, is refused nowhere, and
/// keeps the live sessions strictly below <c>--capacity</c>; the steady-state population stays under
/// the retention ceiling <c>rate × (idle + 2 × sweep) + margin</c>, so retention bounds the footprint
/// instead of the cumulative flow count; the estimated kernel receive buffer stays inside the same
/// ceiling in bytes; the descriptor delta stays inside the per-session budget (one relay socket per
/// live session plus one control connection per association); and the drain returns to zero sessions,
/// zero leases, zero associations, and the pre-churn descriptor baseline. While the population fits
/// the pool's shared budget, the run additionally requires the association count to follow the shared
/// fan-out — the direct evidence that control connections are reused — and beyond it reports the
/// saturation the pool's per-flow fallback produces instead.
/// </para>
/// <para>
/// A churn window whose cumulative flow count does not exceed the ceiling cannot discriminate
/// retention from accumulation, so the scenario refuses it before the load instead of recording it as
/// evidence (<see cref="SessionBudgetMath.NonDiscriminatingChurnWindow"/>); <c>--require-pooling</c>
/// turns the same refusal on the pooling half, which a saturated population would otherwise skip.
/// </para>
/// <para>
/// The run mirrors two production wiring points rather than re-deriving them: the sweep interval comes
/// from <see cref="IdleExpirySweeper.DeriveUdpSweepInterval"/> over the effective retention floor, and
/// the coordinator's activity clock is ticked once per driven iteration (the run's
/// <c>TickActivityClock</c>), the cadence the capture pump's <c>FlushPendingInjections</c> gives it in
/// production. The second mirror is load-bearing: a soak with a frozen clock stamps every session at the
/// coordinator's construction bucket, so the first sweep past the retention mass-retires the whole
/// population and the run measures a sawtooth artifact instead of the retention its verdicts name.
/// </para>
/// <para>
/// The descriptor series describes this process: with the in-process harness SOCKS5 server, the two
/// sockets it owns per accepted control connection are subtracted
/// (<see cref="ProcessResourceSampler"/>); <c>--socks5-external</c> moves them into a child process.
/// </para>
/// </summary>
internal static class UdpSessionBudgetScenario
{
    /// <summary>
    /// Opt-in switch for the verbose product-event census, mirroring <see cref="UdpLossScenario"/>'s
    /// pattern: the row always carries the warn-level census, whose events are rate-limited or
    /// one-shot and cost nothing on the datagram paths; flipping this adds the per-datagram
    /// trace/debug counts, which allocate and slow the send/receive paths, so it is reserved for a
    /// loss-localization session.
    /// </summary>
    private const bool CaptureProductEvents = false;

    private static readonly TimeSpan s_mainSweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>One key per flow id, in id order: the run sends with these and the sink resolves a reply's sender by indexing them.</summary>
    private static FlowKey[] CreateFlowKeys(int flowCount)
    {
        var keys = new FlowKey[flowCount];
        for (var index = 0; index < keys.Length; index++) keys[index] = BenchmarkShared.CreateFlowKey(index);
        return keys;
    }

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        var idleTimeout = ConfigurationLoader.DefaultUdpSessionIdleTimeout;
        var oneShotIdleTimeout = UdpProxyCoordinator.OneShotIdleTimeout;
        // The production UDP cadence for the effective retention floor — the shorter of the configured
        // retention and the one-shot class — taken from the sweeper itself instead of re-derived here:
        // max(5 s, floor / 2), capped by the 60 s main-leg interval (5 s for the default 30 s retention
        // and the 5 s one-shot class, was 15 s under uniform retention).
        var sweepInterval = IdleExpirySweeper.DeriveUdpSweepInterval(s_mainSweepInterval, IdleExpirySweeper.EffectiveUdpRetentionFloor(idleTimeout, oneShotIdleTimeout), udpSweepInterval: null);
        ValidateChurnWindow(options, idleTimeout, sweepInterval);
        // Warm-up flows: one shared association's worth at the configured fan-out bound, without
        // dominating a short churn window.
        var warmupFlows = Math.Min(options.Rate, ConfigurationLoader.DefaultUdpAssociationFlowsPerAssociation);
        var churnFlows = checked(options.Rate * options.ChurnSeconds);
        var flowCapacity = checked(warmupFlows + churnFlows);

        await using var externalServer = options.Socks5External
            ? await ExternalLoopbackSocks5UdpServer.StartAsync(flowCapacity, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false)
            : null;
        await using var receiver = externalServer is null ? new EchoReceiver(flowCapacity) : null;
        await using var server = receiver is null ? null : new LoopbackSocks5UdpServer(receiver.Endpoint);
        var flowKeys = CreateFlowKeys(flowCapacity);
        var sink = new SessionBudgetSink(churnOffset: warmupFlows, flowCapacity: flowCapacity, flowKeys: flowKeys);
        var productEvents = new CountingRuntimeLogger(CaptureProductEvents);
        using var sampler = new ProcessResourceSampler(server);
        const int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;
        using var setupQueuePool = new NativeBufferPool(maximumFrameSize);
        using var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));
        using var setupExecutor = new SetupExecutor();
        var registry = new SelfTrafficRegistry();
        // The census logger goes to the pool too, because the fallback warn is a pool event.
        await using var associations = new UdpAssociationPool(registry, options.ReuseMode, logger: productEvents);
        var coordinator = new UdpProxyCoordinator(
            new Socks5UdpTransportFactory(associations, registry, maximumFrameSize),
            sink,
            setupQueuePool,
            receiveWindowPool,
            setupExecutor,
            new UdpProxyOptions { Capacity = options.Capacity, Logger = productEvents });
        var run = new UdpSessionBudgetRun(context, options, coordinator, associations, sink, sampler, productEvents, flowKeys, idleTimeout, oneShotIdleTimeout, sweepInterval, warmupFlows, churnFlows);
        try
        {
            var controlPort = checked((ushort)(externalServer?.ControlEndpoint.Port ?? server!.ControlEndpoint.Port));
            var socksServer = new Socks5Server("session-budget", "127.0.0.1", controlPort, Username: null, Password: null);
            await run.WarmUpAsync(socksServer).ConfigureAwait(false);
            sink.BeginChurn();
            await run.ChurnAsync(socksServer).ConfigureAwait(false);
            await run.DrainAsync().ConfigureAwait(false);
        }
        finally
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
        }

        run.WriteSummary();
    }

    /// <summary>
    /// Refuses a churn window the retention ceiling cannot discriminate at, before the load rather
    /// than after: <c>rate × churnSeconds</c> must exceed the ceiling, otherwise a run that never
    /// expires a session ends inside the bound and the retention assertion would claim nothing. The
    /// sentence is the acceptance's own (<see cref="SessionBudgetMath.NonDiscriminatingChurnWindow"/>),
    /// so the fast refusal and the recorded verdict cannot drift apart. Internal so the shape check is
    /// unit-testable without running the soak.
    /// </summary>
    internal static void ValidateChurnWindow(SoakOptions options, TimeSpan idleTimeout, TimeSpan sweepInterval)
    {
        var margin = SessionBudgetMath.SteadyStateMargin(options.Rate);
        var ceiling = SessionBudgetMath.SteadyStateSessionCeiling(options.Rate, idleTimeout, sweepInterval, margin);
        if (SessionBudgetMath.NonDiscriminatingChurnWindow(options.Rate, options.ChurnSeconds, ceiling) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }
    }
}
