using WinForward.Configuration;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.FlowBuilders;

namespace WinForward.Core.Tests;

/// <summary>
/// R4: per-flow bounds alone allow capacity × 32 KiB of buffered datagrams; this file pins the
/// global byte budget. The datagram that would cross the aggregate is rejected without a cooldown
/// tombstone (backpressure, not a setup failure), and every charged byte plus its pooled lease is
/// credited back on flush, setup failure, dispose, drop-oldest, and the flush TTL drop.
/// </summary>
public sealed class UdpSetupQueueBudgetTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    [Fact]
    public async Task GlobalSetupBudgetRejectsBeyondTheAggregateAndCreditsBackOnFlush()
    {
        // R4: per-flow bounds alone allow capacity × 32 KiB of buffered datagrams; the global
        // byte budget rejects the datagram that would cross the aggregate, without a cooldown
        // tombstone (backpressure, not a setup failure), and every flushed byte is credited
        // back so the budget recovers once the setup completes.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task);
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16, MaximumFrameSize = 4096, SetupQueueGlobalByteBudget = 4096 });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, new byte[3000], default, CancellationToken.None));
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        Assert.Equal(3001, coordinator.Diagnostics.PendingSetupBytes);

        // 3001 + 2000 crosses the 4096-byte aggregate: the new datagram is rejected and counted.
        Assert.False(await coordinator.TrySendSpanAsync(flow, s_server, new byte[2000], default, CancellationToken.None));
        Assert.Equal(1, coordinator.Diagnostics.SetupBudgetRejectionCount);
        Assert.Equal(3001, coordinator.Diagnostics.PendingSetupBytes);

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
        Assert.Equal(0, coordinator.Diagnostics.PendingSetupBytes);
        Assert.True(await coordinator.TrySendSpanAsync(CreateFlow("192.0.2.54"), s_server, "\t"u8, default, CancellationToken.None));
    }

    [Fact]
    public async Task SetupFailureCreditsBackThePendingBudget()
    {
        // A gated, then failed, setup makes the charge observable while parked and the credit
        // observable after the failure teardown drains the queue.
        var factory = new GatedTransportFactory();
        using var pool = new NativeBufferPool(4096, capacity: 8);
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16, MaximumFrameSize = 4096, SetupQueueGlobalByteBudget = 4096 }, setupQueuePool: pool);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, new byte[3000], default, CancellationToken.None));
        await factory.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3000, coordinator.Diagnostics.PendingSetupBytes);
        Assert.Equal(1, pool.Stats.Outstanding);

        // The failure teardown drains the setup queue fail-closed and releases its charge and lease.
        factory.Fail(new IOException("SOCKS5 server is unreachable (synthetic)."));
        await WaitForAsync(() => coordinator.Diagnostics.PendingSetupBytes == 0);
        await WaitForAsync(() => pool.Stats.Outstanding == 0);
        Assert.Equal(pool.Stats.Rented, pool.Stats.Returned);
    }

    [Fact]
    public async Task DisposeCreditsBackDatagramsStillQueuedForSetup()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task);
        using var pool = new NativeBufferPool(4096, capacity: 8);
        var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16, MaximumFrameSize = 4096, SetupQueueGlobalByteBudget = 4096 }, setupQueuePool: pool);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, new byte[3000], default, CancellationToken.None));
        Assert.Equal(3000, coordinator.Diagnostics.PendingSetupBytes);
        Assert.Equal(1, pool.Stats.Outstanding);

        await coordinator.DisposeAsync();
        Assert.Equal(0, coordinator.Diagnostics.PendingSetupBytes);

        // The gate never opens: disposal must complete without waiting for the stalled setup, and
        // the drained datagram's lease must be back in the pool.
        gate.TrySetResult();
        Assert.Equal(0, pool.Stats.Outstanding);
        Assert.Equal(pool.Stats.Rented, pool.Stats.Returned);
    }

    [Fact]
    public async Task SetupQueueLeasesReturnToThePoolAcrossDropOldestFlushAndTtlDrop()
    {
        // B4 balance: every queued datagram holds a lease; drop-oldest eviction, the flush TTL
        // drop, and the flush send must each release their lease exactly once.
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelayedTransportFactory(gate.Task);
        using var pool = new NativeBufferPool(1514, capacity: 64);
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16, TimeProvider = time }, setupQueuePool: pool);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.CreateCalls == 1);
        // 40 datagrams overflow the 32-packet per-flow bound: drop-oldest keeps the freshest.
        for (var index = 0; index < 40; index++)
        {
            Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [(byte)index], default, CancellationToken.None));
        }

        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [0xaa], default, CancellationToken.None));

        gate.TrySetResult();
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });
        lock (transport.Sent) Assert.Equal((byte)0xaa, Assert.Single(transport.Sent[0].Payload));
        // The 31 stale datagrams that survived drop-oldest age out at the flush; only the fresh
        // one is delivered.
        Assert.Equal(31, coordinator.Diagnostics.SetupTtlExpiredCount);
        await WaitForAsync(() => pool.Stats.Outstanding == 0);
        Assert.Equal(pool.Stats.Rented, pool.Stats.Returned);
        Assert.Equal(0, coordinator.Diagnostics.PendingSetupBytes);
    }

    [Fact]
    public async Task SetupQueueLeaseIsReleasedWhenTheDatagramExceedsTheFrameCap()
    {
        // B4 bounds refusal: a datagram larger than the pinned frame cap cannot be copied into a
        // pooled lease; it is rejected fail-closed with its lease and budget charge released.
        using var pool = new NativeBufferPool(64, capacity: 8);
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(new FakeTransportFactory(), new FakeResponseSink(), new UdpProxyOptions { Capacity = 16, MaximumFrameSize = 64 }, setupQueuePool: pool);
        var flow = CreateFlow("192.0.2.53");

        Assert.False(await coordinator.TrySendSpanAsync(flow, s_server, new byte[100], default, CancellationToken.None));

        Assert.Equal(0, coordinator.Diagnostics.PendingSetupBytes);
        Assert.Equal(0, pool.Stats.Outstanding);
        Assert.Equal(pool.Stats.Rented, pool.Stats.Returned);
    }
}
