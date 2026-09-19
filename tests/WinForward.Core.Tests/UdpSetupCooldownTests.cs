using System.Globalization;
using WinForward.Configuration;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.FlowBuilders;

namespace WinForward.Core.Tests;

/// <summary>
/// R1: the cooldown tombstone and the cap that must not swallow it. A failed setup writes a
/// fail-closed cooldown tombstone (bounded by the session capacity, evicting the oldest retry
/// deadline); the 8-wide concurrent-setup cap must instead queue flows beyond the cap (patient
/// admission) rather than fail them into that tombstone — the 64-flow flash crowd pins that
/// every first datagram is admitted without loss.
/// </summary>
public sealed class UdpSetupCooldownTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    [Fact]
    public async Task FailedSetupEntersCooldownAndRetriesAfterOneSecond()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FailingTransportFactory();
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16, TimeProvider = time, Logger = logger });
        var flow = CreateFlow("192.0.2.53");

        // Accepted (buffered); the failure itself surfaces through the background setup task.
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));

        // Frozen fake time keeps the tombstone active: once the failure is processed, every
        // datagram is rejected fail-closed for the cooldown window.
        Assert.True(await WaitUntilFalseAsync(() => coordinator.TrySendSpanAsync(flow, s_server, [2], default, CancellationToken.None).AsTask()));
        Assert.Equal(1, factory.CreateCalls);
        Assert.Contains(logger.Events, item => string.Equals(item.Name, "udp.setup.cooldown", StringComparison.Ordinal));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [3], default, CancellationToken.None));
        await WaitForAsync(() => factory.CreateCalls == 2);
    }

    [Fact]
    public async Task SetupCooldownsAreBoundedAndEvictTheOldestAtCapacity()
    {
        // R3-UDP: the cooldown dictionary is bounded by the session capacity; at capacity the
        // oldest retry deadline is evicted, so a failing-server storm cannot grow it without
        // bound while every recent flow keeps its cooldown (eviction, never refusal).
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FailingTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 4, TimeProvider = time });
        const int flowCount = 5;
        var flows = Enumerable.Range(0, flowCount).Select(index => CreateFlow(string.Create(CultureInfo.InvariantCulture, $"192.0.2.{index + 1}"))).ToArray();

        for (var index = 0; index < flowCount; index++)
        {
            Assert.True(await coordinator.TrySendSpanAsync(flows[index], s_server, [1], default, CancellationToken.None));
            // The failure surfaces through the background task: wait for the flow's cooldown to
            // arm, then advance the clock so each flow earns a distinct retry deadline.
            Assert.True(await WaitUntilFalseAsync(() => coordinator.TrySendSpanAsync(flows[index], s_server, [2], default, CancellationToken.None).AsTask()));
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        // Bounded at capacity: the first flow's cooldown entry (the oldest deadline) was evicted.
        Assert.Equal(4, coordinator.Diagnostics.SetupCooldownCount);

        // Every surviving cooldown entry still cools down its flow at the frozen clock...
        for (var index = 1; index < flowCount; index++)
        {
            Assert.False(await coordinator.TrySendSpanAsync(flows[index], s_server, [3], default, CancellationToken.None));
        }

        // ...while the evicted flow retries immediately instead of being cooldown-rejected.
        Assert.True(await coordinator.TrySendSpanAsync(flows[0], s_server, [3], default, CancellationToken.None));
        await WaitForAsync(() => factory.CreateCalls == flowCount + 1);
        Assert.Equal(4, coordinator.Diagnostics.SetupCooldownCount);
    }

    [Fact]
    public async Task SetupConcurrencyCapQueuesFlowsBeyondTheCapUntilASlotFrees()
    {
        // Patient admission: the 8-wide setup cap must queue the ninth flow's setup, not fail
        // it into the cooldown tombstone (whose teardown drops the buffered datagram).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task);
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16, Logger = logger });
        const int cappedFlows = 8;
        var flows = Enumerable.Range(0, cappedFlows + 1).Select(index => CreateFlow(string.Create(CultureInfo.InvariantCulture, $"192.0.2.{index + 1}"))).ToArray();

        for (var index = 0; index <= cappedFlows; index++)
        {
            Assert.True(await coordinator.TrySendSpanAsync(flows[index], s_server, [(byte)index], default, CancellationToken.None));
        }

        // The first eight setups occupy the limiter; the ninth setup is queued on it.
        await WaitForAsync(() => factory.CreateCalls == cappedFlows);

        gate.TrySetResult();
        // The ninth setup runs once a slot frees and its buffered datagram is forwarded.
        await WaitForAsync(() => factory.Transports.Count == cappedFlows + 1);
        await WaitForAsync(() =>
        {
            // Transports is appended under its own lock by CreateAsync; enumerate under it too,
            // or a concurrent append fails the enumeration with "collection was modified".
            lock (factory.Transports)
            {
                return factory.Transports.Exists(transport =>
                {
                    lock (transport.Sent) return transport.Sent.Count == 1 && Assert.Single(transport.Sent[0].Payload) == cappedFlows;
                });
            }
        });
        Assert.DoesNotContain(logger.Events, item => string.Equals(item.Name, "udp.setup.failed", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Events, item => string.Equals(item.Name, "udp.setupqueue.dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetupFlashCrowdOfDistinctFlowsQueuesThroughTheCapWithoutLoss()
    {
        // A flash crowd of first datagrams (the 2026-08-29 soak shape: every flow starts at
        // once) must all be admitted through the 8-wide setup gate: every accepted datagram
        // is forwarded, no setup queue is drained, no tombstone is written. The barrier holds
        // the first eight handshakes until all 64 datagrams have been accepted, proving the
        // burst is genuinely concurrent rather than scheduler luck.
        const int flowCount = 64;
        const int concurrentSetupCap = 8;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BarrierTransportFactory(barrier, concurrentSetupCap);
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = flowCount, Logger = logger });
        var flows = Enumerable.Range(0, flowCount).Select(index => CreateFlow(string.Create(CultureInfo.InvariantCulture, $"198.51.100.{index + 1}"))).ToArray();

        var sends = Enumerable.Range(0, flowCount)
            .Select(index => coordinator.TrySendSpanAsync(flows[index], s_server, [(byte)index], default, CancellationToken.None).AsTask())
            .ToArray();
        // Every first datagram of the crowd is accepted before any handshake completes.
        await factory.GatesHeld.Task.WaitAsync(CancellationToken.None);
        Assert.All(await Task.WhenAll(sends), Assert.True);
        barrier.TrySetResult();

        await WaitForAsync(() => factory.Transports.Count == flowCount, timeoutMs: 30_000);
        await WaitForAsync(() =>
        {
            lock (factory.Transports)
            {
                return factory.Transports.Sum(transport =>
                {
                    lock (transport.Sent) return transport.Sent.Count;
                }) == flowCount;
            }
        }, timeoutMs: 30_000);

        var forwarded = new HashSet<byte>();
        lock (factory.Transports)
        {
            foreach (var transport in factory.Transports)
            {
                lock (transport.Sent)
                {
                    foreach (var (_, payload) in transport.Sent) forwarded.Add(Assert.Single(payload));
                }
            }
        }

        Assert.Equal(flowCount, forwarded.Count);
        Assert.DoesNotContain(logger.Events, item => string.Equals(item.Name, "udp.setupqueue.dropped", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Events, item => string.Equals(item.Name, "udp.setup.failed", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Events, item => string.Equals(item.Name, "udp.setup.cooldown", StringComparison.Ordinal));
    }

    private static async Task<bool> WaitUntilFalseAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (!await condition().ConfigureAwait(false)) return true;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return !await condition().ConfigureAwait(false);
    }

    /// <summary>
    /// A factory whose first <paramref name="gateWidth"/> handshakes hold on a shared barrier
    /// (modeling a slow SOCKS5 ASSOCIATE) so a flash crowd provably queues behind the setup
    /// limiter before any handshake completes.
    /// </summary>
    private sealed class BarrierTransportFactory(TaskCompletionSource barrier, int gateWidth) : IUdpProxyTransportFactory
    {
        private int _nextLocalPort = 42000;
        private int _entered;
        public TaskCompletionSource<bool> GatesHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FakeTransport> Transports { get; } = [];

        public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entered) == gateWidth) GatesHeld.TrySetResult(true);
            await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var transport = new FakeTransport(System.Net.Sockets.AddressFamily.InterNetwork, Interlocked.Increment(ref _nextLocalPort));
            lock (Transports) Transports.Add(transport);
            return transport;
        }
    }
}
