using System.Net;
using WinForward.Benchmarks.Stability;
using WinForward.Configuration;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;

namespace WinForward.Core.Tests;

/// <summary>
/// The two-class idle retention: a session whose flow sent one datagram and got a response is a
/// completed one-shot exchange and retires on <see cref="UdpProxyCoordinator.OneShotIdleTimeout"/>
/// (5 s); every other session keeps the configured retention. The facts drive the real
/// <c>RemoveExpiredAsync</c> + <c>TryBeginExpiry</c> path over a mutable clock and a fake transport
/// that implements the exchange-evidence seam, so both edges of each class are exact — and the class
/// is read from the same constant the product uses.
/// </summary>
public sealed class UdpSessionRetentionTests
{
    private static readonly TimeSpan s_long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_short = UdpProxyCoordinator.OneShotIdleTimeout;

    /// <summary>The activity quantum; one bucket is the finest late-retirement step the sweep can take.</summary>
    private static readonly TimeSpan s_bucket = TimeSpan.FromMilliseconds(500);

    private static readonly byte[] s_payload = [1];
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    [Fact]
    public async Task ACompletedSingleExchangeIsRetiredAtTheShortTtlAndNotOneBucketEarlier()
    {
        await using var harness = await RetentionHarness.CreateAsync();
        await harness.AdmitAsync(datagramsSent: 1, sawResponse: true);

        // Exactly at the short TTL the cutoff bucket still equals the stamp bucket, and the sweep's
        // comparison is strict — retirement is never early.
        harness.Advance(s_short);
        Assert.Equal(0, await harness.SweepAsync(s_long, s_short));

        harness.Advance(s_bucket);
        Assert.Equal(1, await harness.SweepAsync(s_long, s_short));
        Assert.Equal(0, harness.Coordinator.SessionCount);
    }

    [Fact]
    public async Task ASustainedExchangeKeepsTheLongTtl()
    {
        await using var harness = await RetentionHarness.CreateAsync();
        await harness.AdmitAsync(datagramsSent: 2, sawResponse: true);

        // The second datagram is positive evidence of a stream: many short-class windows pass.
        harness.Advance(s_short + (20 * s_bucket));
        Assert.Equal(0, await harness.SweepAsync(s_long, s_short));

        harness.Advance(s_long - s_short - (20 * s_bucket));
        Assert.Equal(0, await harness.SweepAsync(s_long, s_short));

        harness.Advance(s_bucket);
        Assert.Equal(1, await harness.SweepAsync(s_long, s_short));
    }

    [Fact]
    public async Task AnUnansweredSingleExchangeIsNotRetiredOnTheShortTtl()
    {
        await using var harness = await RetentionHarness.CreateAsync();
        await harness.AdmitAsync(datagramsSent: 1, sawResponse: false);

        // A slow first reply must never be cut: without a response the session is not a completed
        // exchange, so it keeps the configured retention and retires on the long class.
        harness.Advance(s_short + (20 * s_bucket));
        Assert.Equal(0, await harness.SweepAsync(s_long, s_short));

        harness.Advance(s_long - s_short - (20 * s_bucket) + s_bucket);
        Assert.Equal(1, await harness.SweepAsync(s_long, s_short));
    }

    [Fact]
    public async Task TheClassificationPromotesOnTheSecondDatagramAndNeverDemotes()
    {
        await using var harness = await RetentionHarness.CreateAsync();

        // Two sessions at the same instant and with the same activity stamp: one completed one-shot
        // and one about to be promoted by its second datagram.
        var answerered = await harness.AdmitAsync(datagramsSent: 1, sawResponse: true);
        var promoting = await harness.AdmitAsync(datagramsSent: 1, sawResponse: true);
        harness.Advance(s_short + s_bucket);

        // Control: the untouched one-shot retires on the short class at this instant.
        promoting.DatagramsSent = 2;
        Assert.Equal(1, await harness.SweepAsync(s_long, s_short));
        Assert.True(answerered.SawResponse);

        // The promoted session survives every later short-class window: the classification is monotone
        // (the counters only grow and the response flag is write-once), so it can never re-enter it.
        harness.Advance(20 * s_bucket);
        Assert.Equal(0, await harness.SweepAsync(s_long, s_short));
        harness.Advance(s_long - s_short - (21 * s_bucket) + s_bucket);
        Assert.Equal(1, await harness.SweepAsync(s_long, s_short));
    }

    [Fact]
    public async Task TheTwoArgumentSweepKeepsUniformRetention()
    {
        await using var harness = await RetentionHarness.CreateAsync();
        await harness.AdmitAsync(datagramsSent: 1, sawResponse: true);

        // The legacy overload passes one timeout for both classes, so a completed one-shot keeps the
        // configured retention exactly as it did before the short class existed.
        harness.Advance(s_short + s_bucket);
        Assert.Equal(0, await harness.SweepUniformAsync(s_long));

        harness.Advance(s_long - s_short - s_bucket);
        Assert.Equal(0, await harness.SweepUniformAsync(s_long));

        harness.Advance(s_bucket);
        Assert.Equal(1, await harness.SweepUniformAsync(s_long));
    }

    [Fact]
    public async Task TheSweeperRetiresAOneShotSessionOnTheShortCadenceAndKeepsASustainedOne()
    {
        await using var harness = await RetentionHarness.CreateAsync();
        var oneShot = await harness.AdmitAsync(datagramsSent: 1, sawResponse: true);
        var sustained = await harness.AdmitAsync(datagramsSent: 2, sawResponse: true);
        Assert.Equal(2, harness.Coordinator.SessionCount);

        var dispatcherHarness = TcpCoordinatorFakes.CreateDispatcherHarness();
        await using var sweeper = new IdleExpirySweeper(
            dispatcherHarness.Dispatcher,
            tcp: null,
            harness.Coordinator,
            interval: TimeSpan.FromMinutes(1),
            relayIdleTimeout: s_long,
            logger: dispatcherHarness.Logger,
            timeProvider: harness.Time,
            udpSweepInterval: TimeSpan.FromMilliseconds(20),
            udpOneShotIdleTimeout: s_short);
        sweeper.Start();

        // Past the short class and below the long one: only the completed one-shot may go.
        harness.Advance(s_short + s_bucket);
        await WaitForAsync(() => harness.Coordinator.SessionCount == 1);

        Assert.Equal(UdpSessionState.SettingUp, harness.Coordinator.SessionState(harness.Flows[0]));
        Assert.Equal(UdpSessionState.Active, harness.Coordinator.SessionState(harness.Flows[1]));
        Assert.True(sustained.SawResponse && oneShot.SawResponse);
    }

    [Fact]
    public void EffectiveUdpRetentionFloorFloorsOnTheOneShotClass()
    {
        Assert.Equal(s_short, IdleExpirySweeper.EffectiveUdpRetentionFloor(s_long, s_short));
        Assert.Equal(s_long, IdleExpirySweeper.EffectiveUdpRetentionFloor(s_long, oneShotIdleTimeout: null));
        Assert.Equal(s_long, IdleExpirySweeper.EffectiveUdpRetentionFloor(s_long, s_long));

        // Degenerate configuration: a retention at or below the class collapses the two, i.e. uniform
        // retention, and the tick follows the configured value rather than the class.
        Assert.Equal(TimeSpan.FromSeconds(5), IdleExpirySweeper.EffectiveUdpRetentionFloor(TimeSpan.FromSeconds(5), s_short));
        Assert.Equal(TimeSpan.FromSeconds(3), IdleExpirySweeper.EffectiveUdpRetentionFloor(TimeSpan.FromSeconds(3), s_short));

        // The shipped derivation: the 5 s class drives the 5 s tick the production composition uses.
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            IdleExpirySweeper.DeriveUdpSweepInterval(TimeSpan.FromMinutes(1), IdleExpirySweeper.EffectiveUdpRetentionFloor(s_long, s_short), udpSweepInterval: null));
    }

    [Fact]
    public void TheRetentionProbeIsSelectableAndExcludedFromAll()
    {
        Assert.Equal(SoakScenario.Retention, SoakOptions.Parse(["--scenario", "retention"]).Scenario);

        var scenarios = SoakRunner.SelectScenarios(SoakScenario.Retention);
        var (name, _) = Assert.Single(scenarios);
        Assert.Equal("retention", name);
        Assert.DoesNotContain(SoakRunner.SelectScenarios(SoakScenario.All), entry => string.Equals(entry.Name, "retention", StringComparison.Ordinal));
    }

    /// <summary>
    /// A coordinator over fake exchange-carrying transports, a bucket-aligned mutable clock, and one
    /// admitted flow per call. Sequential admission is what maps <see cref="Flows"/> to the transports
    /// <see cref="AdmitAsync"/> returns by index, so a fact can set the evidence of the flow it names.
    /// </summary>
    private sealed class RetentionHarness : IAsyncDisposable
    {
        private const int MaximumFrameSize = 1_514;

        private readonly ExchangeTransportFactory _factory;
        private readonly NativeBufferPool _setupQueuePool;
        private readonly NativeBufferPool _receiveWindowPool;
        private readonly SetupExecutor _setupExecutor = new();

        private RetentionHarness(MutableTimeProvider time, ActivityBucketClock clock, ExchangeTransportFactory factory, NativeBufferPool setupQueuePool, NativeBufferPool receiveWindowPool)
        {
            Time = time;
            Clock = clock;
            _factory = factory;
            _setupQueuePool = setupQueuePool;
            _receiveWindowPool = receiveWindowPool;
            Coordinator = new UdpProxyCoordinator(
                factory,
                new NoopResponseSink(),
                setupQueuePool,
                receiveWindowPool,
                _setupExecutor,
                new UdpProxyOptions { Capacity = 64, TimeProvider = time, ActivityClock = clock });
        }

        public UdpProxyCoordinator Coordinator { get; }

        public MutableTimeProvider Time { get; }

        private ActivityBucketClock Clock { get; }

        public List<FlowKey> Flows { get; } = [];

        public static Task<RetentionHarness> CreateAsync()
        {
            // Bucket-aligned start: every stamp and cutoff below is an exact number of 500 ms buckets,
            // so the boundary facts are not sensitive to where the clock happened to begin.
            var time = new MutableTimeProvider(new DateTimeOffset(TimeSpan.FromHours(1).Ticks, TimeSpan.Zero));
            var clock = new ActivityBucketClock(time);
            return Task.FromResult(new RetentionHarness(
                time,
                clock,
                new ExchangeTransportFactory(),
                new NativeBufferPool(MaximumFrameSize),
                new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(MaximumFrameSize))));
        }

        public async Task<ExchangeTransport> AdmitAsync(int datagramsSent, bool sawResponse)
        {
            var index = Flows.Count;
            var flow = MakeFlow(index);
            Flows.Add(flow);
            Assert.True(await Coordinator.TrySendSpanAsync(flow, s_server, s_payload, default, CancellationToken.None));

            // The population proof is the transport's creation plus the flush of the queued datagram:
            // the flush is what stamps the session's activity, so advancing the clock before it would
            // measure a session stamped after the advance.
            await WaitForAsync(() => _factory.Transports.Count == Flows.Count && _factory.Transports[index].SentCount >= 1);
            Assert.Equal(Flows.Count, Coordinator.SessionCount);

            var transport = _factory.Transports[index];
            transport.DatagramsSent = datagramsSent;
            transport.SawResponse = sawResponse;
            return transport;
        }

        public void Advance(TimeSpan delta)
        {
            Time.Advance(delta);
            Clock.Tick();
        }

        public ValueTask<int> SweepAsync(TimeSpan longTtl, TimeSpan shortTtl) =>
            Coordinator.RemoveExpiredAsync(Time.GetUtcNow(), longTtl, shortTtl);

        public ValueTask<int> SweepUniformAsync(TimeSpan ttl) =>
            Coordinator.RemoveExpiredAsync(Time.GetUtcNow(), ttl);

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            _setupExecutor.Dispose();
            _setupQueuePool.Dispose();
            _receiveWindowPool.Dispose();
        }

        private static FlowKey MakeFlow(int index) =>
            FlowKey.Create(
                Endpoint.From(IPAddress.Parse("192.0.2.10"), checked((ushort)(53_000 + index))),
                Endpoint.From(IPAddress.Parse("192.0.2.53"), 53),
                TransportProtocol.Udp,
                FlowOriginKind.Host);
    }

    private sealed class ExchangeTransportFactory : IUdpProxyTransportFactory
    {
        private int _nextLocalPort = 40_000;
        private int _created;

        public List<ExchangeTransport> Transports { get; } = [];

        public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            var transport = new ExchangeTransport(Interlocked.Increment(ref _nextLocalPort));
            Transports.Add(transport);
            Interlocked.Increment(ref _created);
            return ValueTask.FromResult<IUdpProxyTransport>(transport);
        }
    }

    /// <summary>
    /// A fake relay transport that also carries the exchange evidence, so a fact can place a flow in
    /// either retention class without driving real datagrams through it.
    /// </summary>
    private sealed class ExchangeTransport(int localPort) : IUdpProxyTransport, IUdpExchangeCounters
    {
        private int _sent;

        public IPEndPoint RelayEndpoint { get; } = new(IPAddress.Loopback, 50_000);

        public IPEndPoint LocalEndpoint { get; } = new(IPAddress.Loopback, localPort);

        public int DatagramsSent { get; set; }

        public bool SawResponse { get; set; }

        /// <summary>The datagrams handed to this transport: the flush proof the harness waits on.</summary>
        public int SentCount => Volatile.Read(ref _sent);

        public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sent);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("the receive loop should end through cancellation");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
