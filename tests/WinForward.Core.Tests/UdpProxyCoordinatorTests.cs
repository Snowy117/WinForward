using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class UdpProxyCoordinatorTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);

    [Fact]
    public async Task SameFlowBurstUsesOneTransportAndPreservesDatagrams()
    {
        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        await using var coordinator = new UdpProxyCoordinator(factory, sink);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 0x12, 0x34 }, CancellationToken.None));
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 0x56, 0x78 }, CancellationToken.None));

        var transport = Assert.Single(factory.Transports);
        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal(new byte[] { 0x12, 0x34 }, transport.Sent[0].Payload);
        Assert.Equal(new byte[] { 0x56, 0x78 }, transport.Sent[1].Payload);
    }

    [Fact]
    public async Task ConcurrentSameFlowBurstUsesOneTransportWithoutResponseCrossWiring()
    {
        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        await using var coordinator = new UdpProxyCoordinator(factory, sink);
        var flow = CreateFlow("192.0.2.53");
        const int count = 32;

        var sends = Enumerable.Range(0, count)
            .Select(index => coordinator.TrySendAsync(flow, s_server, new[] { (byte)index }, CancellationToken.None).AsTask())
            .ToArray();
        Assert.All(await Task.WhenAll(sends), Assert.True);

        var transport = Assert.Single(factory.Transports);
        lock (transport.Sent) Assert.Equal(count, transport.Sent.Count);

        for (var index = 0; index < count; index++)
        {
            await transport.Responses.Writer.WriteAsync(new Socks5UdpDatagram(IPAddress.Parse("192.0.2.53"), null, 53, new[] { (byte)index }), CancellationToken.None);
        }

        for (var index = 0; index < count; index++)
        {
            var response = await sink.Responses.Reader.ReadAsync(CancellationToken.None);
            Assert.Equal(flow, response.Flow);
            Assert.Equal((byte)index, Assert.Single(response.Payload));
        }
    }

    [Fact]
    public async Task ConcurrentDifferentRemoteEndpointsRemainOnDistinctTransports()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var sends = new[] { "192.0.2.53", "192.0.2.54" }
            .Select((address, index) => coordinator.TrySendAsync(CreateFlow(address), s_server, new[] { (byte)index }, CancellationToken.None).AsTask())
            .ToArray();

        Assert.All(await Task.WhenAll(sends), Assert.True);
        Assert.Equal(2, factory.Transports.Count);
    }

    [Fact]
    public async Task Ipv6OriginalFlowCanUseIpv4RelayAliasWithoutFlowKeyMismatch()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var flow = FlowKey.Create(Endpoint.From(IPAddress.Parse("2001:db8::10"), 53000), Endpoint.From(IPAddress.Parse("2001:db8::53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        var transport = Assert.Single(factory.Transports);
        Assert.Equal(AddressFamily.InterNetwork, transport.LocalEndpoint.AddressFamily);
        Assert.Equal(1, factory.CreateCalls);
        var sent = Assert.Single(transport.Sent);
        Assert.Equal(flow.Remote.Address, sent.Destination.Address);
        Assert.Equal(flow.Remote.Port, sent.Destination.Port);
    }

    [Fact]
    public async Task Ipv6OriginalFlowStillSupportsMatchingIpv6Relay()
    {
        var factory = new FakeTransportFactory(AddressFamily.InterNetworkV6);
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var flow = FlowKey.Create(Endpoint.From(IPAddress.Parse("2001:db8::10"), 53000), Endpoint.From(IPAddress.Parse("2001:db8::53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        var transport = Assert.Single(factory.Transports);
        Assert.Equal(AddressFamily.InterNetworkV6, transport.LocalEndpoint.AddressFamily);
        Assert.Equal(AddressFamily.InterNetworkV6, transport.RelayEndpoint.AddressFamily);
    }

    [Fact]
    public async Task DifferentRemoteEndpointsCreateDistinctTransports()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());

        Assert.True(await coordinator.TrySendAsync(CreateFlow("192.0.2.53"), s_server, new byte[] { 1 }, CancellationToken.None));
        Assert.True(await coordinator.TrySendAsync(CreateFlow("192.0.2.54"), s_server, new byte[] { 2 }, CancellationToken.None));

        Assert.Equal(2, factory.Transports.Count);
    }

    [Fact]
    public async Task RelayResponseKeepsOriginalFlowIdentity()
    {
        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        await using var coordinator = new UdpProxyCoordinator(factory, sink);
        var flow = CreateFlow("192.0.2.53");
        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));

        await factory.Transports[0].Responses.Writer.WriteAsync(new Socks5UdpDatagram(IPAddress.Parse("192.0.2.53"), null, 53, new byte[] { 9, 8 }), CancellationToken.None);
        var response = await sink.Responses.Reader.ReadAsync(CancellationToken.None);

        Assert.Equal(flow, response.Flow);
        Assert.Equal(Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), response.Remote);
        Assert.Equal(new byte[] { 9, 8 }, response.Payload);
    }

    [Fact]
    public async Task RelayResponseCarriesRecordedClientMac()
    {
        // R2: the client MAC captured with the first datagram must travel with the session into
        // every response sink call so forwarded responses can be rebuilt toward the client.
        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        await using var coordinator = new UdpProxyCoordinator(factory, sink);
        var flow = CreateFlow("192.0.2.53");
        var clientMac = new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x0a };

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None, 0, 0, clientMac));

        await factory.Transports[0].Responses.Writer.WriteAsync(new Socks5UdpDatagram(IPAddress.Parse("192.0.2.53"), null, 53, new byte[] { 9 }), CancellationToken.None);
        var response = await sink.Responses.Reader.ReadAsync(CancellationToken.None);

        Assert.Equal(clientMac, response.ClientMac);
    }

    [Fact]
    public async Task ReceiveBufferIsBoundedAndReturnedWhenCoordinatorStops()
    {
        var pool = new TrackingArrayPool();
        var factory = new FakeTransportFactory();
        var coordinator = new UdpProxyCoordinator(
            factory,
            new FakeResponseSink(),
            1,
            TimeProvider.System,
            null,
            maximumFrameSize: 1514,
            receiveBufferPool: pool);

        Assert.True(await coordinator.TrySendAsync(CreateFlow("192.0.2.53"), s_server, new byte[] { 1 }, CancellationToken.None));
        Assert.Equal(1537, pool.LastMinimumLength);

        await coordinator.DisposeAsync();

        Assert.Equal(1, pool.ReturnCount);
    }

    [Theory]
    [InlineData(1536, 1537, false)]
    [InlineData(1537, 1537, true)]
    public void FullReceiveBufferIsRejectedAsPossiblyTruncated(int receivedBytes, int bufferLength, bool expected)
    {
        Assert.Equal(expected, Socks5UdpTransport.IsPossiblyTruncated(receivedBytes, bufferLength));
    }

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

    private sealed class FakeTransportFactory : IUdpProxyTransportFactory
    {
        private readonly AddressFamily _addressFamily;
        public List<FakeTransport> Transports { get; } = [];
        private int _nextLocalPort = 40000;
        private int _createCalls;

        public FakeTransportFactory(AddressFamily addressFamily = AddressFamily.InterNetwork)
        {
            _addressFamily = addressFamily;
        }

        public int CreateCalls => Volatile.Read(ref _createCalls);

        public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            // Each transport models a distinct bound UDP socket, so its local port is unique; the
            // relay alias collision guard in UdpProxyCoordinator must not reject distinct flows.
            Interlocked.Increment(ref _createCalls);
            var transport = new FakeTransport(_addressFamily, Interlocked.Increment(ref _nextLocalPort));
            lock (Transports) Transports.Add(transport);
            return ValueTask.FromResult<IUdpProxyTransport>(transport);
        }
    }

    private sealed class GatedTransportFactory : IUdpProxyTransportFactory
    {
        private readonly TaskCompletionSource<IUdpProxyTransport> _create = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public TaskCompletionSource<bool> CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CreateFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FakeTransport> CreatedTransports { get; } = [];

        public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) != 1)
            {
                var transport = new FakeTransport(AddressFamily.InterNetwork, 40001);
                CreatedTransports.Add(transport);
                return transport;
            }

            CreateStarted.TrySetResult(true);
            try
            {
                return await _create.Task.ConfigureAwait(false);
            }
            finally
            {
                CreateFinished.TrySetResult(true);
            }
        }

        public void Fail(Exception exception) => _create.TrySetException(exception);
    }

    private sealed class CancellationAwareTransportFactory : IUdpProxyTransportFactory
    {
        public TaskCompletionSource<bool> CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            CreateStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Cancellation must interrupt the pending setup.");
        }
    }

    private sealed class CollidingAliasTransportFactory : IUdpProxyTransportFactory
    {
        public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IUdpProxyTransport>(new FakeTransport(AddressFamily.InterNetwork, 40000));
    }

    private sealed class ImmediateFaultTransportFactory : IUdpProxyTransportFactory
    {
        private int _calls;
        public List<ImmediateFaultTransport> FaultedTransports { get; } = [];

        public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                var transport = new ImmediateFaultTransport(AddressFamily.InterNetwork, 40000);
                FaultedTransports.Add(transport);
                return ValueTask.FromResult<IUdpProxyTransport>(transport);
            }

            return ValueTask.FromResult<IUdpProxyTransport>(new FakeTransport(AddressFamily.InterNetwork, 40001));
        }
    }


    private sealed class FakeTransport : IUdpProxyTransport
    {
        public FakeTransport(AddressFamily addressFamily, int localPort)
        {
            var loopback = addressFamily == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            LocalEndpoint = new IPEndPoint(loopback, localPort);
            RelayEndpoint = new IPEndPoint(loopback, 50000);
        }

        public IPEndPoint RelayEndpoint { get; }
        public IPEndPoint LocalEndpoint { get; }
        public bool IsDisposed { get; private set; }
        public List<(IPEndPoint Destination, byte[] Payload)> Sent { get; } = [];
        public Channel<Socks5UdpDatagram> Responses { get; } = Channel.CreateUnbounded<Socks5UdpDatagram>();

        public ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            lock (Sent) Sent.Add((destination, payload.ToArray()));
            return ValueTask.CompletedTask;
        }

        public ValueTask<Socks5UdpDatagram> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            // A disposed transport models a closed socket: the pump's pending receive must end
            // promptly instead of blocking forever, mirroring the real socket's throw.
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return Responses.Reader.ReadAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            Responses.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ImmediateFaultTransport : IUdpProxyTransport
    {
        public ImmediateFaultTransport(AddressFamily addressFamily, int localPort)
        {
            var loopback = addressFamily == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            LocalEndpoint = new IPEndPoint(loopback, localPort);
            RelayEndpoint = new IPEndPoint(loopback, 50000);
        }

        public IPEndPoint RelayEndpoint { get; }
        public IPEndPoint LocalEndpoint { get; }
        public bool IsDisposed { get; private set; }

        public ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("relay receive already failed"));

        public ValueTask<Socks5UdpDatagram> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromException<Socks5UdpDatagram>(new IOException("relay receive failed"));

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeResponseSink : IUdpResponseSink
    {
        public Channel<(FlowKey Flow, Endpoint Remote, byte[] Payload, byte[]? ClientMac)> Responses { get; } = Channel.CreateUnbounded<(FlowKey, Endpoint, byte[], byte[]?)>();

        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken) =>
            Responses.Writer.WriteAsync((originalFlow, remoteSource, payload.ToArray(), clientMac), cancellationToken);
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        public int LastMinimumLength { get; private set; }
        public int ReturnCount { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            LastMinimumLength = minimumLength;
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false) => ReturnCount++;
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

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
