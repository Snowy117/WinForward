using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.Socks5TestServer;

namespace WinForward.Core.Tests;

/// <summary>
/// D2 sync-send fast path: the relay socket runs non-blocking and <c>SendAsync</c> is a
/// non-async entry whose warm shape hands the datagram to the kernel inline (no IOCP hop, no
/// state machine); a loopback echo peer proves the synchronous send is actually delivered.
/// </summary>
public sealed class Socks5UdpTransportSendTests
{
    [Fact]
    public async Task SyncSendReachesLoopbackEchoPeerAndReceivesTheEcho()
    {
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var associateRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeAssociateOnlyAsync(tcpListener, relayEndpoint, associateRead, serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), null, null);
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, new SelfTrafficRegistry(), CancellationToken.None);
        var payload = new byte[] { 0x51, 0x52, 0x53 };
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);

        await transport.SendAsync(destination, payload, CancellationToken.None);

        var buffer = new byte[65_535];
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        var received = await relaySocket.ReceiveFromAsync(buffer, SocketFlags.None, sender, CancellationToken.None);
        Assert.True(Socks5UdpCodec.TryDecode(buffer.AsMemory(0, received.ReceivedBytes), out var datagram));
        Assert.Equal((IPAddressValue?)destination.Address, datagram.DestinationAddress);
        Assert.Equal(destination.Port, datagram.DestinationPort);
        Assert.Equal(payload, datagram.Payload.ToArray());

        // The echo peer answers from the negotiated relay; the same transport decodes it.
        await relaySocket.SendToAsync(
            Socks5UdpCodec.Encode(IPAddress.Parse("192.0.2.10"), 53000, payload),
            SocketFlags.None,
            (EndPoint)transport.LocalEndpoint!,
            CancellationToken.None);
        var echo = await transport.ReceiveAsync(buffer, CancellationToken.None);
        Assert.True(echo.HasDatagram);
        Assert.Equal(payload, echo.Datagram.Payload.ToArray());

        await transport.DisposeAsync();
        serverCancellation.Cancel();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public async Task RelaySocketRunsNonBlockingAfterAssociate()
    {
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAssociateOnlyAsync(tcpListener, relayEndpoint, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), null, null);
        TrackingSocket? socket = null;
        var transport = await Socks5UdpTransport.CreateAsync(
            socksServer,
            new SelfTrafficRegistry(),
            CancellationToken.None,
            null,
            family => socket = new TrackingSocket(family));

        Assert.False(socket!.Blocking);

        await transport.DisposeAsync();
        serverCancellation.Cancel();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public async Task JumboCapSendBufferEncodesPayloadsBeyondTheDefaultCap()
    {
        // R4: the send buffer derives from the frame cap fed at construction. With a jumbo cap
        // (9014), a 2000-byte payload — far beyond the default ABI's 1508-byte ceiling — encodes
        // and reaches the relay instead of failing the buffer bound.
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAssociateOnlyAsync(tcpListener, relayEndpoint, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), null, null);
        var transport = await Socks5UdpTransport.CreateAsync(
            socksServer,
            new SelfTrafficRegistry(),
            CancellationToken.None,
            null,
            null,
            maximumFrameSize: 9014);
        var payload = new byte[2000];

        await transport.SendAsync(Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), payload, CancellationToken.None);

        var buffer = new byte[65_535];
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        var received = await relaySocket.ReceiveFromAsync(buffer, SocketFlags.None, sender, CancellationToken.None);
        Assert.Equal(6 + 4 + payload.Length, received.ReceivedBytes);
        Assert.True(Socks5UdpCodec.TryDecode(buffer.AsMemory(0, received.ReceivedBytes), out var datagram));
        Assert.Equal(payload.Length, datagram.Payload.Length);
        Assert.Equal((IPAddressValue?)IPAddress.Parse("192.0.2.53"), datagram.DestinationAddress);

        await transport.DisposeAsync();
        serverCancellation.Cancel();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public async Task DefaultCapSendBufferFailsClosedOnOversizedPayloads()
    {
        // R4: the buffer bound stays fail-closed. At the default cap the send buffer covers
        // 6 + 16 + 1514 bytes, so a 2000-byte payload cannot encode; the IOException (not a
        // silent drop) is the guard if the capture-bounds-the-payload assumption ever breaks.
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAssociateOnlyAsync(tcpListener, relayEndpoint, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), null, null);
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, new SelfTrafficRegistry(), CancellationToken.None);
        var payload = new byte[2000];

        await Assert.ThrowsAsync<IOException>(async () =>
            await transport.SendAsync(Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), payload, CancellationToken.None));

        await transport.DisposeAsync();
        serverCancellation.Cancel();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public void SendAsyncWarmPathRunsNoAsyncStateMachine()
    {
        var method = typeof(Socks5UdpTransport).GetMethod(
            nameof(Socks5UdpTransport.SendAsync),
            [typeof(Endpoint), typeof(ReadOnlyMemory<byte>), typeof(CancellationToken)]);
        Assert.NotNull(method);
        Assert.Null(method.GetCustomAttribute<AsyncStateMachineAttribute>());
    }
}
