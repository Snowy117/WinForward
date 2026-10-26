using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Runtime.TcpRedirect.Tests;

[SupportedOSPlatform("windows")]
public sealed class TcpProxyRelayFactoryTests
{
    private static readonly IPAddress s_originalDestination = IPAddress.Parse("192.0.2.53");
    private static readonly byte[] s_requestPayload = [1, 2, 3];
    private static readonly byte[] s_responsePayload = [9, 8, 7];

    [Fact]
    public void RawSocketOverloadAndEndKindArePublicForAnOutOfAssemblyBackend()
    {
        // The friend assembly compiles against internals too, so only reflection proves the seam an
        // out-of-assembly backend composes with (it owns the accepted socket instead of using the
        // redirect listener) is actually public.
        var overload = typeof(TcpProxyRelayFactory).GetMethod(
            nameof(TcpProxyRelayFactory.EstablishAsync),
            [typeof(Endpoint), typeof(Socket), typeof(Socks5Server), typeof(CancellationToken)]);

        Assert.NotNull(overload);
        Assert.True(overload.IsPublic);
        Assert.True(typeof(ITcpRelayEndInfo).IsPublic);
        Assert.True(typeof(RelayEndKind).IsPublic);
    }

    [Fact]
    public async Task RawSocketRelayPumpsBothDirectionsAndReportsACleanEnd()
    {
        await using var upstream = FakeSocks5Upstream.StartClean(s_responsePayload);
        var (peer, accepted) = await CreateSocketPairAsync();
        using var client = peer;
        var factory = new TcpProxyRelayFactory(new SelfTrafficRegistry());
        var destination = Endpoint.From(s_originalDestination, 443);

        var relay = await factory.EstablishAsync(destination, accepted, upstream.Server, CancellationToken.None);
        Assert.False(accepted.IsDisposedValue);
        // The raw entry replaces the listener, whose accept puts TCP_NODELAY on the client socket:
        // both relay legs are byte pipes, so the wrapped socket must come out Nagle-free.
        Assert.True(accepted.NoDelay);
        try
        {
            Assert.Equal(destination, await upstream.ReadConnectDestinationAsync());

            await client.SendAsync(s_requestPayload, SocketFlags.None);
            client.Shutdown(SocketShutdown.Send);

            var response = new byte[s_responsePayload.Length];
            Assert.Equal(s_responsePayload.Length, await client.ReceiveAsync(response, SocketFlags.None));
            Assert.Equal(s_responsePayload, response);
            // A clean end closes the client leg plainly: the peer reads the FIN (0), not a reset.
            Assert.Equal(0, await ReceiveWithTimeoutAsync(client));

            Assert.Equal(s_requestPayload, await upstream.Forwarded.WaitAsync(TimeSpan.FromSeconds(5)));

            await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            var endInfo = Assert.IsType<ITcpRelayEndInfo>(relay, exactMatch: false);
            Assert.Equal(RelayEndKind.CleanEnded, endInfo.EndKind);
            Assert.Equal(s_responsePayload.Length, endInfo.ServerStreamBytes);
        }
        finally
        {
            await relay.DisposeAsync();
        }

        // Ownership transferred at establishment: the relay—not the caller—closed the socket it was
        // handed. The test never disposes it, so any second owner would show up as a double close.
        Assert.True(accepted.IsDisposedValue);
    }

    [Fact]
    public async Task RawSocketRelayReportsAFaultedEndAndResetsTheClientWhenTheUpstreamResets()
    {
        await using var upstream = FakeSocks5Upstream.StartFaulting();
        var (peer, accepted) = await CreateSocketPairAsync();
        using var client = peer;
        var factory = new TcpProxyRelayFactory(new SelfTrafficRegistry());

        var relay = await factory.EstablishAsync(
            Endpoint.From(s_originalDestination, 443), accepted, upstream.Server, CancellationToken.None);
        Assert.False(accepted.IsDisposedValue);
        try
        {
            upstream.Abort();

            var fault = await Assert.ThrowsAnyAsync<Exception>(
                async () => await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(
                fault is IOException or SocketException,
                $"an upstream reset must fault the relay, got {fault.GetType().Name}");

            var endInfo = Assert.IsType<ITcpRelayEndInfo>(relay, exactMatch: false);
            Assert.Equal(RelayEndKind.Faulted, endInfo.EndKind);

            // The raw entry arms the abortive close, and the relay closes the socket before Completion
            // faults: the client leg must observe the reset, not the FIN a plain close would send. A
            // linger write after Completion cannot produce this, because the socket is gone by then.
            var reset = await Assert.ThrowsAsync<SocketException>(async () => await ReceiveWithTimeoutAsync(client));
            Assert.Equal(SocketError.ConnectionReset, reset.SocketErrorCode);
        }
        finally
        {
            await relay.DisposeAsync();
        }

        Assert.True(accepted.IsDisposedValue);
    }

    [Fact]
    public async Task AcceptedConnectionEntryKeepsThePlainCloseWhenTheUpstreamResets()
    {
        // The packet path's relay must not arm the abortive close: there the client-visible abort is
        // the acceptor's crafted RST|ACK, so the relay's own local close stays a plain FIN.
        await using var upstream = FakeSocks5Upstream.StartFaulting();
        var (peer, accepted) = await CreateSocketPairAsync();
        using var client = peer;
        var factory = new TcpProxyRelayFactory(new SelfTrafficRegistry());
        var acceptedConnection = new TcpAcceptedConnection(accepted, Endpoint.From(IPAddress.Loopback, 40000));

        var relay = await factory.EstablishAsync(
            Endpoint.From(s_originalDestination, 443), acceptedConnection, upstream.Server, CancellationToken.None);
        try
        {
            upstream.Abort();

            await Assert.ThrowsAnyAsync<Exception>(
                async () => await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            var endInfo = Assert.IsType<ITcpRelayEndInfo>(relay, exactMatch: false);
            Assert.Equal(RelayEndKind.Faulted, endInfo.EndKind);

            // No reset: the client leg reads the FIN (0 bytes) the plain close sent.
            Assert.Equal(0, await ReceiveWithTimeoutAsync(client));
        }
        finally
        {
            await relay.DisposeAsync();
        }

        Assert.True(accepted.IsDisposedValue);
    }

    [Fact]
    public async Task RawSocketOverloadRejectsASocketWithoutARemoteEndpoint()
    {
        using var unconnected = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var factory = new TcpProxyRelayFactory(new SelfTrafficRegistry());
        var server = new Socks5Server("unused", "127.0.0.1", 1080, Username: null, Password: null);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => factory.EstablishAsync(Endpoint.From(s_originalDestination, 443), unconnected, server, CancellationToken.None).AsTask());

        Assert.Equal("acceptedSocket", exception.ParamName);
    }

    [Fact]
    public async Task RawSocketOverloadLeavesTheSocketWithTheCallerWhenEstablishmentFails()
    {
        var refused = new TcpListener(IPAddress.Loopback, 0);
        refused.Start();
        var port = ((IPEndPoint)refused.LocalEndpoint).Port;
        refused.Stop();

        var (peer, accepted) = await CreateSocketPairAsync();
        using var client = peer;
        var factory = new TcpProxyRelayFactory(new SelfTrafficRegistry());
        var server = new Socks5Server("refused", "127.0.0.1", checked((ushort)port), Username: null, Password: null);

        await Assert.ThrowsAsync<IOException>(
            () => factory.EstablishAsync(Endpoint.From(s_originalDestination, 443), accepted, server, CancellationToken.None).AsTask());

        // Ownership transfers only on success: the caller still owns the socket and closes it itself.
        Assert.False(accepted.IsDisposedValue);
        accepted.Dispose();
        Assert.True(accepted.IsDisposedValue);
    }

    private static async Task<(Socket Peer, TrackingSocket Accepted)> CreateSocketPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = new TrackingSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await accepted.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var peer = await listener.AcceptSocketAsync();
            return (peer, accepted);
        }
        catch
        {
            accepted.Dispose();
            throw;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<int> ReceiveWithTimeoutAsync(Socket socket)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await socket.ReceiveAsync(new byte[1], SocketFlags.None, cancellation.Token);
    }

    /// <summary>
    /// A loopback SOCKS5 upstream: it negotiates no-auth, records the CONNECT request and, depending
    /// on the script, either forwards the relayed bytes back with a scripted response or aborts the
    /// connection with a reset once the test asks it to.
    /// </summary>
    private sealed class FakeSocks5Upstream : IAsyncDisposable
    {
        private static readonly byte[] s_methodSelection = [5, 0];
        private static readonly byte[] s_connectSuccess = [5, 0, 0, 1, 127, 0, 0, 1, 0, 0];
        private static readonly byte[] s_greeting = [5, 1, 0];

        private readonly TcpListener _listener;
        private readonly byte[]? _response;
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<byte[]> _connectRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<byte[]> _forwarded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _abort = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serve;

        private FakeSocks5Upstream(byte[]? response)
        {
            _response = response;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _serve = ServeAsync();
        }

        public Socks5Server Server => new(
            "fake",
            "127.0.0.1",
            checked((ushort)((IPEndPoint)_listener.LocalEndpoint).Port),
            Username: null,
            Password: null);

        public Task<byte[]> Forwarded => _forwarded.Task;

        public static FakeSocks5Upstream StartClean(byte[] response) => new(response);

        public static FakeSocks5Upstream StartFaulting() => new(response: null);

        /// <summary>Resets the established connection with an abortive close (linger 0).</summary>
        public void Abort() => _abort.TrySetResult();

        public async Task<Endpoint> ReadConnectDestinationAsync()
        {
            var request = await _connectRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new byte[] { 5, 1, 0, 1 }, request[..4]);
            return Endpoint.From(
                new IPAddress(request.AsSpan(4, 4)),
                BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8, 2)));
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _serve.ConfigureAwait(false);
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptSocketAsync(_stop.Token);
                await using var stream = new NetworkStream(client, ownsSocket: false);

                var greeting = new byte[s_greeting.Length];
                await stream.ReadExactlyAsync(greeting, _stop.Token);
                Assert.Equal(s_greeting, greeting);
                await stream.WriteAsync(s_methodSelection, _stop.Token);

                _connectRequest.TrySetResult(await ReadConnectRequestAsync(stream));
                await stream.WriteAsync(s_connectSuccess, _stop.Token);

                if (_response is null)
                {
                    await _abort.Task.WaitAsync(_stop.Token);
                    // The using-scope disposal closes with linger 0, which is an abortive close: the
                    // relay's upstream read observes a reset, not a FIN.
                    client.LingerState = new LingerOption(enable: true, seconds: 0);
                    return;
                }

                _forwarded.TrySetResult(await ReadUntilEndAsync(stream));
                await stream.WriteAsync(_response, _stop.Token);
                client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // Test teardown interrupts the script; the relay-side outcome is what the test asserts.
            }
        }

        private async Task<byte[]> ReadConnectRequestAsync(Stream stream)
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, _stop.Token);
            var address = new byte[4];
            await stream.ReadExactlyAsync(address, _stop.Token);
            var port = new byte[2];
            await stream.ReadExactlyAsync(port, _stop.Token);
            return [.. header, .. address, .. port];
        }

        private async Task<byte[]> ReadUntilEndAsync(Stream stream)
        {
            var buffer = new byte[256];
            await using var received = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, _stop.Token)) != 0)
            {
                await received.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            }

            return received.ToArray();
        }
    }
}
