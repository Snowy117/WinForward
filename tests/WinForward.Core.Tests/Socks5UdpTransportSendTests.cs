using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.Socks5TestServer;

namespace WinForward.Core.Tests;

/// <summary>
/// D2 sync-send fast path: the relay socket runs non-blocking and <c>SendSpanAsync</c> is a
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
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, new SelfTrafficRegistry(), CancellationToken.None, createControl: null, socketFactory: null);
        var payload = "QRS"u8.ToArray();
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);

        await transport.SendSpanAsync(destination, payload, CancellationToken.None);

        var buffer = new byte[65_535];
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        var received = await relaySocket.ReceiveFromAsync(buffer, SocketFlags.None, sender, CancellationToken.None);
        Assert.True(Socks5UdpCodec.TryDecode(buffer.AsMemory(0, received.ReceivedBytes), out var datagram));
        Assert.Equal(destination.Address, datagram.DestinationAddress);
        Assert.Equal(destination.Port, datagram.DestinationPort);
        Assert.Equal(payload, datagram.Payload.ToArray());

        // The echo peer answers from the negotiated relay; the same transport decodes it.
        await relaySocket.SendToAsync(
            Socks5UdpDatagrams.Encode(IPAddress.Parse("192.0.2.10"), 53000, payload),
            SocketFlags.None,
            transport.LocalEndpoint,
            CancellationToken.None);
        var echo = await transport.ReceiveAsync(buffer, CancellationToken.None);
        Assert.True(echo.HasDatagram);
        Assert.Equal(payload, echo.Datagram.Payload.ToArray());

        await transport.DisposeAsync();
        await serverCancellation.CancelAsync();
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
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        TrackingSocket? socket = null;
        var transport = await Socks5UdpTransport.CreateAsync(
            socksServer,
            new SelfTrafficRegistry(),
            CancellationToken.None,
            createControl: null,
            family => socket = new TrackingSocket(family));

        Assert.False(socket!.Blocking);

        await transport.DisposeAsync();
        await serverCancellation.CancelAsync();
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
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        var transport = await Socks5UdpTransport.CreateAsync(
            socksServer,
            new SelfTrafficRegistry(),
            CancellationToken.None,
            createControl: null,
            socketFactory: null,
            maximumFrameSize: 9014);
        var payload = new byte[2000];

        await transport.SendSpanAsync(Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), payload, CancellationToken.None);

        var buffer = new byte[65_535];
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        var received = await relaySocket.ReceiveFromAsync(buffer, SocketFlags.None, sender, CancellationToken.None);
        Assert.Equal(6 + 4 + payload.Length, received.ReceivedBytes);
        Assert.True(Socks5UdpCodec.TryDecode(buffer.AsMemory(0, received.ReceivedBytes), out var datagram));
        Assert.Equal(payload.Length, datagram.Payload.Length);
        Assert.Equal((IPAddressValue?)IPAddress.Parse("192.0.2.53"), datagram.DestinationAddress);

        await transport.DisposeAsync();
        await serverCancellation.CancelAsync();
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
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, new SelfTrafficRegistry(), CancellationToken.None, createControl: null, socketFactory: null);
        var payload = new byte[2000];

        await Assert.ThrowsAsync<IOException>(async () =>
            await transport.SendSpanAsync(Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), payload, CancellationToken.None));

        await transport.DisposeAsync();
        await serverCancellation.CancelAsync();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public void SendSpanAsyncWarmPathRunsNoAsyncStateMachine()
    {
        var method = typeof(Socks5UdpTransport).GetMethod(
            nameof(Socks5UdpTransport.SendSpanAsync),
            [typeof(Endpoint), typeof(ReadOnlySpan<byte>), typeof(CancellationToken)]);
        Assert.NotNull(method);
        Assert.Null(method.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    [Fact]
    public async Task WarmSyncSendAllocatesNoManagedBytes()
    {
        // Regression gate for the real relay send path: serializing the destination EndPoint on
        // every SendTo allocated 72 B/datagram (the fake-transport allocation gates could not see
        // it). The warm shape must hand the pre-serialized SocketAddress to the kernel inline.
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAssociateOnlyAsync(tcpListener, relayEndpoint, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, new SelfTrafficRegistry(), CancellationToken.None, createControl: null, socketFactory: null);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = "QRS"u8.ToArray();
        try
        {
            for (var warm = 0; warm < 8; warm++)
            {
                var warmSend = transport.SendSpanAsync(destination, payload, CancellationToken.None);
                Assert.True(warmSend.IsCompletedSuccessfully);
                await warmSend;
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            const int count = 64;
            for (var index = 0; index < count; index++)
            {
                var send = transport.SendSpanAsync(destination, payload, CancellationToken.None);
                Assert.True(send.IsCompletedSuccessfully, "the warm send must complete synchronously on the calling thread");
                await send;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
        }
        finally
        {
            await transport.DisposeAsync();
            await serverCancellation.CancelAsync();
            await IgnoreExpectedCancellationAsync(server);
        }
    }

    [Fact]
    public async Task SendAfterDisposalIsRefusedByTheDisposalGuardNotTheDisposedGate()
    {
        // Regression this catches: the send gate is disposed last and used to be the only barrier,
        // so a sender arriving after disposal entered `_sendGate.WaitAsync` and surfaced the
        // semaphore's own ObjectDisposedException. The refusal must come from the transport's
        // disposal guard, before the gate is touched.
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAssociateOnlyAsync(tcpListener, relayEndpoint, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, new SelfTrafficRegistry(), CancellationToken.None, createControl: null, socketFactory: null);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = "ABC"u8.ToArray();

        await transport.DisposeAsync();

        var refusal = await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await transport.SendSpanAsync(destination, payload, CancellationToken.None));
        Assert.EndsWith(nameof(Socks5UdpTransport), refusal.ObjectName, StringComparison.Ordinal);

        await serverCancellation.CancelAsync();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public async Task SendsRacingDisposalNeverObserveTheDisposedGate()
    {
        // Regression this catches: a sender that has entered SendSpanAsync but not yet reached the
        // gate must be refused by the disposal guard; it must never get the disposed semaphore's
        // ObjectDisposedException. Only the deliberate transport-level refusal is allowed.
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        using var relaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcpListener.Start();
        relaySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var relayEndpoint = (IPEndPoint)relaySocket.LocalEndPoint!;
        var controlEndpoint = (IPEndPoint)tcpListener.LocalEndpoint;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAssociateOnlyAsync(tcpListener, relayEndpoint, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), serverCancellation.Token);
        var socksServer = new Socks5Server("test", controlEndpoint.Address.ToString(), checked((ushort)controlEndpoint.Port), Username: null, Password: null);
        var transport = await Socks5UdpTransport.CreateAsync(socksServer, new SelfTrafficRegistry(), CancellationToken.None, createControl: null, socketFactory: null);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = "XYZ"u8.ToArray();

        var senders = new Task[4];
        for (var index = 0; index < senders.Length; index++)
        {
            senders[index] = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    try
                    {
                        await transport.SendSpanAsync(destination, payload, CancellationToken.None);
                    }
                    catch (ObjectDisposedException exception)
                    {
                        Assert.DoesNotContain("SemaphoreSlim", exception.ObjectName, StringComparison.Ordinal);
                    }
                    catch (SocketException)
                    {
                        // Disposing the socket faults an in-flight or late send; that is not the gate race.
                    }
                }
            });
        }

        await transport.DisposeAsync();
        await Task.WhenAll(senders);

        await serverCancellation.CancelAsync();
        await IgnoreExpectedCancellationAsync(server);
    }
}
