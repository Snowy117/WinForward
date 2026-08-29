using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.Socks5TestServer;

namespace WinForward.Core.Tests;

public sealed class Socks5ControlTimeoutTests
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
        Assert.True(response.HasDatagram);
        Assert.Equal(IPAddress.Parse("192.0.2.53"), response.Datagram.DestinationAddress);
        Assert.Equal(53, response.Datagram.DestinationPort);
        Assert.Equal(new byte[] { 0xab }, response.Datagram.Payload.ToArray());

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
}
