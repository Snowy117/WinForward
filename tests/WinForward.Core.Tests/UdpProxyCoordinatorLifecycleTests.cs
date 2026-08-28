using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class UdpProxyCoordinatorLifecycleTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);

    [Fact]
    public async Task RemoveExpiredDisposesIdleSessionAndReleasesAssociation()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var flow = CreateFlow("192.0.2.53");
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        var transport = Assert.Single(factory.Transports);

        // The session's last activity is now; a sweep far in the future must expire it.
        var removed = await coordinator.RemoveExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1));

        Assert.Equal(1, removed);
        Assert.True(transport.IsDisposed);
        // A subsequent send creates a fresh session (the old association was released).
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 2 }, CancellationToken.None));
        Assert.Equal(2, factory.Transports.Count);
    }

    [Fact]
    public async Task RemoveExpiredKeepsActiveSession()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var flow = CreateFlow("192.0.2.53");
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));

        // A sweep with a timeout far beyond the session's age must not expire it.
        var removed = await coordinator.RemoveExpiredAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));

        Assert.Equal(0, removed);
        Assert.Single(factory.Transports);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotTearDownSharedSetupAndLateFaultReleasesCapacity()
    {
        var factory = new GatedTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), 1, TimeProvider.System, null);
        var first = CreateFlow("192.0.2.53");
        var second = CreateFlow("192.0.2.54");
        using var cancelled = new CancellationTokenSource();

        var waiting = Task.Run(async () => await coordinator.TrySendAsync(first, s_server, new byte[] { 1 }, cancelled.Token));
        await factory.CreateStarted.Task.WaitAsync(CancellationToken.None);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);

        factory.Fail(new IOException("setup failed after the caller left"));
        await factory.CreateFinished.Task.WaitAsync(CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => coordinator.TrySendAsync(second, s_server, new byte[] { 2 }, CancellationToken.None).AsTask()));
        Assert.Single(factory.CreatedTransports);
    }

    [Fact]
    public async Task ReceiveFaultDisposesAndRemovesSessionWithoutAnotherSend()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), 1, TimeProvider.System, null);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        var transport = Assert.Single(factory.Transports);
        transport.Responses.Writer.TryComplete(new IOException("relay read failed"));

        Assert.True(await WaitUntilAsync(() => Task.FromResult(transport.IsDisposed)));
        Assert.True(await coordinator.TrySendAsync(CreateFlow("192.0.2.54"), s_server, new byte[] { 2 }, CancellationToken.None));
        Assert.Equal(2, factory.Transports.Count);
    }

    [Fact]
    public async Task ImmediateReceiveFaultRemovesSessionAfterCoordinatorRegistration()
    {
        var factory = new ImmediateFaultTransportFactory();
        var pool = new TrackingArrayPool();
        await using var coordinator = new UdpProxyCoordinator(
            factory,
            new FakeResponseSink(),
            1,
            TimeProvider.System,
            null,
            receiveBufferPool: pool);
        var flow = CreateFlow("192.0.2.53");

        await Assert.ThrowsAsync<IOException>(async () => await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));

        Assert.True(await WaitUntilAsync(() => Task.FromResult(Assert.Single(factory.FaultedTransports).IsDisposed)));
        Assert.Equal(1, pool.ReturnCount);
        Assert.True(await WaitUntilAsync(() => coordinator.TrySendAsync(CreateFlow("192.0.2.54"), s_server, new byte[] { 2 }, CancellationToken.None).AsTask()));
    }

    [Fact]
    public async Task CoordinatorDisposalPreservesSetupCancellation()
    {
        var factory = new CancellationAwareTransportFactory();
        var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var send = coordinator.TrySendAsync(CreateFlow("192.0.2.53"), s_server, new byte[] { 1 }, CancellationToken.None).AsTask();
        await factory.CreateStarted.Task.WaitAsync(CancellationToken.None);

        await coordinator.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await send);
    }

    [Fact]
    public async Task ConcurrentDisposalIsSingleFlightAndRejectsNewSends()
    {
        var factory = new FakeTransportFactory();
        var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());

        var firstDispose = DisposeCoordinatorAsync(coordinator);
        var secondDispose = DisposeCoordinatorAsync(coordinator);
        await Task.WhenAll(firstDispose, secondDispose);

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await coordinator.TrySendAsync(CreateFlow("192.0.2.53"), s_server, new byte[] { 1 }, CancellationToken.None));
        Assert.Empty(factory.Transports);
    }

    [Fact]
    public async Task SuccessfulSendRefreshesAssociationAndAvoidsStaleExpiry()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), 1, time, null);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 2 }, CancellationToken.None));

        Assert.Equal(0, await coordinator.RemoveExpiredAsync(time.GetUtcNow(), TimeSpan.FromMinutes(1)));
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 3 }, CancellationToken.None));
        Assert.Single(factory.Transports);
    }

    [Fact]
    public async Task ActiveSessionRetainsRelayAliasAfterItsCreationTimestampExpires()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new CollidingAliasTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink(), 2, time, null);
        var first = CreateFlow("192.0.2.53");
        var second = CreateFlow("192.0.2.54");

        Assert.True(await coordinator.TrySendAsync(first, s_server, new byte[] { 1 }, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await coordinator.TrySendAsync(first, s_server, new byte[] { 2 }, CancellationToken.None));
        Assert.Equal(0, await coordinator.RemoveExpiredAsync(time.GetUtcNow(), TimeSpan.FromMinutes(1)));

        await Assert.ThrowsAsync<IOException>(async () => await coordinator.TrySendAsync(second, s_server, new byte[] { 3 }, CancellationToken.None));
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
        await using var coordinator = new UdpProxyCoordinator(
            factory,
            new FakeResponseSink(),
            1,
            time,
            () =>
            {
                snapshotTaken!.TrySetResult(true);
                return new ValueTask(resumeSweep!.Task);
            });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(2));

        snapshotTaken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        resumeSweep = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweep = coordinator.RemoveExpiredAsync(time.GetUtcNow(), TimeSpan.FromMinutes(1)).AsTask();
        await snapshotTaken.Task.WaitAsync(CancellationToken.None);
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 2 }, CancellationToken.None));
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
        await using var coordinator = new UdpProxyCoordinator(
            factory,
            sink,
            1,
            time,
            () =>
            {
                snapshotTaken!.TrySetResult(true);
                return new ValueTask(resumeSweep!.Task);
            });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(2));

        snapshotTaken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        resumeSweep = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweep = coordinator.RemoveExpiredAsync(time.GetUtcNow(), TimeSpan.FromMinutes(1)).AsTask();
        await snapshotTaken.Task.WaitAsync(CancellationToken.None);
        await factory.Transports[0].Responses.Writer.WriteAsync(new Socks5UdpDatagram(IPAddress.Parse("192.0.2.53"), null, 53, new byte[] { 2 }), CancellationToken.None);
        _ = await sink.Responses.Reader.ReadAsync(CancellationToken.None);
        resumeSweep.TrySetResult(true);

        Assert.Equal(0, await sweep);
        Assert.False(Assert.Single(factory.Transports).IsDisposed);
    }

    private static FlowKey CreateFlow(string remoteAddress) =>
        FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse(remoteAddress), 53), TransportProtocol.Udp, FlowOriginKind.Host);

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition().ConfigureAwait(false)) return true;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return await condition().ConfigureAwait(false);
    }

    private static async Task DisposeCoordinatorAsync(UdpProxyCoordinator coordinator) => await coordinator.DisposeAsync();
}
