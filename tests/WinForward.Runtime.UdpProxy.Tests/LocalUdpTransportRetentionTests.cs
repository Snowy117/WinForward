using WinForward.Configuration;
using WinForward.Core;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FlowBuilders;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// Retention over the real local transport: the exchange evidence a completed one-shot flow needs
/// (<see cref="IUdpExchangeCounters"/>) is produced by real datagrams rather than a fake's counters,
/// so a local-target DNS session retires at the one-shot class instead of holding a socket for the
/// configured retention. The class joins <see cref="UdpLocalTargetCounterCollection"/> because the
/// expiry fact below asserts an exact <c>udpLocalTargetFailures</c> delta.
/// </summary>
[Collection(UdpLocalTargetCounterCollection.Name)]
public sealed class LocalUdpTransportRetentionTests
{
    private const int MaximumFrameSize = 1_514;

    [Fact]
    public async Task CompletedOneShotExchangeRetiresAtTheOneShotClass()
    {
        await using var responder = new LoopbackUdpResponder();
        var time = new MutableTimeProvider(new DateTimeOffset(TimeSpan.FromHours(1).Ticks, TimeSpan.Zero));
        var clock = new ActivityBucketClock(time);
        await using var harness = new RetentionHarness(time, clock, responder);

        await harness.AdmitAsync();

        // One send and one reply: evidence the transport carries through IUdpExchangeCounters, so the
        // session is a completed one-shot exchange rather than a sustained flow.
        Assert.Equal(1, harness.DatagramsSent);
        Assert.True(harness.SawResponse);

        harness.Advance(UdpProxyCoordinator.OneShotIdleTimeout);
        Assert.Equal(0, await harness.SweepAsync(TimeSpan.FromSeconds(30)));
        harness.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, await harness.SweepAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, harness.Coordinator.SessionCount);
    }

    [Fact]
    public async Task AnExpiryWithAParkedReceiveDoesNotCountAFailure()
    {
        await using var responder = new LoopbackUdpResponder();
        var time = new MutableTimeProvider(new DateTimeOffset(TimeSpan.FromHours(1).Ticks, TimeSpan.Zero));
        var clock = new ActivityBucketClock(time);
        await using var harness = new RetentionHarness(time, clock, responder);

        await harness.AdmitAsync();

        // The parked receive the session's loop holds while it waits for a second datagram, plus the
        // session's own scope cancellation: an idle expiry is the normal end of the flow, so neither
        // may move the failure counter.
        var parked = harness.Transport.ReceiveAsync(new byte[MaximumFrameSize], CancellationToken.None).AsTask();
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFailures);

        harness.Advance(UdpProxyCoordinator.OneShotIdleTimeout);
        Assert.Equal(0, await harness.SweepAsync(TimeSpan.FromSeconds(30)));
        harness.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, await harness.SweepAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(0, harness.Coordinator.SessionCount);
        await Assert.ThrowsAnyAsync<Exception>(() => parked);
        Assert.Equal(0, RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFailures) - before);
    }

    /// <summary>
    /// A coordinator over the real local transport factory, a bucket-aligned mutable clock, and a
    /// loopback responder, so the retention class is decided from evidence a real one-shot exchange
    /// produced rather than from a fake's counters.
    /// </summary>
    private sealed class RetentionHarness : IAsyncDisposable
    {
        private readonly RecordingTransportFactory _factory;
        private readonly LoopbackUdpResponder _responder;
        private readonly NativeBufferPool _receiveWindowPool;
        private readonly SetupExecutor _setupExecutor = new();

        internal RetentionHarness(MutableTimeProvider time, ActivityBucketClock clock, LoopbackUdpResponder responder)
        {
            Time = time;
            Clock = clock;
            _responder = responder;
            _factory = new RecordingTransportFactory(new LocalUdpTransportFactory(new SelfTrafficRegistry(), MaximumFrameSize));
            _receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(MaximumFrameSize));
            Coordinator = new UdpProxyCoordinator(
                _factory,
                new FakeResponseSink(),
                new NativeBufferPool(MaximumFrameSize),
                _receiveWindowPool,
                _setupExecutor,
                new UdpProxyOptions { Capacity = 16, TimeProvider = time, ActivityClock = clock });
        }

        internal UdpProxyCoordinator Coordinator { get; }

        private MutableTimeProvider Time { get; }

        private ActivityBucketClock Clock { get; }

        internal LocalUdpTransport Transport => (LocalUdpTransport)_factory.Transports[0];

        internal int DatagramsSent => ((IUdpExchangeCounters)Transport).DatagramsSent;

        internal bool SawResponse => ((IUdpExchangeCounters)Transport).SawResponse;

        private static ProxyTarget Local(string name, Endpoint endpoint) => new(name, Socks5: null, new LocalTarget(name, endpoint));

        internal async Task AdmitAsync()
        {
            var flow = CreateFlow("192.0.2.53");
            Assert.True(await Coordinator.TrySendSpanAsync(flow, Local("dns-in", _responder.Endpoint), [0x11], default, CancellationToken.None));
            await WaitForAsync(() => _factory.Transports.Count == 1 && SawResponse);
        }

        internal void Advance(TimeSpan delta)
        {
            Time.Advance(delta);
            Clock.Tick();
        }

        internal ValueTask<int> SweepAsync(TimeSpan longTtl) => Coordinator.RemoveExpiredAsync(Time.GetUtcNow(), longTtl, UdpProxyCoordinator.OneShotIdleTimeout);

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            _setupExecutor.Dispose();
            _receiveWindowPool.Dispose();
        }
    }
}
