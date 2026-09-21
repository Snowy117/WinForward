using System.Net;
using System.Reflection;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.FlowBuilders;

namespace WinForward.Core.Tests;

public sealed class UdpProxyCoordinatorLifecycleTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    private static async Task WaitForReadyAsync(FakeTransport transport, int sentCount)
    {
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count >= sentCount;
        });
    }

    [Fact]
    public async Task RemoveExpiredDisposesIdleSessionAndReleasesAssociation()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink());
        var flow = CreateFlow("192.0.2.53");
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForReadyAsync(transport, 1);

        // The session's last activity is now; a sweep far in the future must expire it.
        var removed = await coordinator.RemoveExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1));

        Assert.Equal(1, removed);
        Assert.True(transport.IsDisposed);
        // A subsequent send creates a fresh session (the old association was released).
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [2], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 2);
        Assert.Equal(2, factory.Transports.Count);
    }

    [Fact]
    public async Task RemoveExpiredKeepsActiveSession()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink());
        var flow = CreateFlow("192.0.2.53");
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);

        // A sweep with a timeout far beyond the session's age must not expire it.
        var removed = await coordinator.RemoveExpiredAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));

        Assert.Equal(0, removed);
        Assert.Single(factory.Transports);
    }

    [Fact]
    public async Task FailedSetupReleasesSlotAndCapacityForOtherFlows()
    {
        var factory = new GatedTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 1 });
        var first = CreateFlow("192.0.2.53");
        var second = CreateFlow("192.0.2.54");

        // The datagram is accepted (buffered); the setup itself runs in the background.
        Assert.True(await coordinator.TrySendSpanAsync(first, s_server, [1], default, CancellationToken.None));
        await factory.CreateStarted.Task.WaitAsync(CancellationToken.None);
        factory.Fail(new IOException("setup failed after the caller left"));
        await factory.CreateFinished.Task.WaitAsync(CancellationToken.None);

        // The failed slot is removed, so the second flow fits inside the capacity of one.
        Assert.True(await WaitUntilTrueAsync(() => coordinator.TrySendSpanAsync(second, s_server, [2], default, CancellationToken.None).AsTask()));
        await WaitForAsync(() => factory.CreatedTransports.Count == 1);
        Assert.Single(factory.CreatedTransports);
    }

    [Fact]
    public async Task ReceiveFaultDisposesAndRemovesSessionWithoutAnotherSend()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 1 });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForReadyAsync(transport, 1);
        transport.Received.Writer.TryComplete(new IOException("relay read failed"));

        await WaitForAsync(() => transport.IsDisposed);
        Assert.True(await WaitUntilTrueAsync(() => coordinator.TrySendSpanAsync(CreateFlow("192.0.2.54"), s_server, [2], default, CancellationToken.None).AsTask()));
        await WaitForAsync(() => factory.Transports.Count == 2);
        Assert.Equal(2, factory.Transports.Count);
    }

    [Fact]
    public async Task ImmediateReceiveFaultRemovesSessionAfterCoordinatorRegistration()
    {
        var factory = new ImmediateFaultTransportFactory();
        using var pool = new NativeBufferPool(1537);
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            factory,
            new FakeResponseSink(),
            new UdpProxyOptions { Capacity = 1 },
            receiveWindowPool: pool);
        var flow = CreateFlow("192.0.2.53");

        // The datagram is accepted and buffered; the receive fault surfaces through the
        // background setup task's failure path instead of the dispatcher's await.
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));

        await WaitForAsync(() => factory.FaultedTransports is [{ IsDisposed: true }]);
        Assert.Equal(1, pool.Stats.Returned);
        Assert.Equal(0, pool.Stats.Outstanding);
        Assert.True(await WaitUntilTrueAsync(() => coordinator.TrySendSpanAsync(CreateFlow("192.0.2.54"), s_server, [2], default, CancellationToken.None).AsTask()));
    }

    [Fact]
    public async Task ReceiveFailureTeardownInFlightAcrossDisposalIsStillJoined()
    {
        // D-C3-8/F1: the receive-failure teardown runs as a coordinator-scope child (Run), so a
        // disposal that begins while it is mid-flight seals after admitting it and joins it in the
        // drain. Gating the session transport's disposal parks the teardown, so the pending
        // coordinator dispose is the discriminator: without the scope join it would complete early.
        var factory = new FakeTransportFactory();
        var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 1 });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForReadyAsync(transport, 1);

        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.DisposeGate = disposeGate;
        transport.Received.Writer.TryComplete(new IOException("relay read failed"));

        // The teardown child removed the slot and is now parked in the session's transport
        // disposal; the session's own receive loop already signalled and returned (if the signal
        // were awaited inline, the loop would deadlock behind this teardown and the join below).
        await transport.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = coordinator.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);

        disposeGate.SetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transport.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await coordinator.TrySendSpanAsync(CreateFlow("192.0.2.54"), s_server, [2], default, CancellationToken.None));

        // The fire-and-forget teardown list is gone by construction: the scope is the only tracker.
        Assert.Null(typeof(UdpProxyCoordinator).GetField("_inFlightTeardowns", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(UdpProxyCoordinator).GetMethod("DrainInFlightTeardownsAsync", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public async Task FaultingReceiveFailureTeardownLogsTheWarningAndNeverEscapes()
    {
        // D-C3-9/D4: Run records and swallows a teardown fault, so the body itself must log the
        // owner's domain-specific warning — and the swallowed fault must not surface as an
        // unobserved task exception nor fail the coordinator's own disposal.
        var logger = new RecordingRuntimeLogger();
        var factory = new FakeTransportFactory();
        var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            factory,
            new FakeResponseSink(),
            new UdpProxyOptions { Capacity = 1, Logger = logger });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForReadyAsync(transport, 1);

        var teardownFault = new IOException("transport dispose failed");
        var probe = new UnobservedExceptionProbe();
        TaskScheduler.UnobservedTaskException += probe.OnUnobserved;
        try
        {
            probe.Track(teardownFault);
            transport.DisposeFault = teardownFault;
            transport.Received.Writer.TryComplete(new IOException("relay read failed"));

            await WaitForAsync(() => logger.WarnCount >= 1);
            Assert.Contains(logger.Lines, line => line.Level == RuntimeLogLevel.Warn && line.Message.Contains("receive-failure teardown faulted", StringComparison.Ordinal));

            UnobservedExceptionProbe.ForceFinalization();
            Assert.Equal(0, probe.Count);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= probe.OnUnobserved;
        }

        // The fault is a child fault, so the owner's disposal still completes without throwing.
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CoordinatorDisposalPreservesSetupCancellation()
    {
        var factory = new CancellationAwareTransportFactory();
        var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink());
        Assert.True(await coordinator.TrySendSpanAsync(CreateFlow("192.0.2.53"), s_server, [1], default, CancellationToken.None));
        await factory.CreateStarted.Task.WaitAsync(CancellationToken.None);

        // Disposal cancels the pending setup and completes without hanging on it.
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ConcurrentDisposalIsSingleFlightAndRejectsNewSends()
    {
        var factory = new FakeTransportFactory();
        var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink());

        var firstDispose = DisposeCoordinatorAsync(coordinator);
        var secondDispose = DisposeCoordinatorAsync(coordinator);
        await Task.WhenAll(firstDispose, secondDispose);

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await coordinator.TrySendSpanAsync(CreateFlow("192.0.2.53"), s_server, [1], default, CancellationToken.None));
        Assert.Empty(factory.Transports);
    }

    [Fact]
    public async Task SuccessfulSendRefreshesAssociationAndAvoidsStaleExpiry()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 1, TimeProvider = time });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForReadyAsync(transport, 1);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [2], default, CancellationToken.None));
        await WaitForReadyAsync(transport, 2);

        Assert.Equal(0, await coordinator.RemoveExpiredAsync(time.GetUtcNow(), TimeSpan.FromMinutes(1)));
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [3], default, CancellationToken.None));
        await WaitForReadyAsync(transport, 3);
        Assert.Single(factory.Transports);
    }

    [Fact]
    public async Task ActiveSessionRetainsRelayAliasAfterItsCreationTimestampExpires()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new CollidingAliasTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 2, TimeProvider = time });
        var first = CreateFlow("192.0.2.53");
        var second = CreateFlow("192.0.2.54");

        Assert.True(await coordinator.TrySendSpanAsync(first, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        // Wait for the flush, not just the transport: the session slot only becomes Ready (and
        // refreshable by the second send) once the first datagram is forwarded. Advancing the
        // clock before that expires the not-yet-ready slot by its creation timestamp instead.
        await WaitForReadyAsync(Assert.Single(factory.Transports), 1);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await coordinator.TrySendSpanAsync(first, s_server, [2], default, CancellationToken.None));
        Assert.Equal(0, await coordinator.RemoveExpiredAsync(time.GetUtcNow(), TimeSpan.FromMinutes(1)));

        // The second flow collides on the relay alias; its background setup fails and the flow
        // enters the cooldown tombstone (frozen fake time keeps it active deterministically).
        Assert.True(await coordinator.TrySendSpanAsync(second, s_server, [3], default, CancellationToken.None));
        Assert.True(await WaitUntilTrueAsync(async () => !await coordinator.TrySendSpanAsync(second, s_server, [4], default, CancellationToken.None)));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await coordinator.TrySendSpanAsync(second, s_server, [5], default, CancellationToken.None));
    }

    [Fact]
    public void RetiredAssociationCannotRemoveOrRefreshReplacementForSameFlow()
    {
        var flow = CreateFlow("192.0.2.53");
        var firstAlias = new RelayAlias(FlowKey.Create(
            Endpoint.From(IPAddress.Loopback, 40000),
            Endpoint.From(IPAddress.Loopback, 50000),
            TransportProtocol.Udp,
            FlowOriginKind.Host));
        var replacementAlias = new RelayAlias(FlowKey.Create(
            Endpoint.From(IPAddress.Loopback, 40001),
            Endpoint.From(IPAddress.Loopback, 50000),
            TransportProtocol.Udp,
            FlowOriginKind.Host));
        var table = new UdpAssociationTable();
        var created = DateTimeOffset.UnixEpoch;

        Assert.True(table.TryClaim(flow, firstAlias, created, out var retired));
        Assert.NotNull(retired);
        Assert.True(table.TryRemove(retired));
        Assert.True(table.TryClaim(flow, replacementAlias, created.AddMinutes(1), out var replacement));
        Assert.NotNull(replacement);

        Assert.False(table.TryTouch(retired, created.AddMinutes(2)));
        Assert.False(table.TryRemove(retired));
        Assert.True(table.TryFindOriginal(flow, created.AddMinutes(3), out var current));
        Assert.Same(replacement, current);
    }

    [Fact]
    public async Task ExpirySnapshotDoesNotDisposeSessionWhoseSendRefreshesActivity()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeTransportFactory();
        TaskCompletionSource<bool>? snapshotTaken = null;
        TaskCompletionSource<bool>? resumeSweep = null;
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            factory,
            new FakeResponseSink(),
            new UdpProxyOptions
            {
                Capacity = 1,
                TimeProvider = time,
                BeforeExpiryRecheck = () =>
                {
                    // ReSharper disable once AccessToModifiedClosure // Two-phase test wiring: this completion source is assigned after construction, and the recheck seam only runs from RemoveExpiredAsync, which the test calls after that assignment.
                    snapshotTaken!.TrySetResult(true);
                    // ReSharper disable once AccessToModifiedClosure // resumeSweep is assigned before the first sweep and gates the seam's returned task; the test completes it only after observing snapshotTaken.
                    return new ValueTask(resumeSweep!.Task);
                },
            });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForReadyAsync(transport, 1);
        time.Advance(TimeSpan.FromMinutes(2));

        snapshotTaken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        resumeSweep = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweep = coordinator.RemoveExpiredAsync(time.GetUtcNow(), TimeSpan.FromMinutes(1)).AsTask();
        await snapshotTaken.Task.WaitAsync(CancellationToken.None);
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [2], default, CancellationToken.None));
        resumeSweep.TrySetResult(true);

        Assert.Equal(0, await sweep);
        Assert.False(Assert.Single(factory.Transports).IsDisposed);
    }

    [Fact]
    public async Task ExpirySnapshotDoesNotDisposeSessionWhoseReceiveRefreshesActivity()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        TaskCompletionSource<bool>? snapshotTaken = null;
        TaskCompletionSource<bool>? resumeSweep = null;
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            factory,
            sink,
            new UdpProxyOptions
            {
                Capacity = 1,
                TimeProvider = time,
                BeforeExpiryRecheck = () =>
                {
                    // ReSharper disable once AccessToModifiedClosure // Same two-phase wiring as the send test above: the assignment happens before RemoveExpiredAsync, the only path that runs the recheck seam.
                    snapshotTaken!.TrySetResult(true);
                    // ReSharper disable once AccessToModifiedClosure // resumeSweep gates the seam's awaited task and is completed after the test observes snapshotTaken, before the sweep can proceed.
                    return new ValueTask(resumeSweep!.Task);
                },
            });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForReadyAsync(transport, 1);
        time.Advance(TimeSpan.FromMinutes(2));

        snapshotTaken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        resumeSweep = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweep = coordinator.RemoveExpiredAsync(time.GetUtcNow(), TimeSpan.FromMinutes(1)).AsTask();
        await snapshotTaken.Task.WaitAsync(CancellationToken.None);
        transport.EnqueueResponse(new Socks5UdpDatagram(IPAddress.Parse("192.0.2.53"), DestinationDomain: null, 53, new byte[] { 2 }));
        _ = await sink.Responses.Reader.ReadAsync(CancellationToken.None);
        resumeSweep.TrySetResult(true);

        Assert.Equal(0, await sweep);
        Assert.False(Assert.Single(factory.Transports).IsDisposed);
    }

    private static async Task<bool> WaitUntilTrueAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await condition().ConfigureAwait(false)) return true;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return await condition().ConfigureAwait(false);
    }

    private static async Task DisposeCoordinatorAsync(UdpProxyCoordinator coordinator) => await coordinator.DisposeAsync();
}
