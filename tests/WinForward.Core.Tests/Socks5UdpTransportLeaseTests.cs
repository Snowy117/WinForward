using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;

namespace WinForward.Core.Tests;

/// <summary>
/// The lease is released exactly once, including every construction-failure path: the factory owns
/// the lease it rents, so a relay socket that cannot be created, a receive buffer that cannot be
/// applied, or a bind that fails must still return the association (and close a private one), and
/// disposing a lease repeatedly must release the association only once.
/// </summary>
public sealed class Socks5UdpTransportLeaseTests
{
    [Fact]
    public async Task FactoryReleasesTheLeaseWhenTheRelaySocketCannotBeCreated()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        await using var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Off);
        var factory = new Socks5UdpTransportFactory(pool, registry, UdpFrameBuilder.DefaultMaximumEthernetFrame, socketFactory: _ => throw new IOException("synthetic socket creation failure"));

        var exception = await Assert.ThrowsAsync<IOException>(() => factory.CreateAsync(server.Server, CancellationToken.None).AsTask());

        Assert.Equal("synthetic socket creation failure", exception.Message);
        await AssertLeaseReleasedAsync(pool, server);
    }

    [Fact]
    public async Task FactoryReleasesTheLeaseWhenTheConfiguredReceiveBufferCannotBeApplied()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        await using var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Off);
        // An already-disposed socket fails the first construction step that touches it: applying
        // the configured relay receive buffer.
        var factory = new Socks5UdpTransportFactory(pool, registry, UdpFrameBuilder.DefaultMaximumEthernetFrame, socketFactory: CreateDisposedSocket);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.CreateAsync(server.Server, CancellationToken.None).AsTask());

        await AssertLeaseReleasedAsync(pool, server);
    }

    [Fact]
    public async Task FactoryReleasesTheLeaseWhenTheRelaySocketCannotBeBound()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        await using var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Off);
        var factory = new Socks5UdpTransportFactory(
            pool,
            registry,
            UdpFrameBuilder.DefaultMaximumEthernetFrame,
            socketFactory: static family => new Socket(family, SocketType.Dgram, ProtocolType.Udp),
            disableUdpConnectionReset: static socket => socket.Dispose());

        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.CreateAsync(server.Server, CancellationToken.None).AsTask());

        await AssertLeaseReleasedAsync(pool, server);
    }

    [Fact]
    public async Task CreateReleasesItsRegistrationsAndTheCallerReleasesTheLeaseWhenTheFrameCapIsRejected()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        await using var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Off);
        var lease = await pool.RentAsync(server.Server, CancellationToken.None);
        // The bound local endpoint is captured while the socket still exists: the transport's
        // self-traffic tuple is keyed by it, and the failure path disposes the socket.
        IPEndPoint? boundLocal = null;

        // The transport constructor rejects the cap after the socket and the self-traffic tuple were
        // acquired; Create releases both, the caller keeps ownership of the lease.
        var rejection = Record.Exception(() => Socks5UdpTransport.Create(
            lease,
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
        var context = new FlowContext(relayFlow, ProcessName: null, ProcessPath: null, AdapterId: null, AdapterName: null, relay.Port);
        Assert.False(registry.IsOwned(context));
        Assert.Equal(1, pool.LeasedFlowCount);
        await lease.DisposeAsync();
        await AssertLeaseReleasedAsync(pool, server);
    }

    [Fact]
    public async Task LeaseDisposeIsExactlyOnce()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = new ScriptedSocks5UdpServer(RelayEndpoint());
        await using var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Off);
        var lease = await pool.RentAsync(server.Server, CancellationToken.None);
        Assert.Equal(1, pool.LeasedFlowCount);

        await lease.DisposeAsync();
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        await AssertLeaseReleasedAsync(pool, server);
    }

    [Fact]
    public async Task TransportReportsTheLeaseRelayAndReleasesItOnDispose()
    {
        var registry = new SelfTrafficRegistry();
        var relayEndpoint = RelayEndpoint();
        await using var server = new ScriptedSocks5UdpServer(relayEndpoint);
        await using var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Off);
        var lease = await pool.RentAsync(server.Server, CancellationToken.None);
        var peerCount = 0;

        var transport = Socks5UdpTransport.Create(lease, registry, disableUdpConnectionReset: _ => peerCount++);

        Assert.Equal(relayEndpoint, transport.RelayEndpoint);
        Assert.Equal(AddressFamily.InterNetwork, transport.LocalEndpoint.AddressFamily);
        Assert.Equal(1, peerCount);
        await transport.DisposeAsync();
        await AssertLeaseReleasedAsync(pool, server);
    }

    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local // False positive: both parameters are the assertion's input — the pool's counters are compared by the Assert.* calls below and the server is polled by the wait predicate, which is what makes this the shared exactly-once-release proof of the construction-failure suite.
    private static async Task AssertLeaseReleasedAsync(UdpAssociationPool pool, ScriptedSocks5UdpServer server)
    {
        Assert.Equal(0, pool.LeasedFlowCount);
        // Off mode: the released lease closed its private association, and with it the control
        // connection — the strongest available proof that the release was neither skipped nor run twice.
        Assert.Equal(0, pool.AssociationCount);
        // The peer observes the close asynchronously; the count settling at zero is the proof.
        await WaitForAsync(() => server.LiveConnectionCount == 0);
    }

    private static IPEndPoint RelayEndpoint() => new(IPAddress.Loopback, 43_000);

    private static Socket CreateDisposedSocket(AddressFamily family)
    {
        var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        socket.Dispose();
        return socket;
    }
}
