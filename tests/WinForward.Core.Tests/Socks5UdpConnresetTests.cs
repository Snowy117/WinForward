using System.Net;
using System.Net.Sockets;
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
/// S2 socket-level contracts for the UDP relay transport: SIO_UDP_CONNRESET is applied to the relay
/// socket before bind (asserted through the injectable seam — the Linux test host cannot execute
/// vendor IOCTLs), and an ICMP-driven ConnectionReset on the receive path is classified as a skip
/// instead of a socket-level failure.
/// </summary>
public sealed class Socks5UdpConnresetTests
{
    [Fact]
    public async Task RelaySocketDisablesUdpConnectionResetBeforeBind()
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
        TrackingSocket? socket = null;
        var disableCalls = new List<Socket>();
        bool? boundAtDisableCall = null;

        var transport = await Socks5UdpTransport.CreateAsync(
            socksServer,
            new SelfTrafficRegistry(),
            CancellationToken.None,
            null,
            family => socket = new TrackingSocket(family),
            disableUdpConnectionReset: candidate =>
            {
                boundAtDisableCall = candidate.LocalEndPoint is not null;
                disableCalls.Add(candidate);
            });

        Assert.Single(disableCalls);
        Assert.Same(socket, disableCalls[0]);
        Assert.False(boundAtDisableCall ?? true, "SIO_UDP_CONNRESET must be applied before the relay socket binds.");
        Assert.NotNull(transport.LocalEndpoint);

        await transport.DisposeAsync();
        serverCancellation.Cancel();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public void ReceiveFaultClassificationMapsOnlyConnectionResetToSkip()
    {
        Assert.Equal(Socks5UdpReceiveSkipReason.ConnectionReset,
            Socks5UdpTransport.ClassifyReceiveFault(new SocketException((int)SocketError.ConnectionReset)));
        Assert.Null(Socks5UdpTransport.ClassifyReceiveFault(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Null(Socks5UdpTransport.ClassifyReceiveFault(new SocketException((int)SocketError.SocketError)));
        Assert.Null(Socks5UdpTransport.ClassifyReceiveFault(new ObjectDisposedException("socket")));
        Assert.Null(Socks5UdpTransport.ClassifyReceiveFault(new OperationCanceledException()));
    }
}
