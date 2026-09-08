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
/// cooldown tombstone, and the concurrent-setup cap. The setup TTL's dial-start age basis —
/// limiter queue-wait is admission delay, not client staleness — is pinned at both the queue
/// level (bulk re-stamp) and the coordinator level (burst shape).
/// </summary>
public sealed class UdpSetupQueueTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);

    [Fact]
    public async Task FirstDatagramDoesNotAwaitAStalledSetupAndDatagramsRelayInFifoOrder()
    {
        // The factory's UDP ASSOCIATE stalls for multiple seconds; the dispatcher-side send must
        // return long before that stall can elapse. The stall stays under the setup datagram TTL
        // (5 s): a setup stalled beyond it legitimately drops its buffered datagrams by contract.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task, TimeSpan.FromSeconds(2));
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

        // The drop-oldest evictions and the flush deliveries each credited their charge: the
        // aggregate returns to zero once the queue is fully drained.
        Assert.Equal(0, coordinator.PendingSetupBytesForDiagnostics);
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
    public async Task SetupCooldownsAreBoundedAndEvictTheOldestAtCapacity()
    {
        // R3-UDP: the cooldown dictionary is bounded by the session capacity; at capacity the
        // oldest retry deadline is evicted, so a failing-server storm cannot grow it without
        // bound while every recent flow keeps its cooldown (eviction, never refusal).
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FailingTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), 4, time, null);
        const int flowCount = 5;
        var flows = Enumerable.Range(0, flowCount).Select(index => CreateFlow($"192.0.2.{index + 1}")).ToArray();

        for (var index = 0; index < flowCount; index++)
        {
            Assert.True(await coordinator.TrySendAsync(flows[index], s_server, new byte[] { 1 }, CancellationToken.None));
            // The failure surfaces through the background task: wait for the flow's cooldown to
            // arm, then advance the clock so each flow earns a distinct retry deadline.
            Assert.True(await WaitUntilFalseAsync(() => coordinator.TrySendAsync(flows[index], s_server, new byte[] { 2 }, CancellationToken.None).AsTask()));
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        // Bounded at capacity: the first flow's cooldown entry (the oldest deadline) was evicted.
        Assert.Equal(4, coordinator.SetupCooldownCountForDiagnostics);

        // Every surviving cooldown entry still cools down its flow at the frozen clock...
        for (var index = 1; index < flowCount; index++)
        {
            Assert.False(await coordinator.TrySendAsync(flows[index], s_server, new byte[] { 3 }, CancellationToken.None));
        }

        // ...while the evicted flow retries immediately instead of being cooldown-rejected.
        Assert.True(await coordinator.TrySendAsync(flows[0], s_server, new byte[] { 3 }, CancellationToken.None));
        await WaitForAsync(() => factory.CreateCalls == flowCount + 1);
        Assert.Equal(4, coordinator.SetupCooldownCountForDiagnostics);
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
    public async Task GlobalSetupBudgetRejectsBeyondTheAggregateAndCreditsBackOnFlush()
    {
        // R4: per-flow bounds alone allow capacity × 32 KiB of buffered datagrams; the global
        // byte budget rejects the datagram that would cross the aggregate, without a cooldown
        // tombstone (backpressure, not a setup failure), and every flushed byte is credited
        // back so the budget recovers once the setup completes.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task);
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), 16, TimeProvider.System, null, setupQueueGlobalByteBudget: 4096);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[3000], CancellationToken.None));
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        Assert.Equal(3001, coordinator.PendingSetupBytesForDiagnostics);

        // 3001 + 2000 crosses the 4096-byte aggregate: the new datagram is rejected and counted.
        Assert.False(await coordinator.TrySendAsync(flow, s_server, new byte[2000], CancellationToken.None));
        Assert.Equal(1, coordinator.SetupBudgetRejectionCount);
        Assert.Equal(3001, coordinator.PendingSetupBytesForDiagnostics);

        gate.TrySetResult();
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 2;
        });
        lock (transport.Sent)
        {
            Assert.Equal(3000, transport.Sent[0].Payload.Length);
            Assert.Equal(new byte[] { 1 }, transport.Sent[1].Payload);
        }

        // The flush credited both charges back: admission recovers for a brand-new flow.
        Assert.Equal(0, coordinator.PendingSetupBytesForDiagnostics);
        Assert.True(await coordinator.TrySendAsync(CreateFlow("192.0.2.54"), s_server, new byte[] { 9 }, CancellationToken.None));
    }

    [Fact]
    public async Task SetupFailureCreditsBackThePendingBudget()
    {
        // A gated, then failed, setup makes the charge observable while parked and the credit
        // observable after the failure teardown drains the queue.
        var factory = new GatedTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), 16, TimeProvider.System, null, setupQueueGlobalByteBudget: 4096);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[3000], CancellationToken.None));
        await factory.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3000, coordinator.PendingSetupBytesForDiagnostics);

        // The failure teardown drains the setup queue fail-closed and releases its charge.
        factory.Fail(new IOException("SOCKS5 server is unreachable (synthetic)."));
        await WaitForAsync(() => coordinator.PendingSetupBytesForDiagnostics == 0);
    }

    [Fact]
    public async Task DisposeCreditsBackDatagramsStillQueuedForSetup()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task);
        var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), 16, TimeProvider.System, null, setupQueueGlobalByteBudget: 4096);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[3000], CancellationToken.None));
        Assert.Equal(3000, coordinator.PendingSetupBytesForDiagnostics);

        await coordinator.DisposeAsync();
        Assert.Equal(0, coordinator.PendingSetupBytesForDiagnostics);

        // The gate never opens: disposal must complete without waiting for the stalled setup.
        gate.TrySetResult();
    }

    [Fact]
    public async Task FlushDropsSetupDatagramsOlderThanTheTtl()
    {
        // R4 TTL: a datagram still buffered after the setup window has long been retransmitted
        // or abandoned at the application layer — the flush delivers only fresh state. The
        // window runs from the dial start (the re-stamp applied when the setup leaves the
        // limiter), so the test pins the dial boundary first (CreateAsync entered) before
        // stalling the fake clock: with the limiter free the re-stamp lands at the enqueue,
        // the 6 s dial stall then ages the first datagram out while the second, queued after
        // the stall, stays fresh.
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task);
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), 16, time, null);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        await WaitForAsync(() => factory.CreateCalls == 1);
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 2 }, CancellationToken.None));

        gate.TrySetResult();
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });
        lock (transport.Sent)
        {
            Assert.Equal((byte)2, Assert.Single(transport.Sent[0].Payload));
        }

        Assert.Equal(1, coordinator.SetupTtlExpiredCount);
        Assert.True(coordinator.SetupStampsRefreshedCount >= 1);
        Assert.Equal(0, coordinator.PendingSetupBytesForDiagnostics);
    }

    [Fact]
    public async Task LimiterQueueWaitDoesNotExpireTheTriggeringDatagram()
    {
        // TTL re-attribution at dial start: a datagram's staleness must not accrue while its
        // flow's setup waits on the 8-wide setup limiter (2026-09-06 burst baseline: wave k's
        // triggering datagram waited (k−1)×dial on the limiter and the enqueue-stamp TTL
        // dropped it). Flow #9 queues 4 s behind the occupants, then its dial stalls another
        // 2 s: 6 s total from enqueue — past the 5 s TTL under the enqueue-stamp semantics —
        // but only the 2 s dial age survives the re-stamp, so the datagram is delivered and
        // nothing expires. The occupants' own dials (4 s) stay under the TTL, so all nine
        // datagrams deliver.
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var occupantGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int occupants = 8;
        var factory = new StagedGateTransportFactory(occupantGate, occupants);
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), occupants + 8, time, null);
        var flows = Enumerable.Range(0, occupants + 1).Select(index => CreateFlow($"192.0.2.{index + 1}")).ToArray();

        for (var index = 0; index < occupants; index++)
        {
            Assert.True(await coordinator.TrySendAsync(flows[index], s_server, new[] { (byte)index }, CancellationToken.None));
        }

        // The occupants hold every limiter slot; flow #9's datagram is accepted (buffered)
        // while its setup queues on the limiter.
        await WaitForAsync(() => factory.CreateCalls == occupants);
        Assert.True(await coordinator.TrySendAsync(flows[occupants], s_server, new[] { (byte)occupants }, CancellationToken.None));

        // 4 s of limiter queue-wait for flow #9 (and 4 s of dial for the occupants, under the
        // TTL). Releasing the occupants lets flow #9's dial start: its queue is re-stamped at
        // that boundary, observable once its CreateAsync is entered.
        time.Advance(TimeSpan.FromSeconds(4));
        occupantGate.TrySetResult();
        await factory.QueuedCreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The occupants' datagrams flush before the clock moves again (their dial was 4 s).
        await WaitForAsync(() => factory.Transports.Sum(transport =>
        {
            lock (transport.Sent) return transport.Sent.Count;
        }) == occupants, timeoutMs: 10_000);

        // Flow #9's dial stalls another 2 s: 6 s from enqueue, 2 s from the re-stamp.
        time.Advance(TimeSpan.FromSeconds(2));
        factory.QueuedGate.TrySetResult();

        await WaitForAsync(() =>
        {
            return factory.Transports.Sum(transport =>
            {
                lock (transport.Sent) return transport.Sent.Count;
            }) == occupants + 1;
        }, timeoutMs: 10_000);

        var forwarded = new HashSet<byte>();
        foreach (var transport in factory.Transports)
        {
            lock (transport.Sent)
            {
                foreach (var sent in transport.Sent) forwarded.Add(Assert.Single(sent.Payload));
            }
        }

        Assert.Equal(occupants + 1, forwarded.Count);
        Assert.Equal(0, coordinator.SetupTtlExpiredCount);
        Assert.True(coordinator.SetupStampsRefreshedCount >= 1);
        Assert.Equal(0, coordinator.PendingSetupBytesForDiagnostics);
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

    [Fact]
    public void RefreshEnqueuedStampsReturnsZeroOnAnEmptyQueue()
    {
        var queue = new BoundedSetupQueue(4, 1024);
        var refreshAt = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(0, queue.RefreshEnqueuedStamps(refreshAt));
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.Bytes);
    }

    [Fact]
    public void RefreshEnqueuedStampsReStampsTheSinglePendingEntry()
    {
        var queue = new BoundedSetupQueue(4, 1024);
        var enqueuedAt = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        var refreshAt = enqueuedAt + TimeSpan.FromSeconds(10);
        Assert.True(queue.TryEnqueue(new byte[] { 1 }, enqueuedAt));

        Assert.Equal(1, queue.RefreshEnqueuedStamps(refreshAt));

        Assert.True(queue.TryDequeue(out var frame, out var stamp));
        Assert.Equal(refreshAt, stamp);
        Assert.Equal(new byte[] { 1 }, frame.ToArray());
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.Bytes);
    }

    [Fact]
    public void RefreshEnqueuedStampsReStampsEveryEntryAndPreservesFifoOrder()
    {
        // Three entries cross the single-slot fast path into the Queue<>; the refresh must
        // re-stamp all of them while preserving membership, FIFO order, and byte accounting.
        var queue = new BoundedSetupQueue(8, 1024);
        var baseStamp = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 3; index++)
        {
            Assert.True(queue.TryEnqueue(new[] { (byte)index }, baseStamp + TimeSpan.FromSeconds(index)));
        }

        var refreshAt = baseStamp + TimeSpan.FromMinutes(1);
        Assert.Equal(3, queue.RefreshEnqueuedStamps(refreshAt));
        Assert.Equal(3, queue.Count);
        Assert.Equal(3, queue.Bytes);

        for (var index = 0; index < 3; index++)
        {
            Assert.True(queue.TryDequeue(out var frame, out var stamp));
            Assert.Equal(refreshAt, stamp);
            Assert.Equal((byte)index, Assert.Single(frame.ToArray()));
        }

        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.Bytes);
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

    /// <summary>
    /// A factory whose first <paramref name="gateWidth"/> handshakes hold on a shared gate
    /// (occupying every setup-limiter slot) while every later handshake first signals its
    /// entry, then holds on a second gate — so a queued flow's dial start is observable
    /// before its dial completes, and the two populations can be stalled for different
    /// fake-clock durations.
    /// </summary>
    private sealed class StagedGateTransportFactory(TaskCompletionSource occupantGate, int gateWidth) : IUdpProxyTransportFactory
    {
        private int _nextLocalPort = 43000;
        private int _entered;
        public TaskCompletionSource<bool> QueuedCreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource QueuedGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FakeTransport> Transports { get; } = [];

        public int CreateCalls => Volatile.Read(ref _entered);

        public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entered) <= gateWidth)
            {
                await occupantGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                QueuedCreateStarted.TrySetResult(true);
                await QueuedGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var transport = new FakeTransport(System.Net.Sockets.AddressFamily.InterNetwork, Interlocked.Increment(ref _nextLocalPort));
            lock (Transports) Transports.Add(transport);
            return transport;
        }
    }
}
