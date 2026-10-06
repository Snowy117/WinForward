using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FlowBuilders;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The one-shot retirement class over the real UoT transport. The exchange evidence comes from
/// <c>Socks5UotTransport</c>'s own accepted-send counter and write-once decoded-frame flag, read by
/// the session's classification and by the coordinator's two-class sweep exactly as the native
/// transport's counters are — so a completed one-shot UoT exchange retires at the 5 s class instead
/// of the configured 30 s, while a second datagram promotes the flow to the sustained class. The
/// facts drive the real dial, the real framed round trip through the scripted server, and the real
/// <c>RemoveExpiredAsync</c> decision over a bucket-aligned mutable clock.
/// </summary>
public sealed class UdpUotRetentionTests
{
    private static readonly TimeSpan s_sustained = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_oneShot = UdpProxyCoordinator.OneShotIdleTimeout;

    /// <summary>The activity quantum; one bucket is the finest late-retirement step the sweep can take.</summary>
    private static readonly TimeSpan s_bucket = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task ACompletedOneShotUotExchangeRetiresOnTheFiveSecondClass()
    {
        await using var harness = await UotRetentionHarness.CreateAsync(datagramCount: 1);
        Assert.Equal(1, harness.Evidence.DatagramsSent);
        Assert.True(harness.Evidence.SawResponse);

        // Past the one-shot class and well below the configured retention: only the short class can
        // retire this session, so a transport whose evidence never reached the session would keep it.
        harness.Advance(s_oneShot + s_bucket);
        Assert.Equal(1, await harness.SweepAsync(s_sustained, s_oneShot));
        Assert.Equal(0, harness.Coordinator.SessionCount);
    }

    [Fact]
    public async Task AnAnsweredTwoDatagramUotExchangeKeepsTheConfiguredRetention()
    {
        await using var harness = await UotRetentionHarness.CreateAsync(datagramCount: 2);
        Assert.Equal(2, harness.Evidence.DatagramsSent);
        Assert.True(harness.Evidence.SawResponse);

        // The second datagram is positive evidence of a stream: the short class must not take it,
        // and it retires on the configured retention exactly as the sustained native session does.
        harness.Advance(s_oneShot + s_bucket + (20 * s_bucket));
        Assert.Equal(0, await harness.SweepAsync(s_sustained, s_oneShot));
        Assert.Equal(1, harness.Coordinator.SessionCount);

        harness.Advance(s_sustained - s_oneShot - (21 * s_bucket) + s_bucket);
        Assert.Equal(1, await harness.SweepAsync(s_sustained, s_oneShot));
        Assert.Equal(0, harness.Coordinator.SessionCount);
    }

    /// <summary>
    /// A coordinator over the real per-flow UoT factory, the scripted server, and a bucket-aligned
    /// mutable clock: one flow, established and answered through the real transport, whose sweep is
    /// the production two-class decision. The transport's exchange evidence is exposed because it is
    /// what the class is computed from.
    /// </summary>
    private sealed class UotRetentionHarness : IAsyncDisposable
    {
        private readonly ScriptedSocks5UotServer _server;

        private UotRetentionHarness(ScriptedSocks5UotServer server, Socks5UotTransport transport, UdpProxyCoordinator coordinator, MutableTimeProvider time, ActivityBucketClock clock)
        {
            _server = server;
            Evidence = transport;
            Coordinator = coordinator;
            Time = time;
            Clock = clock;
        }

        public UdpProxyCoordinator Coordinator { get; }

        public IUdpExchangeCounters Evidence { get; }

        private MutableTimeProvider Time { get; }

        private ActivityBucketClock Clock { get; }

        public static async Task<UotRetentionHarness> CreateAsync(int datagramCount)
        {
            // Bucket-aligned start: every stamp and cutoff below is an exact number of 500 ms buckets,
            // so the two class boundaries are not sensitive to where the clock happened to begin.
            var time = new MutableTimeProvider(new DateTimeOffset(TimeSpan.FromHours(1).Ticks, TimeSpan.Zero));
            var clock = new ActivityBucketClock(time);
            var server = new ScriptedSocks5UotServer();
            var sink = new FakeResponseSink();
            var transports = new RecordingTransportFactory(new Socks5UdpTransportFactory(new SelfTrafficRegistry(), UdpFrameBuilder.DefaultMaximumEthernetFrame));
            var coordinator = UdpCoordinatorFakes.CreateCoordinator(transports, sink, new UdpProxyOptions { Capacity = 16, TimeProvider = time, ActivityClock = clock });
            var flow = CreateFlow("192.0.2.53");
            var target = ProxyTarget.FromServer(server.Server);
            try
            {
                // The first datagram is buffered and flushed by the setup; ready therefore means it
                // was handed to the transport, which is where the send counter is incremented.
                Assert.True(await coordinator.TrySendSpanAsync(flow, target, [1], default, CancellationToken.None));
                await WaitForAsync(() => coordinator.SessionReadyForDiagnostics(flow));
                for (var index = 1; index < datagramCount; index++)
                {
                    Assert.True(await coordinator.TrySendSpanAsync(flow, target, [(byte)index], default, CancellationToken.None));
                }

                // The answer is the response evidence: the transport sets its write-once flag before
                // the frame is handed to the session, so a sink read proves the answered class.
                await server.InjectFrameAsync(new byte[] { 7 });
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                _ = await sink.Responses.Reader.ReadAsync(budget.Token);

                var transport = Assert.IsType<Socks5UotTransport>(Assert.Single(transports.Transports));
                return new UotRetentionHarness(server, transport, coordinator, time, clock);
            }
            catch
            {
                await coordinator.DisposeAsync();
                await server.DisposeAsync();
                throw;
            }
        }

        public void Advance(TimeSpan delta)
        {
            Time.Advance(delta);
            Clock.Tick();
        }

        public ValueTask<int> SweepAsync(TimeSpan longTtl, TimeSpan shortTtl) =>
            Coordinator.RemoveExpiredAsync(Time.GetUtcNow(), longTtl, shortTtl);

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await _server.DisposeAsync();
        }
    }
}
