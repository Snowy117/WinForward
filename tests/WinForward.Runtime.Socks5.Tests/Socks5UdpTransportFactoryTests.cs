using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.TestSupport;
using Xunit;
using Xunit.Abstractions;
using static WinForward.TestSupport.AsyncTestExtensions;

namespace WinForward.Runtime.Socks5.Tests;

/// <summary>
/// The factory's ownership contract: it dials the flow's association, and when the relay socket
/// cannot come into existence — a socket constructor failure, an unappliable receive buffer, a
/// failed bind — the association it dialed is released (its control connection closed) instead of
/// being leaked. The transport it returns owns both halves and releases them exactly once.
/// </summary>
public sealed class Socks5UdpTransportFactoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FactoryReleasesTheAssociationWhenTheRelaySocketCannotBeCreated()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        var factory = new Socks5UdpTransportFactory(registry, UdpFrameBuilder.DefaultMaximumEthernetFrame, socketFactory: _ => throw new IOException("synthetic socket creation failure"));

        var exception = await Assert.ThrowsAsync<IOException>(() => factory.CreateAsync(ProxyTarget.FromServer(server.Server), CancellationToken.None).AsTask());

        Assert.Equal("synthetic socket creation failure", exception.Message);
        await AssertAssociationReleasedAsync(server);
    }

    [Fact]
    public async Task FactoryReleasesTheAssociationWhenTheConfiguredReceiveBufferCannotBeApplied()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        // An already-disposed socket fails the first construction step that touches it: applying
        // the configured relay receive buffer.
        var factory = new Socks5UdpTransportFactory(registry, UdpFrameBuilder.DefaultMaximumEthernetFrame, socketFactory: CreateDisposedSocket);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.CreateAsync(ProxyTarget.FromServer(server.Server), CancellationToken.None).AsTask());

        await AssertAssociationReleasedAsync(server);
    }

    [Fact]
    public async Task FactoryReleasesTheAssociationWhenTheRelaySocketCannotBeBound()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        var factory = new Socks5UdpTransportFactory(
            registry,
            UdpFrameBuilder.DefaultMaximumEthernetFrame,
            socketFactory: static family => new Socket(family, SocketType.Dgram, ProtocolType.Udp),
            disableUdpConnectionReset: static socket => socket.Dispose());

        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.CreateAsync(ProxyTarget.FromServer(server.Server), CancellationToken.None).AsTask());

        await AssertAssociationReleasedAsync(server);
    }

    [Fact]
    public async Task CreateReleasesItsRegistrationsAndTheCallerReleasesTheAssociationWhenTheFrameCapIsRejected()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        var association = await Socks5UdpAssociation.ConnectAsync(server.Server, Context(registry), CancellationToken.None);
        // The bound local endpoint is captured while the socket still exists: the transport's
        // self-traffic tuple is keyed by it, and the failure path disposes the socket.
        IPEndPoint? boundLocal = null;

        // The transport constructor rejects the cap after the socket and the self-traffic tuple were
        // acquired; Create releases both, the caller keeps ownership of the association.
        var rejection = Record.Exception(() => Socks5UdpTransport.Create(
            association,
            registry,
            socketFactory: family =>
            {
                var socket = new TrackingSocket(family);
                socket.OnDisposing = () => boundLocal = (IPEndPoint)socket.LocalEndPoint!;
                return socket;
            },
            maximumFrameSize: 0));
        Assert.IsType<ArgumentOutOfRangeException>(rejection);

        Assert.NotNull(boundLocal);
        var local = Endpoint.From(boundLocal.Address, checked((ushort)boundLocal.Port));
        var relay = Endpoint.From(RelayEndpoint().Address, checked((ushort)RelayEndpoint().Port));
        var relayFlow = FlowKey.Create(local, relay, TransportProtocol.Udp, FlowOriginKind.Host);
        var context = FlowBuilders.Context(relayFlow);
        Assert.False(registry.IsOwned(context));
        Assert.Equal(1, server.LiveConnectionCount);
        await association.DisposeAsync();
        await AssertAssociationReleasedAsync(server);
    }

    [Fact]
    public async Task TransportDisposeIsExactlyOnce()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        var transport = await UdpTransportTestFactory.CreateAsync(server.Server, registry);
        Assert.Equal(1, server.LiveConnectionCount);

        await transport.DisposeAsync();
        await transport.DisposeAsync();
        await transport.DisposeAsync();

        await AssertAssociationReleasedAsync(server);
    }

    [Fact]
    public async Task TransportReportsTheAssociationsRelayAndReleasesItOnDispose()
    {
        var registry = new SelfTrafficRegistry();
        var relayEndpoint = RelayEndpoint();
        await using var server = new ScriptedSocks5UdpServer(relayEndpoint);
        var peerCount = 0;

        var transport = await UdpTransportTestFactory.CreateAsync(
            server.Server,
            registry,
            disableUdpConnectionReset: _ => peerCount++);

        Assert.Equal(relayEndpoint, transport.PeerEndpoint);
        Assert.Equal(AddressFamily.InterNetwork, transport.LocalEndpoint.AddressFamily);
        Assert.Equal(1, peerCount);
        await transport.DisposeAsync();
        await AssertAssociationReleasedAsync(server);
    }

    /// <summary>
    /// The configured relay receive budget reaches a real socket through the production factory, which
    /// the direct-construction test alone cannot show. On Linux the kernel doubles <c>SO_RCVBUF</c>, so
    /// the exact statement is the configured value and this fact asserts the socket's applied value is
    /// at least the request; the read-back is written into the test output so the platform difference
    /// is visible rather than assumed.
    /// </summary>
    [Theory]
    [InlineData(128 * 1024)]
    [InlineData(ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes)]
    public async Task FactoryAppliesTheConfiguredRelayReceiveBufferToARealSocket(int requestedBytes)
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        var factory = new Socks5UdpTransportFactory(registry, UdpFrameBuilder.DefaultMaximumEthernetFrame, requestedBytes);

        await using var transport = await factory.CreateAsync(ProxyTarget.FromServer(server.Server), CancellationToken.None);

        var applied = Assert.IsType<Socks5UdpTransport>(transport).AppliedRelayReceiveBufferSize;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"requested={requestedBytes} applied={applied}"));
        Assert.True(applied >= requestedBytes, string.Create(CultureInfo.InvariantCulture, $"the relay socket applied {applied} bytes for a requested {requestedBytes}"));
    }

    private static Socks5UdpAssociationContext Context(SelfTrafficRegistry registry) =>
        new(registry, AddressCache: null, CreateControl: null, NullRuntimeLogger.Instance, new RuntimeLogThrottle(TimeSpan.FromSeconds(5)));

    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local // False positive: the server is polled by the wait predicate, which is what makes this the shared exactly-once-release proof of the construction-failure suite.
    private static async Task AssertAssociationReleasedAsync(ScriptedSocks5UdpServer server)
    {
        // The released association closed its control connection — the strongest available proof
        // that the release was neither skipped nor run twice.
        await WaitForAsync(() => server.LiveConnectionCount == 0);
        Assert.Equal(1, server.ConnectionCount);
    }

    private static IPEndPoint RelayEndpoint() => new(IPAddress.Loopback, 43_000);

    private static Socket CreateDisposedSocket(AddressFamily family)
    {
        var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        socket.Dispose();
        return socket;
    }
}
