using System.Diagnostics;
using System.Globalization;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// One run's state: the churn counters, the sweeper schedule, the sampled series, and the verdict.
/// The churn counters are written only by the churn loop (the response sink owns the
/// cross-thread counters), so plain fields are enough; the sample list is read only after the
/// load phases have returned. The assertions live in <see cref="SessionBudgetAcceptance"/>: this
/// type drives the phases and writes the rows, that one owns the thresholds.
/// </summary>
internal sealed class UdpSessionBudgetRun(
    StabilityContext context,
    SoakOptions options,
    UdpProxyCoordinator coordinator,
    SessionBudgetSink sink,
    ProcessResourceSampler sampler,
    CountingRuntimeLogger productEvents,
    FlowKey[] flowKeys,
    LoopbackSocks5UotServer? uotServer,
    TimeSpan idleTimeout,
    TimeSpan oneShotIdleTimeout,
    TimeSpan sweepInterval,
    int warmupFlows,
    int churnFlows)
{
    private static readonly TimeSpan s_sampleInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_warmupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_warmupPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan s_retireTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_settleTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_drainPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<SessionBudgetSample> _samples = [];
    private readonly long[] _issueTicks = new long[churnFlows];
    private readonly object _parameters = new
    {
        rate = options.Rate,
        capacity = options.Capacity,
        churnSeconds = options.ChurnSeconds,
        drainSeconds = options.DrainSeconds,
        payloadBytes = options.PayloadBytes,
        seed = options.Seed,
        socks5External = options.Socks5External,
        idleTimeoutSeconds = idleTimeout.TotalSeconds,
        oneShotIdleTimeoutSeconds = oneShotIdleTimeout.TotalSeconds,
        sweepIntervalSeconds = sweepInterval.TotalSeconds,
    };

    private TimeSpan _nextSweep;
    private long _accepted;
    private long _rejected;
    private long _expired;
    private long _capacityRejectionsBefore;
    private long _setupRejectionsBefore;
    private long _setupFailuresBefore;
    private long _baselineFileDescriptors;
    private long _baselineManagedBytes;
    private int _churnPeakSessions;
    private int _steadyPeakSessions;
    private double? _sessionsZeroAtSeconds;
    private SessionBudgetSample? _final;

    /// <summary>
    /// Fires the warm-up flows, waits for their echoes, and retires them through the same expiry
    /// path the sweeper drives, so the churn window opens on a settled population and the descriptor
    /// and managed-memory baselines are the post-JIT, post-dial shape rather than a cold process.
    /// </summary>
    public async Task WarmUpAsync(Socks5Server socksServer)
    {
        _nextSweep = sweepInterval;
        var payload = new byte[options.PayloadBytes];
        for (var flow = 0; flow < warmupFlows; flow++)
        {
            TickActivityClock();
            DatagramHeader.Write(payload, flow + 1, flow);
            _ = await coordinator.TrySendSpanAsync(flowKeys[flow], ProxyTarget.FromServer(socksServer), payload, default, CancellationToken.None).ConfigureAwait(false);
        }

        var watch = Stopwatch.StartNew();
        while (sink.WarmupFirstResponses < warmupFlows && watch.Elapsed < s_warmupTimeout)
        {
            TickActivityClock();
            await DelayAsync(s_warmupPollInterval).ConfigureAwait(false);
        }

        // Every warm-up flow answering is the per-flow-association shape, and anything short of that
        // means the instrument is not delivering: each flow owns its association, so its echo can
        // only come back to it. Zero own echoes means nothing is being delivered at all, and the
        // churn measurement would be meaningless.
        if (sink.WarmupFirstResponses < warmupFlows)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Only {sink.WarmupFirstResponses} of {warmupFlows} warm-up flows saw their own echo within {s_warmupTimeout.TotalSeconds:0} s; the proxy path or the harness server is not delivering, so the churn measurement would be meaningless."));
        }

        var retireWatch = Stopwatch.StartNew();
        while (coordinator.SessionCount > 0 && retireWatch.Elapsed < s_retireTimeout)
        {
            TickActivityClock();
            _expired += await coordinator.RemoveExpiredAsync(TimeProvider.System.GetUtcNow(), TimeSpan.Zero).ConfigureAwait(false);
        }

        if (coordinator.SessionCount != 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The warm-up left {coordinator.SessionCount} sessions live after {s_retireTimeout.TotalSeconds:0} s of expiry sweeps."));
        }

        var baseline = sampler.Sample();
        _baselineFileDescriptors = baseline.ProxyFileDescriptors;
        _baselineManagedBytes = baseline.ManagedBytes;
        _capacityRejectionsBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpCapacityRejections);
        _setupRejectionsBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpSetupRejections);
        _setupFailuresBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpSetupFailures);
        _ = Sample("warmup");
    }

    /// <summary>
    /// The churn window: one new flow every 1 / <c>--rate</c> seconds, each sending exactly one
    /// datagram whose echo is the flow's establishment proof. Arrivals are paced against an absolute
    /// schedule anchored at the churn window's own start, so a sweep that overran its slot is caught
    /// up immediately instead of shifting the realized rate, and the warm-up duration is never
    /// emitted as a burst of catch-up arrivals.
    /// </summary>
    public async Task ChurnAsync(Socks5Server socksServer)
    {
        var payload = new byte[options.PayloadBytes];
        var arrivals = new SessionBudgetArrivals(_watch.Elapsed, TimeSpan.FromSeconds(1.0 / options.Rate));
        var steadyThreshold = (idleTimeout + sweepInterval).TotalSeconds;
        var nextSample = s_sampleInterval;
        for (var flow = 0; flow < churnFlows; flow++)
        {
            // The pump's per-iteration tick, before this arrival: the new session's first stamp and the
            // cutoff its sweep compares are then the same instant, exactly as in production.
            TickActivityClock();
            var flowId = warmupFlows + flow;
            DatagramHeader.Write(payload, flow + 1, flowId);
            _issueTicks[flow] = Stopwatch.GetTimestamp();
            if (await coordinator.TrySendSpanAsync(flowKeys[flowId], ProxyTarget.FromServer(socksServer), payload, default, CancellationToken.None).ConfigureAwait(false))
            {
                _accepted++;
            }
            else
            {
                _rejected++;
            }

            var remaining = arrivals.Advance(_watch.Elapsed);
            if (remaining > TimeSpan.Zero)
            {
                await DelayAsync(remaining).ConfigureAwait(false);
            }

            var elapsed = _watch.Elapsed;
            // The peak is read at arrival granularity, before the sweep of this iteration: the 5 s rows
            // sample the sawtooth and can miss its peak by up to a sample interval of arrivals.
            if (elapsed.TotalSeconds >= steadyThreshold)
            {
                _steadyPeakSessions = Math.Max(_steadyPeakSessions, coordinator.SessionCount);
            }

            await SweepAsync(elapsed).ConfigureAwait(false);
            if (elapsed >= nextSample)
            {
                while (elapsed >= nextSample) nextSample += s_sampleInterval;
                _churnPeakSessions = Math.Max(_churnPeakSessions, Sample("churn").Sessions);
            }
        }

        _churnPeakSessions = Math.Max(_churnPeakSessions, Sample("churnEnd").Sessions);
    }

    /// <summary>
    /// The drain window: no new flows, the sweeper still on its production cadence, and a sample
    /// every <see cref="s_sampleInterval"/>. The tail of the window plus a short settle is what
    /// the drain assertions read, so a straggling echo, the flow's own association teardown, or an
    /// idle session retirement has landed before the final descriptor count is taken.
    /// </summary>
    public async Task DrainAsync()
    {
        var drainWatch = Stopwatch.StartNew();
        var drainWindow = TimeSpan.FromSeconds(options.DrainSeconds);
        var nextSample = s_sampleInterval;
        while (drainWatch.Elapsed < drainWindow)
        {
            TickActivityClock();
            await DelayAsync(s_drainPollInterval).ConfigureAwait(false);
            await SweepAsync(_watch.Elapsed).ConfigureAwait(false);
            if (drainWatch.Elapsed < nextSample) continue;
            while (drainWatch.Elapsed >= nextSample) nextSample += s_sampleInterval;
            var sample = Sample("drain");
            if (sample.Sessions == 0 && _sessionsZeroAtSeconds is null) _sessionsZeroAtSeconds = sample.ElapsedSeconds;
        }

        await DelayAsync(s_settleTime).ConfigureAwait(false);
        _final = Sample("drainEnd");
        if (_final.Sessions == 0 && _sessionsZeroAtSeconds is null) _sessionsZeroAtSeconds = _final.ElapsedSeconds;
    }

    /// <summary>The run's pacing waits; the windows are sub-second, so each sleep is short and bounded.</summary>
    private static Task DelayAsync(TimeSpan delay) => Task.Delay(delay, TimeProvider.System);

    /// <summary>Drives the coordinator's idle expiry on the production sweeper cadence for the whole run, warm-up included.</summary>
    private async ValueTask SweepAsync(TimeSpan elapsed)
    {
        while (elapsed >= _nextSweep)
        {
            _nextSweep += sweepInterval;
            _expired += await coordinator.RemoveExpiredAsync(TimeProvider.System.GetUtcNow(), idleTimeout, oneShotIdleTimeout).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Mirrors the capture pump's per-iteration activity tick
    /// (<c>DurableCaptureBundle.FlushPendingInjections</c>), which is what advances the composition's
    /// <see cref="WinForward.Core.ActivityBucketClock"/> in production. The soak drives the sweep directly and has no
    /// pump, so without this tick the clock would stay frozen at the coordinator's construction bucket,
    /// every session would carry the same stamp, and the sweep would mass-retire the whole population at
    /// the first tick past the retention — a sawtooth artifact instead of the retention the verdicts name.
    /// </summary>
    private void TickActivityClock() => coordinator.ActivityClock.Tick();

    private SessionBudgetSample Sample(string phase)
    {
        var resources = sampler.Sample();
        var sessions = coordinator.SessionCount;
        var accepted = _accepted;
        var received = sink.ChurnFirstResponses;
        var sample = new SessionBudgetSample(
            Phase: phase,
            Index: _samples.Count + 1,
            ElapsedSeconds: _watch.Elapsed.TotalSeconds,
            Sessions: sessions,
            // The server's own view of the flow's association — the control connection it accepted and
            // has not seen close — so the row reads the peer's side of the 1:1 shape.
            Associations: resources.HarnessControlConnections,
            FileDescriptors: resources.ProxyFileDescriptors,
            RawFileDescriptors: resources.RawFileDescriptors,
            HarnessServerConnections: resources.HarnessServerConnections,
            ManagedBytes: resources.ManagedBytes,
            WorkingSetBytes: resources.WorkingSetBytes,
            BaselineFileDescriptors: _baselineFileDescriptors,
            BaselineManagedBytes: _baselineManagedBytes,
            // The heartbeat's estimate (Cli/Program.cs): live sessions × the configured relay receive buffer.
            RelayReceiveBufferBytes: (long)sessions * coordinator.RelayReceiveBufferBytes,
            Accepted: accepted,
            Rejected: _rejected,
            Expired: _expired,
            DatagramsSent: accepted,
            DatagramsReceived: received,
            DatagramsLost: Math.Max(0, accepted - received),
            Misdelivered: sink.ChurnMisdeliveredFlows,
            CapacityRejections: ProductDelta(RuntimeCounters.UdpCapacityRejections, _capacityRejectionsBefore),
            SetupRejections: ProductDelta(RuntimeCounters.UdpSetupRejections, _setupRejectionsBefore),
            SetupFailures: ProductDelta(RuntimeCounters.UdpSetupFailures, _setupFailuresBefore),
            UotHandshakes: UotHandshakeCounters.Snapshot(uotServer));
        _samples.Add(sample);
        context.WriteResult("udp.sessionBudget", _parameters, sample.ToRow());
        return sample;
    }

    private static long ProductDelta(string counter, long before) => RuntimeCounters.Shared.Get(counter) - before;

    /// <summary>
    /// Writes the verdict row and then fails the run, so a failed soak still records its evidence —
    /// including which half of the acceptance held — before the harness reports the exception.
    /// </summary>
    public void WriteSummary()
    {
        var acceptance = new SessionBudgetAcceptance(
            options,
            idleTimeout,
            sweepInterval,
            coordinator.RelayReceiveBufferBytes);
        acceptance.Evaluate(new SessionBudgetOutcome(_samples, FinalSample(), _accepted, _rejected, _churnPeakSessions, _steadyPeakSessions));
        context.WriteResult("udp.sessionBudget", _parameters, BuildSummaryRow(acceptance));
        if (!acceptance.Passed)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"udp.sessionBudget failed {acceptance.Failures.Count} acceptance assertion(s): {string.Join(" | ", acceptance.Failures)}"));
        }
    }

    /// <summary>
    /// The summary row. Fields derived from the worst steady-state sample are null when the run never
    /// reached steady state, so a missing sample is omitted instead of printing zeros that read like
    /// a measured flat shape.
    /// </summary>
    private object BuildSummaryRow(SessionBudgetAcceptance acceptance)
    {
        var final = FinalSample();
        var worst = acceptance.WorstSteady;
        var misdelivered = sink.ChurnMisdeliveredFlows;
        return new
        {
            phase = "summary",
            elapsedSeconds = _watch.Elapsed.TotalSeconds,
            samples = _samples.Count,
            steadyStateSamples = acceptance.SteadyStateSamples,
            accepted = _accepted,
            rejected = _rejected,
            expired = _expired,
            datagramsSent = final.DatagramsSent,
            datagramsReceived = final.DatagramsReceived,
            datagramsLost = final.DatagramsLost,
            lossRate = final.DatagramsSent == 0 ? 0.0 : final.DatagramsLost / (double)final.DatagramsSent,
            misdelivered,
            noResponse = Math.Max(0, _accepted - final.DatagramsReceived - misdelivered),
            firstResponseMs = BuildFirstResponses(),
            peakChurnSessions = _churnPeakSessions,
            steadyStateSessions = worst is null ? (int?)null : acceptance.SteadyPeakSessions,
            steadyStateSampledSessions = worst?.Sessions,
            steadyStateSessionCeiling = acceptance.SessionCeiling,
            steadyStateMargin = acceptance.SteadyStateMargin,
            retentionSeconds = (idleTimeout + sweepInterval).TotalSeconds,
            minimumDiscriminatingChurnSeconds = acceptance.MinimumChurnSeconds,
            retentionDiscriminating = acceptance.RetentionDiscriminating,
            baselineFileDescriptors = final.BaselineFileDescriptors,
            fileDescriptors = final.FileDescriptors,
            fileDescriptorsPerSession = worst?.FileDescriptorsPerSession,
            managedBytesPerSession = worst?.ManagedBytesPerSession,
            baselineManagedBytes = final.BaselineManagedBytes,
            managedBytes = final.ManagedBytes,
            workingSetBytes = final.WorkingSetBytes,
            relayReceiveBufferBytes = worst?.RelayReceiveBufferBytes,
            relayReceiveBufferBytesPerSession = worst?.RelayReceiveBufferBytesPerSession,
            relayReceiveBufferBudget = acceptance.RelayReceiveBufferBudget,
            descriptorBudgetHeadroom = acceptance.DescriptorHeadroom,
            uotHandshakes = UotHandshakeCounters.Snapshot(uotServer),
            drain = new
            {
                seconds = options.DrainSeconds,
                sessionsZeroAtSeconds = _sessionsZeroAtSeconds,
                sessions = final.Sessions,
                associations = final.Associations,
                fileDescriptors = final.FileDescriptors,
                descriptorDelta = final.FileDescriptors - final.BaselineFileDescriptors,
            },
            productEvents = StabilityShared.BuildProductEvents(productEvents),
            verdict = BuildVerdictRow(acceptance),
        };
    }

    /// <summary>The verdict term and the failure list it was computed from, so a failed soak's row carries its own reasons.</summary>
    private static object BuildVerdictRow(SessionBudgetAcceptance acceptance) => new
    {
        passed = acceptance.Passed,
        retentionBounded = acceptance.RetentionBounded,
        failures = acceptance.Failures,
    };

    private LatencyDistribution BuildFirstResponses()
    {
        var latencies = new List<double>(churnFlows);
        for (var flow = 0; flow < churnFlows; flow++)
        {
            var response = sink.FirstResponseTicks(warmupFlows + flow);
            if (response != 0) latencies.Add(StabilityShared.TicksToMilliseconds(response - _issueTicks[flow]));
        }

        return LatencyDistribution.FromMilliseconds(latencies);
    }

    private SessionBudgetSample FinalSample() =>
        _final ?? throw new InvalidOperationException("The session-budget soak has no drain sample; the run did not reach its verdict phase.");
}
