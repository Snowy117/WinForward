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
    public async Task IPv6FlowUsesIPv6UdpTransport()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var flow = FlowKey.Create(Endpoint.From(IPAddress.Parse("2001:db8::10"), 53000), Endpoint.From(IPAddress.Parse("2001:db8::53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        var transport = Assert.Single(factory.Transports);
        Assert.Equal(AddressFamily.InterNetworkV6, transport.LocalEndpoint.AddressFamily);
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

    private static FlowKey CreateFlow(string remoteAddress) =>
        FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse(remoteAddress), 53), TransportProtocol.Udp, FlowOriginKind.Host);

    private sealed class FakeTransportFactory : IUdpProxyTransportFactory
    {
        public List<FakeTransport> Transports { get; } = [];
        private int _nextLocalPort = 40000;

        public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, AddressFamily addressFamily, CancellationToken cancellationToken)
        {
            // Each transport models a distinct bound UDP socket, so its local port is unique; the
            // relay alias collision guard in UdpProxyCoordinator must not reject distinct flows.
            var transport = new FakeTransport(addressFamily, Interlocked.Increment(ref _nextLocalPort));
            lock (Transports) Transports.Add(transport);
            return ValueTask.FromResult<IUdpProxyTransport>(transport);
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

    private sealed class FakeResponseSink : IUdpResponseSink
    {
        public Channel<(FlowKey Flow, Endpoint Remote, byte[] Payload)> Responses { get; } = Channel.CreateUnbounded<(FlowKey, Endpoint, byte[])>();

        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
            Responses.Writer.WriteAsync((originalFlow, remoteSource, payload.ToArray()), cancellationToken);
    }
}
