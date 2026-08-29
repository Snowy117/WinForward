using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.Socks5TestServer;

namespace WinForward.Core.Tests;

public sealed class Socks5UdpAssociateTests
{
    [Fact]
    public async Task UdpSocketIsNotCreatedWhenControlSetupFails()
    {
        TrackingSocket? socket = null;
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await Socks5UdpTransport.CreateAsync(
                new Socks5Server("test", "127.0.0.1", 1080, null, null),
                new SelfTrafficRegistry(),
                CancellationToken.None,
                _ => ValueTask.FromException<Socks5ControlConnection>(new IOException("control setup failed")),
                family => socket = new TrackingSocket(family));
        });

        Assert.Null(socket);
    }

    [Fact]
    public async Task CoordinatorSendsIpv6DestinationThroughIpv4RelayAfterAllZeroAssociate()
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
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), null, null);
        var registry = new SelfTrafficRegistry();
        var coordinator = new UdpProxyCoordinator(new Socks5UdpTransportFactory(registry), new NoopResponseSink());
        var destination = new IPEndPoint(IPAddress.Parse("2001:db8::53"), 5353);
        var flow = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("2001:db8::10"), 53000),
            Endpoint.From(destination.Address, checked((ushort)destination.Port)),
            TransportProtocol.Udp,
            FlowOriginKind.Host);
        var payload = new byte[] { 0xde, 0xad, 0xbe, 0xef };

        Assert.True(await coordinator.TrySendAsync(flow, socksServer, payload, CancellationToken.None));

        var request = await associateRequest.Task.WaitAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, request);

        var packet = await relayPacket.Task.WaitAsync(CancellationToken.None);
        Assert.Equal(AddressFamily.InterNetwork, packet.Sender.AddressFamily);
        Assert.Equal(4, packet.Datagram[3]);
        Assert.True(Socks5UdpCodec.TryDecode(packet.Datagram, out var decoded));
        Assert.Equal(destination.Address, decoded.DestinationAddress);
        Assert.Equal((ushort)destination.Port, decoded.DestinationPort);
        Assert.Equal(payload, decoded.Payload.ToArray());

        var local = Endpoint.From(packet.Sender.Address, checked((ushort)packet.Sender.Port));
        var relay = Endpoint.From(relayEndpoint.Address, checked((ushort)relayEndpoint.Port));
        var relayFlow = FlowKey.Create(local, relay, TransportProtocol.Udp, FlowOriginKind.Host);
        Assert.True(registry.IsOwned(new FlowContext(relayFlow, null, null, null, null, relay.Port)));

        await coordinator.DisposeAsync();

        Assert.False(registry.IsOwned(new FlowContext(relayFlow, null, null, null, null, relay.Port)));
        await server;
    }

    [Fact]
    public async Task MatchingIpv6ControlAndRelayRemainSupported()
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
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), null, null);
        var registry = new SelfTrafficRegistry();
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, registry, CancellationToken.None);
        var destination = new IPEndPoint(IPAddress.Parse("2001:db8::53"), 5353);
        var payload = new byte[] { 1, 2, 3 };

        Assert.Equal(AddressFamily.InterNetworkV6, transport.LocalEndpoint.AddressFamily);
        await transport.SendAsync(destination, payload, CancellationToken.None);

        var request = await associateRequest.Task.WaitAsync(CancellationToken.None);
        Assert.Equal(22, request.Length);
        Assert.Equal(new byte[] { 5, 3, 0, 4 }, request[..4]);
        Assert.All(request[4..], value => Assert.Equal(0, value));

        var packet = await relayPacket.Task.WaitAsync(CancellationToken.None);
        Assert.Equal(AddressFamily.InterNetworkV6, packet.Sender.AddressFamily);
        Assert.True(Socks5UdpCodec.TryDecode(packet.Datagram, out var decoded));
        Assert.Equal(destination.Address, decoded.DestinationAddress);
        Assert.Equal((ushort)destination.Port, decoded.DestinationPort);
        Assert.Equal(payload, decoded.Payload.ToArray());

        await transport.DisposeAsync();
        await server;
    }

    [Fact]
    public async Task ControlLoopPreventionRemainsOwnedUntilSocketCloses()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = AcceptGreetingAsync(listener, CancellationToken.None);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var socksServer = new Socks5Server("test", endpoint.Address.ToString(), checked((ushort)endpoint.Port), null, null);
        TrackingSocket? socket = null;
        var registration = new OrderingRegistration(() => socket is not null && socket.IsDisposedValue);

        var control = await Socks5ControlConnection.ConnectAsync(
            socksServer,
            CancellationToken.None,
            onSocketReady: (_, _) => registration,
            socketFactory: family => socket = new TrackingSocket(family, SocketType.Stream, ProtocolType.Tcp));
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
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), null, null);
        var registry = new SelfTrafficRegistry();
        TrackingSocket? socket = null;
        var transport = await Socks5UdpTransport.CreateAsync(
            socksServer,
            registry,
            CancellationToken.None,
            null,
            family => socket = new TrackingSocket(family));
        var local = Endpoint.From(transport.LocalEndpoint.Address, checked((ushort)transport.LocalEndpoint.Port));
        var relay = Endpoint.From(transport.RelayEndpoint.Address, checked((ushort)transport.RelayEndpoint.Port));
        var context = new FlowContext(
            FlowKey.Create(local, relay, TransportProtocol.Udp, FlowOriginKind.Host),
            null,
            null,
            null,
            null,
            relay.Port);
        socket!.OnDisposing = () => Assert.True(registry.IsOwned(context));

        await transport.DisposeAsync();

        Assert.False(registry.IsOwned(context));
        serverCancellation.Cancel();
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
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), null, null);
        var registry = new SelfTrafficRegistry();
        TrackingSocket? socket = null;
        var transport = await Socks5UdpTransport.CreateAsync(
            socksServer,
            registry,
            CancellationToken.None,
            null,
            family => socket = new TrackingSocket(family));
        await transport.SendAsync(new IPEndPoint(IPAddress.Loopback, 53), new byte[] { 1 }, CancellationToken.None);
        var packet = await relayPacket.Task.WaitAsync(CancellationToken.None);
        var local = Endpoint.From(packet.Sender.Address, checked((ushort)packet.Sender.Port));
        var relay = Endpoint.From(relayEndpoint.Address, checked((ushort)relayEndpoint.Port));
        var context = new FlowContext(
            FlowKey.Create(local, relay, TransportProtocol.Udp, FlowOriginKind.Host),
            null,
            null,
            null,
            null,
            relay.Port);
        socket!.DisposeException = new IOException("synthetic socket disposal failure");

        var exception = await Assert.ThrowsAsync<IOException>(async () => await transport.DisposeAsync());

        Assert.Equal("synthetic socket disposal failure", exception.Message);
        Assert.True(socket.IsDisposedValue);
        Assert.False(registry.IsOwned(context));
        await server;
    }

    private sealed class NoopResponseSink : IUdpResponseSink
    {
        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class OrderingRegistration(Func<bool> socketIsDisposed) : IDisposable
    {
        private readonly Func<bool> _socketIsDisposed = socketIsDisposed;

        public bool DisposedAfterSocket { get; private set; }

        public void Dispose() => DisposedAfterSocket = _socketIsDisposed();
    }
}
