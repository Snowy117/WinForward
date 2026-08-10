using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
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

        var associate = control.UdpAssociateAsync(new IPEndPoint(IPAddress.Any, 12345), CancellationToken.None).AsTask();
        await commandRead.Task.WaitAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(async () => await associate);
        stopServer.Cancel();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public async Task UdpSocketIsDisposedWhenControlSetupFails()
    {
        var socket = new TrackingSocket(AddressFamily.InterNetwork);
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await Socks5UdpTransport.CreateAsync(
                new Socks5Server("test", "127.0.0.1", 1080, null, null),
                AddressFamily.InterNetwork,
                new SelfTrafficRegistry(),
                CancellationToken.None,
                _ => ValueTask.FromException<Socks5ControlConnection>(new IOException("control setup failed")),
                _ => socket);
        });

        Assert.True(socket.IsDisposedValue);
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

        protected override void Dispose(bool disposing)
        {
            IsDisposedValue = true;
            base.Dispose(disposing);
        }
    }

    private sealed class OrderingRegistration(Func<bool> socketIsDisposed) : IDisposable
    {
        private readonly Func<bool> _socketIsDisposed = socketIsDisposed;

        public bool DisposedAfterSocket { get; private set; }

        public void Dispose() => DisposedAfterSocket = _socketIsDisposed();
    }
}
