using System.Diagnostics;
using System.Globalization;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.FlowBuilders;

namespace WinForward.Core.Tests;

/// <summary>
/// R1: the first datagram of a new UDP flow must never drag the capture pump through the SOCKS5
/// setup. These tests pin the bounded setup queue and its entry lifetime: the dispatcher-side
/// send returns during a long stall, buffered datagrams relay in FIFO order, overflow drops the
/// oldest, a send racing the flush cannot overtake the queue, dispose drains without awaiting
/// the stalled setup, and the setup TTL (dial-start age basis: limiter queue-wait is admission
/// delay, not client staleness) ages entries out at the flush — pinned both at the queue level
/// (bulk re-stamp) and the coordinator level (burst shape).
/// </summary>
public sealed class UdpSetupQueueTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

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
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [2], default, CancellationToken.None));
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"TrySendSpanAsync awaited the setup ({stopwatch.Elapsed}).");

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
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Logger = logger });
        var flow = CreateFlow("192.0.2.53");

        // 40 datagrams against a 32-packet queue: the eight oldest are dropped, the newest 32
        // survive, and their FIFO delivery order is preserved.
        const int count = 40;
        const int retained = 32;
        for (var index = 0; index < count; index++)
        {
            Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [(byte)index], default, CancellationToken.None));
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
        Assert.Equal(0, coordinator.Diagnostics.PendingSetupBytes);
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

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        gate.TrySetResult();
        for (var index = 2; index <= 4; index++)
        {
            Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [(byte)index], default, CancellationToken.None));
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
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16, TimeProvider = time });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.CreateCalls == 1);
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [2], default, CancellationToken.None));

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

        Assert.Equal(1, coordinator.Diagnostics.SetupTtlExpiredCount);
        Assert.True(coordinator.Diagnostics.SetupStampsRefreshedCount >= 1);
        Assert.Equal(0, coordinator.Diagnostics.PendingSetupBytes);
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
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = occupants + 8, TimeProvider = time });
        var flows = Enumerable.Range(0, occupants + 1).Select(index => CreateFlow(string.Create(CultureInfo.InvariantCulture, $"192.0.2.{index + 1}"))).ToArray();

        for (var index = 0; index < occupants; index++)
        {
            Assert.True(await coordinator.TrySendSpanAsync(flows[index], s_server, [(byte)index], default, CancellationToken.None));
        }

        // The occupants hold every limiter slot; flow #9's datagram is accepted (buffered)
        // while its setup queues on the limiter.
        await WaitForAsync(() => factory.CreateCalls == occupants);
        Assert.True(await coordinator.TrySendSpanAsync(flows[occupants], s_server, [(byte)occupants], default, CancellationToken.None));

        // 4 s of limiter queue-wait for flow #9 (and 4 s of dial for the occupants, under the
        // TTL). Releasing the occupants lets flow #9's dial start: its queue is re-stamped at
        // that boundary, observable once its CreateAsync is entered.
        time.Advance(TimeSpan.FromSeconds(4));
        occupantGate.TrySetResult();
        await factory.QueuedCreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, CancellationToken.None);

        // The occupants' datagrams flush before the clock moves again (their dial was 4 s).
        await WaitForAsync(() =>
        {
            // Occupant transports can still be appending while this polls: enumerate under the
            // factory lock, or the Sum throws "collection was modified" (observed 2026-09-17).
            lock (factory.Transports)
            {
                return factory.Transports.Sum(transport =>
                {
                    lock (transport.Sent) return transport.Sent.Count;
                }) == occupants;
            }
        }, timeoutMs: 10_000);

        // Flow #9's dial stalls another 2 s: 6 s from enqueue, 2 s from the re-stamp.
        time.Advance(TimeSpan.FromSeconds(2));
        factory.QueuedGate.TrySetResult();

        await WaitForAsync(() =>
        {
            lock (factory.Transports)
            {
                return factory.Transports.Sum(transport =>
                {
                    lock (transport.Sent) return transport.Sent.Count;
                }) == occupants + 1;
            }
        }, timeoutMs: 10_000);

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

        Assert.Equal(occupants + 1, forwarded.Count);
        Assert.Equal(0, coordinator.Diagnostics.SetupTtlExpiredCount);
        Assert.True(coordinator.Diagnostics.SetupStampsRefreshedCount >= 1);
        Assert.Equal(0, coordinator.Diagnostics.PendingSetupBytes);
    }

    [Fact]
    public async Task DisposeDropsDatagramsStillQueuedForSetup()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task);
        var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await coordinator.DisposeAsync();

        // The gate never opens: disposal must complete without waiting for the stalled setup.
        gate.TrySetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await coordinator.TrySendSpanAsync(flow, s_server, [2], default, CancellationToken.None));
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
        using var pool = new NativeBufferPool(64);
        var queue = new BoundedSetupQueue(4, 1024);
        var enqueuedAt = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        var refreshAt = enqueuedAt + TimeSpan.FromSeconds(10);
        Enqueue(queue, pool, [1], enqueuedAt);

        Assert.Equal(1, queue.RefreshEnqueuedStamps(refreshAt));

        Assert.True(queue.TryDequeue(out var lease, out var length, out var stamp));
        Assert.Equal(refreshAt, stamp);
        Assert.Equal(new byte[] { 1 }, lease.Span[..length].ToArray());
        lease.Dispose();
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.Bytes);
    }

    [Fact]
    public void RefreshEnqueuedStampsReStampsEveryEntryAndPreservesFifoOrder()
    {
        // Three entries cross the single-slot fast path into the Queue<>; the refresh must
        // re-stamp all of them while preserving membership, FIFO order, and byte accounting.
        using var pool = new NativeBufferPool(64);
        var queue = new BoundedSetupQueue(8, 1024);
        var baseStamp = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 3; index++)
        {
            Enqueue(queue, pool, [(byte)index], baseStamp + TimeSpan.FromSeconds(index));
        }

        var refreshAt = baseStamp + TimeSpan.FromMinutes(1);
        Assert.Equal(3, queue.RefreshEnqueuedStamps(refreshAt));
        Assert.Equal(3, queue.Count);
        Assert.Equal(3, queue.Bytes);

        for (var index = 0; index < 3; index++)
        {
            Assert.True(queue.TryDequeue(out var lease, out var length, out var stamp));
            Assert.Equal(refreshAt, stamp);
            Assert.Equal((byte)index, Assert.Single(lease.Span[..length].ToArray()));
            lease.Dispose();
        }

        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.Bytes);
    }

    private static void Enqueue(BoundedSetupQueue queue, NativeBufferPool pool, ReadOnlySpan<byte> payload, DateTimeOffset enqueuedAt)
    {
        var lease = pool.Rent();
        payload.CopyTo(lease.Span);
        Assert.True(queue.TryEnqueue(lease, payload.Length, enqueuedAt));
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
