using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.FlowBuilders;

namespace WinForward.Core.Tests;

/// <summary>
/// R4 seam proof: <see cref="UdpSessionSetup"/> is constructible directly against a fake
/// <see cref="IUdpSessionSlotHost"/> — no coordinator — so the dial/claim/construct/flush
/// pipeline is testable through the single slot-access seam. The TTL-drop flush path is pinned
/// here: a datagram whose entry stamp is older than the setup TTL is dropped and its lease
/// released, never sent.
/// </summary>
public sealed class UdpSessionSetupTests
{
    private const int ReceiveBufferSize = 1537;
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    [Fact]
    public async Task FlushDropsTtlExpiredDatagramThroughTheSlotHostSeam()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var flow = CreateFlow("192.0.2.53");
        var factory = new FakeTransportFactory();
        using var receiveWindowPool = new NativeBufferPool(ReceiveBufferSize, capacity: 4);
        using var setupQueuePool = new NativeBufferPool(64, capacity: 4);
        var host = new TtlExpiredDequeueHost(setupQueuePool, time.GetUtcNow() - TimeSpan.FromSeconds(6));
        using var shutdown = new CancellationTokenSource();
        var setup = new UdpSessionSetup(factory, new UdpAssociationTable(), new FakeResponseSink(), time, NullRuntimeLogger.Instance, receiveWindowPool, ReceiveBufferSize, host);
        var slot = new UdpProxyCoordinator.UdpSessionSlot();

        await setup.CreateSessionAsync(flow, s_server, flowGeneration: 1, MacAddress.Invalid, slot, shutdown.Token);

        // The first dequeue step delivered an over-TTL entry: it was dropped without a send, its
        // lease returned to the pool, and the flush then stopped at the not-owner step.
        Assert.Equal(1, setup.TtlExpiredCount);
        Assert.Equal(2, host.DequeueCalls);
        var transport = Assert.Single(factory.Transports);
        lock (transport.Sent) Assert.Empty(transport.Sent);
        Assert.Equal(setupQueuePool.Stats.Rented, setupQueuePool.Stats.Returned);
        Assert.Equal(0, setupQueuePool.Stats.Outstanding);

        // The fake host observed the constructed session through the opaque slot handle.
        var session = Assert.IsType<UdpProxySession>(host.AttachedSession);
        Assert.Same(session, slot.Session);
        await shutdown.CancelAsync();
        await session.DisposeAsync();
        setup.DisposeLimiter();
    }

    /// <summary>
    /// A slot host whose first flush-dequeue step hands back an entry older than the setup TTL
    /// (rented from the setup queue pool), then reports not-owner so the flush stops — exactly
    /// the coordinator-side behavior the TTL-drop path needs, without a coordinator.
    /// </summary>
    private sealed class TtlExpiredDequeueHost(NativeBufferPool setupQueuePool, DateTimeOffset enqueuedAt) : IUdpSessionSlotHost
    {
        private bool _dequeued;

        public int DequeueCalls;
        public UdpProxySession? AttachedSession;

        public void AttachSession(UdpProxyCoordinator.UdpSessionSlot slot, UdpProxySession session)
        {
            AttachedSession = session;
            slot.Session = session;
        }

        public int RefreshSetupStamps(FlowKey flow, UdpProxyCoordinator.UdpSessionSlot slot) => 0;

        public (UdpSessionSetup.FlushStep Step, NativeLease Lease, int Length, DateTimeOffset EnqueuedAt) DequeueForFlush(FlowKey flow, UdpProxyCoordinator.UdpSessionSlot slot)
        {
            DequeueCalls++;
            if (_dequeued) return (UdpSessionSetup.FlushStep.NotOwner, default, 0, default);
            _dequeued = true;
            var lease = setupQueuePool.Rent();
            lease.Span[0] = 0xaa;
            return (UdpSessionSetup.FlushStep.Dequeued, lease, 1, enqueuedAt);
        }

        public Task<bool> RemoveSlotAsync(FlowKey flow, UdpProxyCoordinator.UdpSessionSlot slot, bool armCooldown) => Task.FromResult(true);

        public Task RemoveReceiveFailedSessionAsync(UdpProxySession session) => Task.CompletedTask;
    }
}
