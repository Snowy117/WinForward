using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;

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

        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        // Session setup runs in the background; wait for both buffered datagrams to flush FIFO.
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 2;
        });
        lock (transport.Sent)
        {
            Assert.Equal(new byte[] { 0x12, 0x34 }, transport.Sent[0].Payload);
            Assert.Equal(new byte[] { 0x56, 0x78 }, transport.Sent[1].Payload);
        }
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

        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == count;
        });

        for (var index = 0; index < count; index++)
        {
            transport.EnqueueResponse(new Socks5UdpDatagram(IPAddress.Parse("192.0.2.53"), null, 53, new[] { (byte)index }));
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
        await WaitForAsync(() => factory.Transports.Count == 2);
        Assert.Equal(2, factory.Transports.Count);
    }

    [Fact]
    public async Task Ipv6OriginalFlowCanUseIpv4RelayAliasWithoutFlowKeyMismatch()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var flow = FlowKey.Create(Endpoint.From(IPAddress.Parse("2001:db8::10"), 53000), Endpoint.From(IPAddress.Parse("2001:db8::53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });
        Assert.Equal(AddressFamily.InterNetwork, transport.LocalEndpoint.AddressFamily);
        Assert.Equal(1, factory.CreateCalls);
        (Endpoint Destination, byte[] Payload) sent;
        lock (transport.Sent) sent = transport.Sent[0];
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
        await WaitForAsync(() => factory.Transports.Count == 1);
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

        await WaitForAsync(() => factory.Transports.Count == 2);
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
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });

        transport.EnqueueResponse(new Socks5UdpDatagram(IPAddress.Parse("192.0.2.53"), null, 53, new byte[] { 9, 8 }));
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
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });

        transport.EnqueueResponse(new Socks5UdpDatagram(IPAddress.Parse("192.0.2.53"), null, 53, new byte[] { 9 }));
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
        // The receive buffer is rented when the background setup starts the session's receive loop.
        await WaitForAsync(() => pool.LastMinimumLength == 1537);

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

    private static FlowKey CreateFlow(string remoteAddress) =>
        FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse(remoteAddress), 53), TransportProtocol.Udp, FlowOriginKind.Host);
}
