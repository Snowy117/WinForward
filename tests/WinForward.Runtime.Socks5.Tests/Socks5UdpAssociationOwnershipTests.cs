using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;

namespace WinForward.Runtime.Socks5.Tests;

/// <summary>
/// The per-flow ownership shape: every proxied UDP flow is served by its own authenticated
/// association — its own control connection, its own <c>UDP ASSOCIATE</c>, its own relay socket —
/// and disposing the flow's transport releases both the control connection and the relay socket.
/// There is no sharing to observe: two concurrent flows cannot end up on one association.
/// </summary>
public sealed class Socks5UdpAssociationOwnershipTests
{
    [Fact]
    public async Task TwoConcurrentFlowsOwnTwoControlConnectionsAndTwoRelaySocketsAndReleaseBothWithTheFlow()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        var sockets = new List<TrackingSocket>();
        var factory = new Socks5UdpTransportFactory(
            registry,
            UdpFrameBuilder.DefaultMaximumEthernetFrame,
            socketFactory: family =>
            {
                var socket = new TrackingSocket(family);
                lock (sockets) sockets.Add(socket);
                return socket;
            });

        var first = (Socks5UdpTransport)await factory.CreateAsync(ProxyTarget.FromServer(server.Server), CancellationToken.None);
        var second = (Socks5UdpTransport)await factory.CreateAsync(ProxyTarget.FromServer(server.Server), CancellationToken.None);

        // One dial and one ASSOCIATE per flow: two control connections, two exchanges.
        Assert.Equal(2, server.ConnectionCount);
        Assert.Equal(2, server.AssociateReplyCount);
        // One relay socket per flow, bound to its own local port.
        TrackingSocket[] bound;
        lock (sockets) bound = [.. sockets];
        Assert.Equal(2, bound.Length);
        Assert.NotEqual(first.LocalEndpoint.Port, second.LocalEndpoint.Port);
        // Both flows negotiated the one relay their own ASSOCIATE returned.
        Assert.Equal(first.PeerEndpoint, second.PeerEndpoint);

        await first.DisposeAsync();
        await second.DisposeAsync();

        // Both halves of both associations are gone: the relay sockets are disposed and the server
        // observes both control connections closed.
        Assert.All(bound, static socket => Assert.True(socket.IsDisposedValue));
        await WaitForAsync(() => server.LiveConnectionCount == 0);
    }

    [Fact]
    public async Task EachFlowCarriesItsOwnAssociateResultSoAFamilyChangeCannotStrandASibling()
    {
        // The per-flow association's relay is negotiated per flow: a server that advertises a
        // different relay per connection gives each flow the relay of its own ASSOCIATE, so no flow
        // ever follows an endpoint another flow negotiated.
        var firstRelay = RelayEndpoint();
        var secondRelay = new IPEndPoint(IPAddress.Loopback, 43_001);
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(firstRelay, ordinal => ordinal == 0 ? firstRelay : secondRelay);
        var factory = new Socks5UdpTransportFactory(registry, UdpFrameBuilder.DefaultMaximumEthernetFrame);

        await using var first = (Socks5UdpTransport)await factory.CreateAsync(ProxyTarget.FromServer(server.Server), CancellationToken.None);
        await using var second = (Socks5UdpTransport)await factory.CreateAsync(ProxyTarget.FromServer(server.Server), CancellationToken.None);

        Assert.Equal(firstRelay, first.PeerEndpoint);
        Assert.Equal(secondRelay, second.PeerEndpoint);
        Assert.Equal(2, server.AssociateReplyCount);
    }

    [Fact]
    public async Task TheFlowRelaySocketIsReleasedWithTheTransport()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        var transport = await UdpTransportTestFactory.CreateAsync(server.Server, registry);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);

        await transport.SendSpanAsync(destination, [1, 2, 3], CancellationToken.None);
        await transport.DisposeAsync();

        // The relay socket is closed: a receive against it can no longer complete.
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(async () => await transport.ReceiveAsync(new byte[256], CancellationToken.None));
        await WaitForAsync(() => server.LiveConnectionCount == 0);
    }

    private static IPEndPoint RelayEndpoint() => new(IPAddress.Loopback, 43_000);
}
