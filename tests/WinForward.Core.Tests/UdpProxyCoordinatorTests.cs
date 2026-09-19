using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.FlowBuilders;

namespace WinForward.Core.Tests;

public sealed class UdpProxyCoordinatorTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);
    private static readonly string[] s_remoteAddresses = ["192.0.2.53", "192.0.2.54"];

    [Fact]
    public async Task SameFlowBurstUsesOneTransportAndPreservesDatagrams()
    {
        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        await using var coordinator = new UdpProxyCoordinator(factory, sink);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [0x12, 0x34], default, CancellationToken.None));
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, "Vx"u8, default, CancellationToken.None));

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
            Assert.Equal("Vx"u8.ToArray(), transport.Sent[1].Payload);
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
            .Select(index => coordinator.TrySendSpanAsync(flow, s_server, [(byte)index], default, CancellationToken.None).AsTask())
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
            transport.EnqueueResponse(new Socks5UdpDatagram(IPAddress.Parse("192.0.2.53"), DestinationDomain: null, 53, new[] { (byte)index }));
        }

        for (var index = 0; index < count; index++)
        {
            var (responseFlow, _, payload, _) = await sink.Responses.Reader.ReadAsync(CancellationToken.None);
            Assert.Equal(flow, responseFlow);
            Assert.Equal((byte)index, Assert.Single(payload));
        }
    }

    [Fact]
    public async Task ConcurrentDifferentRemoteEndpointsRemainOnDistinctTransports()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var sends = s_remoteAddresses
            .Select((address, index) => coordinator.TrySendSpanAsync(CreateFlow(address), s_server, [(byte)index], default, CancellationToken.None).AsTask())
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

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
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

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
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

        Assert.True(await coordinator.TrySendSpanAsync(CreateFlow("192.0.2.53"), s_server, [1], default, CancellationToken.None));
        Assert.True(await coordinator.TrySendSpanAsync(CreateFlow("192.0.2.54"), s_server, [2], default, CancellationToken.None));

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
        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });

        transport.EnqueueResponse(new Socks5UdpDatagram(IPAddress.Parse("192.0.2.53"), DestinationDomain: null, 53, new byte[] { 9, 8 }));
        var (responseFlow, remote, payload, _) = await sink.Responses.Reader.ReadAsync(CancellationToken.None);

        Assert.Equal(flow, responseFlow);
        Assert.Equal(Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), remote);
        Assert.Equal(new byte[] { 9, 8 }, payload);
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

        Assert.True(await coordinator.TrySendSpanAsync(flow, s_server, [1], MacAddress.From(clientMac), CancellationToken.None, 0, 0));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });

        transport.EnqueueResponse(new Socks5UdpDatagram(IPAddress.Parse("192.0.2.53"), DestinationDomain: null, 53, "\t"u8.ToArray()));
        var (_, _, _, responseClientMac) = await sink.Responses.Reader.ReadAsync(CancellationToken.None);

        Assert.Equal(MacAddress.From(clientMac), responseClientMac);
    }

    [Fact]
    public async Task ReceiveBufferIsBoundedAndReturnedWhenCoordinatorStops()
    {
        using var pool = new NativeBufferPool(1537);
        var factory = new FakeTransportFactory();
        var coordinator = new UdpProxyCoordinator(
            factory,
            new FakeResponseSink(),
            new UdpProxyOptions { Capacity = 1, MaximumFrameSize = 1514, ReceiveWindowPool = pool });

        Assert.True(await coordinator.TrySendSpanAsync(CreateFlow("192.0.2.53"), s_server, [1], default, CancellationToken.None));
        // The receive window is rented when the background setup starts the session's receive loop.
        await WaitForAsync(() => pool.Stats.Outstanding == 1);
        Assert.Equal(1537, pool.BufferSize);

        await coordinator.DisposeAsync();

        Assert.Equal(1, pool.Stats.Returned);
        Assert.Equal(0, pool.Stats.Outstanding);
    }

    [Theory]
    [InlineData(1536, 1537, false)]
    [InlineData(1537, 1537, true)]
    public void FullReceiveBufferIsRejectedAsPossiblyTruncated(int receivedBytes, int bufferLength, bool expected)
    {
        Assert.Equal(expected, Socks5UdpTransport.IsPossiblyTruncated(receivedBytes, bufferLength));
    }

}
