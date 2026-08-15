using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class Socks5ControlConnectionTests
{
    [Fact]
    public async Task ServerAddressResolutionTimeoutDoesNotDependOnResolverCancellation()
    {
        var unresolved = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var socksServer = new Socks5Server("test", "resolver.invalid", 1080, null, null);

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await Socks5ControlConnection.ConnectAsync(
                socksServer,
                CancellationToken.None,
                resolveAddresses: (_, _) => new ValueTask<IPAddress[]>(unresolved.Task),
                perAttemptTimeout: TimeSpan.FromMilliseconds(50));
        });
    }

    [Fact]
    public async Task HandshakeTimeoutIncludesMethodSelectionRead()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stopServer = new CancellationTokenSource();
        var greetingRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = StallAfterGreetingAsync(listener, greetingRead, stopServer.Token);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var socksServer = new Socks5Server("test", endpoint.Address.ToString(), checked((ushort)endpoint.Port), null, null);

        var connect = Socks5ControlConnection.ConnectAsync(socksServer, CancellationToken.None, perAttemptTimeout: TimeSpan.FromMilliseconds(100)).AsTask();
        await greetingRead.Task.WaitAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(async () => await connect);
        stopServer.Cancel();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationDuringHandshake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stopServer = new CancellationTokenSource();
        var greetingRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = StallAfterGreetingAsync(listener, greetingRead, stopServer.Token);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var socksServer = new Socks5Server("test", endpoint.Address.ToString(), checked((ushort)endpoint.Port), null, null);
        using var cancellation = new CancellationTokenSource();

        var connect = Socks5ControlConnection.ConnectAsync(socksServer, cancellation.Token, perAttemptTimeout: TimeSpan.FromSeconds(5)).AsTask();
        await greetingRead.Task.WaitAsync(CancellationToken.None);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connect);
        stopServer.Cancel();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public async Task CommandTimeoutIncludesReplyRead()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stopServer = new CancellationTokenSource();
        var commandRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = StallAfterUdpAssociateRequestAsync(listener, commandRead, stopServer.Token);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var socksServer = new Socks5Server("test", endpoint.Address.ToString(), checked((ushort)endpoint.Port), null, null);
        await using var control = await Socks5ControlConnection.ConnectAsync(socksServer, CancellationToken.None, perAttemptTimeout: TimeSpan.FromMilliseconds(200));

        var associate = control.UdpAssociateAsync(CancellationToken.None).AsTask();
        await commandRead.Task.WaitAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(async () => await associate);
        stopServer.Cancel();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public async Task UpstreamStreamClearsPerAttemptSocketTimeouts()
    {
        // R1: the per-attempt 30s socket timeouts bound connect/authentication only. Once the
        // connection enters the long-lived relay phase the socket timeouts must be cleared so an
        // idle upstream is governed by the relay's stall window, not by a stale connect timeout.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = AcceptGreetingAsync(listener, CancellationToken.None);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var socksServer = new Socks5Server("test", endpoint.Address.ToString(), checked((ushort)endpoint.Port), null, null);

        await using var control = await Socks5ControlConnection.ConnectAsync(socksServer, CancellationToken.None);
        var stream = Assert.IsType<NetworkStream>(control.GetUpstreamStream());

        // An infinite timeout reads back as -1 on Windows and as 0 on Linux; either way the
        // per-attempt 30s timeout must no longer apply to the relay socket.
        Assert.True(stream.Socket.ReceiveTimeout <= 0);
        Assert.True(stream.Socket.SendTimeout <= 0);
        await server;
    }

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
    public async Task RelayDatagramFromSiblingAddressIsAccepted()
    {
        // R3: RFC 1928 does not pin the relay reply to the BND address. A multi-homed/anycast relay
        // may answer from another address of the same scope; the source check must accept it as
        // long as the port and address family match the negotiated relay endpoint.
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var advertised = new IPEndPoint(IPAddress.Parse("127.0.0.3"), relayEndpoint.Port);
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAdvertisedAssociateAsync(tcpListener, advertised, serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), null, null);
        var registry = new SelfTrafficRegistry();
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, registry, CancellationToken.None);
        Assert.Equal(advertised, transport.RelayEndpoint);

        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), relayEndpoint.Port));
        var datagram = Socks5UdpCodec.Encode(IPAddress.Parse("192.0.2.53"), 53, new byte[] { 0xab });
        await sender.SendToAsync(datagram, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, transport.LocalEndpoint.Port), CancellationToken.None);

        var buffer = new byte[65_535];
        var response = await transport.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(IPAddress.Parse("192.0.2.53"), response.DestinationAddress);
        Assert.Equal(53, response.DestinationPort);
        Assert.Equal(new byte[] { 0xab }, response.Payload.ToArray());

        await transport.DisposeAsync();
        await server;
    }

    [Fact]
    public void RelaySourceValidationAcceptsSameFamilySamePort()
    {
        Assert.True(Socks5UdpTransport.IsAcceptableRelaySource(
            new IPEndPoint(IPAddress.Parse("127.0.0.2"), 1080),
            new IPEndPoint(IPAddress.Parse("127.0.0.3"), 1080)));
    }

    [Fact]
    public void RelaySourceValidationRejectsDifferentPortOrAddressFamily()
    {
        Assert.False(Socks5UdpTransport.IsAcceptableRelaySource(
            new IPEndPoint(IPAddress.Parse("127.0.0.2"), 1081),
            new IPEndPoint(IPAddress.Parse("127.0.0.3"), 1080)));
        Assert.False(Socks5UdpTransport.IsAcceptableRelaySource(
            new IPEndPoint(IPAddress.IPv6Loopback, 1080),
            new IPEndPoint(IPAddress.Parse("127.0.0.3"), 1080)));
        Assert.False(Socks5UdpTransport.IsAcceptableRelaySource(
            new IPEndPoint(IPAddress.Parse("127.0.0.2"), 1080),
            new IPEndPoint(IPAddress.IPv6Loopback, 1080)));
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

    private static async Task StallAfterGreetingAsync(TcpListener listener, TaskCompletionSource<bool> greetingRead, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        greetingRead.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AcceptGreetingAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task StallAfterUdpAssociateRequestAsync(TcpListener listener, TaskCompletionSource<bool> commandRead, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);
        var request = new byte[10];
        await stream.ReadExactlyAsync(request, cancellationToken).ConfigureAwait(false);
        commandRead.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ServeUdpAssociateAsync(
        TcpListener listener,
        Socket relaySocket,
        IPEndPoint relayEndpoint,
        TaskCompletionSource<byte[]> associateRequest,
        TaskCompletionSource<RelayPacket> relayDatagram,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);

        var request = await ReadSocksRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        associateRequest.TrySetResult(request);

        var addressBytes = relayEndpoint.Address.GetAddressBytes();
        var reply = new byte[4 + addressBytes.Length + 2];
        reply[0] = 5;
        reply[1] = 0;
        reply[2] = 0;
        reply[3] = relayEndpoint.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        addressBytes.CopyTo(reply, 4);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4 + addressBytes.Length), checked((ushort)relayEndpoint.Port));
        await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[65_535];
        EndPoint sender = relayEndpoint.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(IPAddress.Any, 0)
            : new IPEndPoint(IPAddress.IPv6Any, 0);
        var result = await relaySocket.ReceiveFromAsync(
            buffer,
            SocketFlags.None,
            sender,
            cancellationToken).ConfigureAwait(false);
        relayDatagram.TrySetResult(new RelayPacket(
            buffer.AsSpan(0, result.ReceivedBytes).ToArray(),
            Assert.IsType<IPEndPoint>(result.RemoteEndPoint)));

        var eofProbe = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(eofProbe, cancellationToken).ConfigureAwait(false));
    }

    private static async Task ServeAdvertisedAssociateAsync(TcpListener listener, IPEndPoint advertised, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);
        await ReadSocksRequestAsync(stream, cancellationToken).ConfigureAwait(false);

        var addressBytes = advertised.Address.GetAddressBytes();
        var reply = new byte[4 + addressBytes.Length + 2];
        reply[0] = 5;
        reply[1] = 0;
        reply[2] = 0;
        reply[3] = advertised.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        addressBytes.CopyTo(reply, 4);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4 + addressBytes.Length), checked((ushort)advertised.Port));
        await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);

        var eofProbe = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(eofProbe, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadSocksRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var addressLength = prefix[3] switch
        {
            1 => 4,
            4 => 16,
            _ => throw new IOException($"Unexpected SOCKS5 address type {prefix[3]} in test server.")
        };
        var request = new byte[4 + addressLength + 2];
        prefix.CopyTo(request, 0);
        await stream.ReadExactlyAsync(request.AsMemory(prefix.Length), cancellationToken).ConfigureAwait(false);
        return request;
    }

    private static async Task IgnoreExpectedCancellationAsync(Task server)
    {
        try
        {
            await server.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            GC.KeepAlive(server);
        }
    }

    private sealed class TrackingSocket : Socket
    {
        public TrackingSocket(AddressFamily addressFamily, SocketType socketType = SocketType.Dgram, ProtocolType protocolType = ProtocolType.Udp) : base(addressFamily, socketType, protocolType) { }
        public bool IsDisposedValue { get; private set; }
        public Action? OnDisposing { get; set; }
        public Exception? DisposeException { get; set; }

        protected override void Dispose(bool disposing)
        {
            OnDisposing?.Invoke();
            IsDisposedValue = true;
            base.Dispose(disposing);
            // A finalizer must never throw: the synthetic disposal failure is reserved for the
            // controlled Dispose() path, otherwise an unreleased test double crashes the host.
            if (disposing && DisposeException is not null) throw DisposeException;
        }
    }

    private sealed record RelayPacket(byte[] Datagram, IPEndPoint Sender);

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
