using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.Socks5TestServer;

namespace WinForward.Runtime.Socks5.Tests;

public sealed class Socks5UdpAssociateTests
{
    [Fact]
    public async Task UdpSocketIsNotCreatedWhenControlSetupFails()
    {
        TrackingSocket? socket = null;
        var registry = new SelfTrafficRegistry();
        var factory = new Socks5UdpTransportFactory(
            registry,
            UdpFrameBuilder.DefaultMaximumEthernetFrame,
            createControl: static (_, _) => ValueTask.FromException<Socks5ControlConnection>(new IOException("control setup failed")),
            socketFactory: family => socket = new TrackingSocket(family));

        await Assert.ThrowsAsync<IOException>(() => factory.CreateAsync(ProxyTarget.FromServer(new Socks5Server("test", "127.0.0.1", 1080, Username: null, Password: null)), CancellationToken.None).AsTask());

        // The relay socket is only created once the association exists, so a control-setup failure
        // cannot leave one behind; the wired factory would record it, so a regression that binds
        // the relay before the dial fails here rather than passing.
        Assert.Null(socket);
    }

    [Fact]
    public async Task CoordinatorSendsIPv6DestinationThroughIPv4RelayAfterAllZeroAssociate()
    {
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var associateRequest = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var relayPacket = new TaskCompletionSource<RelayPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeUdpAssociateAsync(
            tcpListener,
            relaySocket,
            relayEndpoint,
            associateRequest,
            relayPacket,
            serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        var registry = new SelfTrafficRegistry();
        var coordinator = UdpCoordinatorFakes.CreateCoordinator(new Socks5UdpTransportFactory(registry, UdpFrameBuilder.DefaultMaximumEthernetFrame), new NoopResponseSink());
        var destination = Endpoint.From(IPAddress.Parse("2001:db8::53"), 5353);
        var flow = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("2001:db8::10"), 53000),
            destination,
            TransportProtocol.Udp,
            FlowOriginKind.Host);
        var payload = new byte[] { 0xde, 0xad, 0xbe, 0xef };

        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(socksServer), payload, default, CancellationToken.None));

        var request = await associateRequest.Task.WaitAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, request);

        var packet = await relayPacket.Task.WaitAsync(CancellationToken.None);
        Assert.Equal(AddressFamily.InterNetwork, packet.Sender.AddressFamily);
        Assert.Equal(4, packet.Datagram[3]);
        Assert.True(Socks5UdpCodec.TryDecode(packet.Datagram, out var decoded));
        Assert.Equal(destination.Address, decoded.DestinationAddress);
        Assert.Equal(destination.Port, decoded.DestinationPort);
        Assert.Equal(payload, decoded.Payload.ToArray());

        var local = Endpoint.From(packet.Sender.Address, checked((ushort)packet.Sender.Port));
        var relay = Endpoint.From(relayEndpoint.Address, checked((ushort)relayEndpoint.Port));
        var relayFlow = FlowKey.Create(local, relay, TransportProtocol.Udp, FlowOriginKind.Host);
        Assert.True(registry.IsOwned(FlowBuilders.Context(relayFlow)));

        await coordinator.DisposeAsync();

        Assert.False(registry.IsOwned(FlowBuilders.Context(relayFlow)));
        await server;
    }

    [Fact]
    public async Task MatchingIPv6ControlAndRelayRemainSupported()
    {
        using var tcpListener = new TcpListener(IPAddress.IPv6Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var associateRequest = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var relayPacket = new TaskCompletionSource<RelayPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeUdpAssociateAsync(
            tcpListener,
            relaySocket,
            relayEndpoint,
            associateRequest,
            relayPacket,
            serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        var registry = new SelfTrafficRegistry();
        await using (var transport = await UdpTransportTestFactory.CreateAsync(socksServer, registry))
        {
            var destination = Endpoint.From(IPAddress.Parse("2001:db8::53"), 5353);
            var payload = new byte[] { 1, 2, 3 };

            Assert.Equal(AddressFamily.InterNetworkV6, transport.LocalEndpoint.AddressFamily);
            await transport.SendSpanAsync(destination, payload, CancellationToken.None);

            var request = await associateRequest.Task.WaitAsync(CancellationToken.None);
            Assert.Equal(22, request.Length);
            Assert.Equal(new byte[] { 5, 3, 0, 4 }, request[..4]);
            Assert.All(request[4..], value => Assert.Equal(0, value));

            var packet = await relayPacket.Task.WaitAsync(CancellationToken.None);
            Assert.Equal(AddressFamily.InterNetworkV6, packet.Sender.AddressFamily);
            Assert.True(Socks5UdpCodec.TryDecode(packet.Datagram, out var decoded));
            Assert.Equal(destination.Address, decoded.DestinationAddress);
            Assert.Equal(destination.Port, decoded.DestinationPort);
            Assert.Equal(payload, decoded.Payload.ToArray());
        }

        await server;
    }

    [Fact]
    public async Task ControlLoopPreventionRemainsOwnedUntilSocketCloses()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = AcceptGreetingAsync(listener, CancellationToken.None);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var socksServer = new Socks5Server("test", endpoint.Address.ToString(), checked((ushort)endpoint.Port), Username: null, Password: null);
        TrackingSocket? socket = null;
        var registration = new OrderingRegistration(() => socket is not null && socket.IsDisposedValue);

        var control = await Socks5ControlConnection.ConnectAsync(
            socksServer,
            CancellationToken.None,
            resolveAddresses: null,
            socketFactory: family => socket = new TrackingSocket(family, SocketType.Stream, ProtocolType.Tcp),
            onSocketReady: (_, _) => registration);
        await control.DisposeAsync();

        Assert.True(registration.DisposedAfterSocket);
        await server;
    }

    [Fact]
    public async Task UdpLoopPreventionRemainsOwnedUntilSocketCloses()
    {
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeUdpAssociateAsync(
            tcpListener,
            relaySocket,
            relayEndpoint,
            new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<RelayPacket>(TaskCreationOptions.RunContinuationsAsynchronously),
            serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        var registry = new SelfTrafficRegistry();
        TrackingSocket? socket = null;
        FlowContext context;
        await using (var transport = await UdpTransportTestFactory.CreateAsync(socksServer, registry, family => socket = new TrackingSocket(family)))
        {
            var local = Endpoint.From(transport.LocalEndpoint.Address, checked((ushort)transport.LocalEndpoint.Port));
            var relay = Endpoint.From(transport.PeerEndpoint.Address, checked((ushort)transport.PeerEndpoint.Port));
            context = FlowBuilders.Context(FlowKey.Create(local, relay, TransportProtocol.Udp, FlowOriginKind.Host));
            socket!.OnDisposing = () => Assert.True(registry.IsOwned(context));
        }

        Assert.False(registry.IsOwned(context));
        await serverCancellation.CancelAsync();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public async Task UdpDisposalReleasesRegistrationAndControlWhenSocketDisposalThrows()
    {
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var relayPacket = new TaskCompletionSource<RelayPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeUdpAssociateAsync(
            tcpListener,
            relaySocket,
            relayEndpoint,
            new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously),
            relayPacket,
            serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        var registry = new SelfTrafficRegistry();
        TrackingSocket? socket = null;
        var transport = await UdpTransportTestFactory.CreateAsync(socksServer, registry, family => socket = new TrackingSocket(family));
        await transport.SendSpanAsync(Endpoint.From(IPAddress.Loopback, 53), [1], CancellationToken.None);
        var packet = await relayPacket.Task.WaitAsync(CancellationToken.None);
        var local = Endpoint.From(packet.Sender.Address, checked((ushort)packet.Sender.Port));
        var relay = Endpoint.From(relayEndpoint.Address, checked((ushort)relayEndpoint.Port));
        var context = FlowBuilders.Context(FlowKey.Create(local, relay, TransportProtocol.Udp, FlowOriginKind.Host));
        socket!.DisposeException = new IOException("synthetic socket disposal failure");

        var exception = await Assert.ThrowsAsync<IOException>(async () => await transport.DisposeAsync());

        Assert.Equal("synthetic socket disposal failure", exception.Message);
        Assert.True(socket.IsDisposedValue);
        Assert.False(registry.IsOwned(context));
        await server;
    }

    private sealed class OrderingRegistration(Func<bool> socketIsDisposed) : IDisposable
    {

        public bool DisposedAfterSocket { get; private set; }

        public void Dispose() => DisposedAfterSocket = socketIsDisposed();
    }
}
