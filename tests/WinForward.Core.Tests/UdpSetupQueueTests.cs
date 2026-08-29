using System.Diagnostics;
using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;

namespace WinForward.Core.Tests;

/// <summary>
/// R1: the first datagram of a new UDP flow must never drag the capture pump through the SOCKS5
/// setup. These tests pin the non-blocking setup contract: prompt dispatcher return during a
/// long stall, FIFO delivery of buffered datagrams, drop-oldest overflow, the setup-failure
/// cooldown tombstone, and the concurrent-setup cap.
/// </summary>
public sealed class UdpSetupQueueTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);

    [Fact]
    public async Task FirstDatagramDoesNotAwaitAStalledSetupAndDatagramsRelayInFifoOrder()
    {
        // N >= 5s per AC1: the factory's UDP ASSOCIATE stalls for at least five seconds; the
        // dispatcher-side send must return long before that stall can elapse.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task, TimeSpan.FromSeconds(5));
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var flow = CreateFlow("192.0.2.53");

        var stopwatch = Stopwatch.StartNew();
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 2 }, CancellationToken.None));
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"TrySendAsync awaited the setup ({stopwatch.Elapsed}).");

        gate.TrySetResult();
        await WaitForAsync(() => factory.Transports.Count == 1, timeoutMs: 10_000);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 2;
        }, timeoutMs: 10_000);
        lock (transport.Sent)
        {
            Assert.Equal(new byte[] { 1 }, transport.Sent[0].Payload);
            Assert.Equal(new byte[] { 2 }, transport.Sent[1].Payload);
        }
    }

    [Fact]
    public async Task SetupQueueOverflowDropsOldestAndDeliversRetainedDatagramsInFifoOrder()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task);
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), logger: logger);
        var flow = CreateFlow("192.0.2.53");

        // 40 datagrams against a 32-packet queue: the eight oldest are dropped, the newest 32
        // survive, and their FIFO delivery order is preserved.
        const int count = 40;
        const int retained = 32;
        for (var index = 0; index < count; index++)
        {
            Assert.True(await coordinator.TrySendAsync(flow, s_server, new[] { (byte)index }, CancellationToken.None));
        }

        gate.TrySetResult();
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == retained;
        });
        lock (transport.Sent)
        {
            for (var index = 0; index < retained; index++)
            {
                Assert.Equal(index + (count - retained), Assert.Single(transport.Sent[index].Payload));
            }
        }

        Assert.Contains(logger.Events, item => string.Equals(item.Name, "udp.setupqueue.dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DatagramsCannotBypassTheSetupQueueAroundTheFlush()
    {
        // A send racing the flush boundary must never overtake an older queued datagram: the
        // slot's ready transition and the queue-empty check share one critical section.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task);
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        gate.TrySetResult();
        for (var index = 2; index <= 4; index++)
        {
            Assert.True(await coordinator.TrySendAsync(flow, s_server, new[] { (byte)index }, CancellationToken.None));
        }

        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 4;
        });
        lock (transport.Sent)
        {
            for (var index = 0; index < 4; index++)
            {
                Assert.Equal(index + 1, Assert.Single(transport.Sent[index].Payload));
            }
        }
    }

    [Fact]
    public async Task FailedSetupEntersCooldownAndRetriesAfterOneSecond()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FailingTransportFactory();
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), 16, time, null, logger);
        var flow = CreateFlow("192.0.2.53");

        // Accepted (buffered); the failure itself surfaces through the background setup task.
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));

        // Frozen fake time keeps the tombstone active: once the failure is processed, every
        // datagram is rejected fail-closed for the cooldown window.
        Assert.True(await WaitUntilFalseAsync(() => coordinator.TrySendAsync(flow, s_server, new byte[] { 2 }, CancellationToken.None).AsTask()));
        Assert.Equal(1, factory.CreateCalls);
        Assert.Contains(logger.Events, item => string.Equals(item.Name, "udp.setup.cooldown", StringComparison.Ordinal));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 3 }, CancellationToken.None));
        await WaitForAsync(() => factory.CreateCalls == 2);
    }

    [Fact]
    public async Task SetupConcurrencyCapQueuesFlowsBeyondTheCapUntilASlotFrees()
    {
        // Patient admission: the 8-wide setup cap must queue the ninth flow's setup, not fail
        // it into the cooldown tombstone (whose teardown drops the buffered datagram).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task);
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), 16, TimeProvider.System, null, logger);
        const int cappedFlows = 8;
        var flows = Enumerable.Range(0, cappedFlows + 1).Select(index => CreateFlow($"192.0.2.{index + 1}")).ToArray();

        for (var index = 0; index <= cappedFlows; index++)
        {
            Assert.True(await coordinator.TrySendAsync(flows[index], s_server, new[] { (byte)index }, CancellationToken.None));
        }

        // The first eight setups occupy the limiter; the ninth setup is queued on it.
        await WaitForAsync(() => factory.CreateCalls == cappedFlows);

        gate.TrySetResult();
        // The ninth setup runs once a slot frees and its buffered datagram is forwarded.
        await WaitForAsync(() => factory.Transports.Count == cappedFlows + 1);
        await WaitForAsync(() =>
        {
            return factory.Transports.Any(transport =>
            {
                lock (transport.Sent) return transport.Sent.Count == 1 && Assert.Single(transport.Sent[0].Payload) == cappedFlows;
            });
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
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), flowCount, TimeProvider.System, null, logger);
        var flows = Enumerable.Range(0, flowCount).Select(index => CreateFlow($"198.51.100.{index + 1}")).ToArray();

        var sends = Enumerable.Range(0, flowCount)
            .Select(index => coordinator.TrySendAsync(flows[index], s_server, new[] { (byte)index }, CancellationToken.None).AsTask())
            .ToArray();
        // Every first datagram of the crowd is accepted before any handshake completes.
        await factory.GatesHeld.Task.WaitAsync(CancellationToken.None);
        Assert.All(await Task.WhenAll(sends), Assert.True);
        barrier.TrySetResult();

        await WaitForAsync(() => factory.Transports.Count == flowCount, timeoutMs: 30_000);
        await WaitForAsync(() =>
        {
            return factory.Transports.Sum(transport =>
            {
                lock (transport.Sent) return transport.Sent.Count;
            }) == flowCount;
        }, timeoutMs: 30_000);

        var forwarded = new HashSet<byte>();
        foreach (var transport in factory.Transports)
        {
            lock (transport.Sent)
            {
                foreach (var sent in transport.Sent) forwarded.Add(Assert.Single(sent.Payload));
            }
        }

        Assert.Equal(flowCount, forwarded.Count);
        Assert.DoesNotContain(logger.Events, item => string.Equals(item.Name, "udp.setupqueue.dropped", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Events, item => string.Equals(item.Name, "udp.setup.failed", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Events, item => string.Equals(item.Name, "udp.setup.cooldown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeDropsDatagramsStillQueuedForSetup()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task);
        var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        await coordinator.DisposeAsync();

        // The gate never opens: disposal must complete without waiting for the stalled setup.
        gate.TrySetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await coordinator.TrySendAsync(flow, s_server, new byte[] { 2 }, CancellationToken.None));
        Assert.Empty(factory.Transports);
    }

    private static FlowKey CreateFlow(string remoteAddress) =>
        FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse(remoteAddress), 53), TransportProtocol.Udp, FlowOriginKind.Host);

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
