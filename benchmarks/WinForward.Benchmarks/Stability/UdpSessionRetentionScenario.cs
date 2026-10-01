using System.Globalization;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The mixed one-shot/sustained retention arm (F6). It measures a mechanism with no pre-change
/// counterpart, so instead of a before-artifact it carries its own control: two identically built
/// cohorts, one shared mutable clock, and <em>one sweep per cohort at the same instant</em> — the
/// control sweeping with both timeouts equal to the configured retention (nothing is past it, so both
/// classes stay resident) and the treatment sweeping with the 5 s one-shot class.
/// <para>
/// One coordinator per cohort is load-bearing: a coordinator sweep is population-wide, so a single
/// coordinator could not sweep its control half without also retiring its treatment half before the
/// treatment arm could be read. The two coordinators share the clock, the transport factory and the
/// receive-window pool, so the only difference between the two rows is the classification.
/// </para>
/// <para>
/// The row carries the population it actually retired per class, so a vacuous run cannot green the
/// measurement: the run aborts unless each cohort's population is proven live before its sweep (the
/// shared factory's created count and every transport's flush, cross-checked against the
/// coordinator's slot count), the treatment's two timeouts differ, and the two arms come out in the
/// shape the mechanism predicts (control: both classes resident; treatment: exactly the one-shot
/// class retired).
/// </para>
/// </summary>
internal static class UdpSessionRetentionScenario
{
    /// <summary>Sessions per class, per cohort: small enough to build in-process, large enough that a partial sweep is visible.</summary>
    private const int SessionsPerClass = 32;

    private const int MaximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;

    /// <summary>The activity quantum; the clock advances past the short class by one bucket.</summary>
    private static readonly TimeSpan s_bucket = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan s_longTtl = ConfigurationLoader.DefaultUdpSessionIdleTimeout;
    private static readonly TimeSpan s_populationTimeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] s_populatePayload = [1];
    private static readonly Socks5Server s_server = new("retention", "127.0.0.1", 1080, Username: null, Password: null);

    public static async Task RunAsync(StabilityContext context, SoakOptions _)
    {
        var shortTtl = UdpProxyCoordinator.OneShotIdleTimeout;
        if (shortTtl >= s_longTtl)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The one-shot class ({shortTtl.TotalSeconds:0.#} s) must be shorter than the configured retention ({s_longTtl.TotalSeconds:0.#} s), otherwise the two arms are identical and the measurement is vacuous."));
        }

        // Bucket-aligned start so the sweep's cutoff comparison is an exact number of 500 ms buckets.
        var time = new MutableTimeProvider(new DateTimeOffset(TimeSpan.FromHours(1).Ticks, TimeSpan.Zero));
        var clock = new ActivityBucketClock(time);
        var factory = new BenchmarkExchangeTransportFactory();
        using var setupQueuePool = new NativeBufferPool(MaximumFrameSize);
        using var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(MaximumFrameSize), UdpProxyCoordinator.ReceiveWindowPoolCapacity(4 * SessionsPerClass));
        using var setupExecutor = new SetupExecutor();

        var control = CreateCoordinator(factory, setupQueuePool, receiveWindowPool, setupExecutor, time, clock);
        var treatment = CreateCoordinator(factory, setupQueuePool, receiveWindowPool, setupExecutor, time, clock);
        try
        {
            var controlCohort = await BuildCohortAsync(control, factory, flowOffset: 0).ConfigureAwait(false);
            var treatmentCohort = await BuildCohortAsync(treatment, factory, flowOffset: controlCohort.Length).ConfigureAwait(false);

            // One shared instant for both arms: past the short class, below the long one.
            time.Advance(shortTtl + s_bucket);
            clock.Tick();
            var now = time.GetUtcNow();

            var controlRemoved = await control.RemoveExpiredAsync(now, s_longTtl, s_longTtl).ConfigureAwait(false);
            var treatmentRemoved = await treatment.RemoveExpiredAsync(now, s_longTtl, shortTtl).ConfigureAwait(false);

            var controlArm = Measure(control, controlCohort);
            var treatmentArm = Measure(treatment, treatmentCohort);
            Validate("control", controlArm, controlRemoved, expectedOneShotResident: SessionsPerClass, expectedSustainedResident: SessionsPerClass);
            Validate("treatment", treatmentArm, treatmentRemoved, expectedOneShotResident: 0, expectedSustainedResident: SessionsPerClass);

            context.WriteResult("udp.sessionRetention.control", new { arm = "control" }, Row(controlArm, s_longTtl, s_longTtl));
            context.WriteResult("udp.sessionRetention.treatment", new { arm = "treatment" }, Row(treatmentArm, shortTtl, s_longTtl));
        }
        finally
        {
            await control.DisposeAsync().ConfigureAwait(false);
            await treatment.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static UdpProxyCoordinator CreateCoordinator(
        BenchmarkExchangeTransportFactory factory,
        NativeBufferPool setupQueuePool,
        NativeBufferPool receiveWindowPool,
        SetupExecutor setupExecutor,
        MutableTimeProvider time,
        ActivityBucketClock clock) =>
        new(
            factory,
            NoopUdpResponseSink.Instance,
            setupQueuePool,
            receiveWindowPool,
            setupExecutor,
            new UdpProxyOptions { Capacity = 2 * SessionsPerClass, TimeProvider = time, ActivityClock = clock });

    /// <summary>
    /// Builds one cohort of <c>2 x SessionsPerClass</c> sessions, one flow at a time so the factory's
    /// creation order indexes the cohort, and places each transport in its retention class by writing
    /// the exchange evidence the product reads. Returns with the population proven live.
    /// </summary>
    private static async Task<CohortMember[]> BuildCohortAsync(UdpProxyCoordinator coordinator, BenchmarkExchangeTransportFactory factory, int flowOffset)
    {
        var members = new CohortMember[2 * SessionsPerClass];
        for (var index = 0; index < members.Length; index++)
        {
            var flow = MakeFlow(flowOffset + index);
            var oneShot = index < SessionsPerClass;
            var expectedCreated = factory.Transports.Count + 1;
            if (!await coordinator.TrySendSpanAsync(flow, s_server, s_populatePayload, default, CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The retention scenario could not admit flow {flowOffset + index}."));
            }

            await WaitUntilAsync(
                () => factory.Transports.Count >= expectedCreated && factory.Transports[expectedCreated - 1].SentCount >= 1,
                string.Create(CultureInfo.InvariantCulture, $"flow {flowOffset + index} never reached a flushed transport")).ConfigureAwait(false);

            var transport = factory.Transports[expectedCreated - 1];
            transport.DatagramsSent = oneShot ? 1 : 2;
            transport.SawResponse = true;
            members[index] = new CohortMember(flow, transport, oneShot);
        }

        // ReSharper disable once ConvertIfStatementToReturnStatement // The population proof is this scenario's abort guard: the throw is the measurement self-check and must read as a standalone early exit, not as the false arm of a return expression (repo precedent: ExternalLoopbackSocks5UdpServer's latch guard).
        if (coordinator.SessionCount != members.Length)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The retention scenario built {members.Length} flows but the coordinator holds {coordinator.SessionCount} sessions; the population is not live."));
        }

        return members;
    }

    private static RetentionArm Measure(UdpProxyCoordinator coordinator, CohortMember[] cohort)
    {
        var oneShotResident = 0;
        var sustainedResident = 0;
        foreach (var member in cohort)
        {
            if (coordinator.SessionState(member.Flow) == UdpSessionState.SettingUp) continue;
            if (member.OneShot) oneShotResident++;
            else sustainedResident++;
        }

        return new RetentionArm(oneShotResident, sustainedResident);
    }

    private static void Validate(string arm, RetentionArm measured, int removed, int expectedOneShotResident, int expectedSustainedResident)
    {
        var resident = measured.OneShotResident + measured.SustainedResident;
        if (removed != (2 * SessionsPerClass) - resident)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The {arm} arm removed {removed} sessions but its resident population moved by {(2 * SessionsPerClass) - resident}."));
        }

        if (measured.OneShotResident != expectedOneShotResident || measured.SustainedResident != expectedSustainedResident)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The {arm} arm came out as oneShotResident={measured.OneShotResident} sustainedResident={measured.SustainedResident}; expected oneShotResident={expectedOneShotResident} sustainedResident={expectedSustainedResident}."));
        }
    }

    private static object Row(RetentionArm measured, TimeSpan shortTtl, TimeSpan longTtl) => new
    {
        oneShot = SessionsPerClass,
        sustained = SessionsPerClass,
        shortTtlSeconds = shortTtl.TotalSeconds,
        longTtlSeconds = longTtl.TotalSeconds,
        oneShotResident = measured.OneShotResident,
        sustainedResident = measured.SustainedResident,
    };

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        using var timeout = new CancellationTokenSource(s_populationTimeout);
        while (!condition())
        {
            try
            {
                await Task.Delay(10, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    private static FlowKey MakeFlow(int index) =>
        FlowKey.Create(
            Endpoint.From(System.Net.IPAddress.Parse("192.0.2.10"), checked((ushort)(40_000 + index))),
            Endpoint.From(System.Net.IPAddress.Parse("192.0.2.53"), 53),
            TransportProtocol.Udp,
            FlowOriginKind.Host);

    private sealed record CohortMember(FlowKey Flow, BenchmarkExchangeTransport Transport, bool OneShot);

    private sealed record RetentionArm(int OneShotResident, int SustainedResident);

    /// <summary>The shared mutable clock both coordinators' sessions stamp against.</summary>
    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
