using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class TcpProxyRelayTests
{
    [Fact]
    public void RelayFactoryCapsSocks5ConnectBudgetToTenSecondsTwoAttempts()
    {
        // D3: the redirect leg completes the client's handshake in tens of milliseconds, so the
        // relay's upstream budget bounds the client's perceived failure window. The call site
        // binds to these constants, which cap the worst case at DNS + 2 x 10s instead of the
        // 4 x 30s per-attempt defaults (worst case ~150s).
        Assert.Equal(2, TcpProxyRelayFactory.RelayConnectMaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(10), TcpProxyRelayFactory.RelayConnectAttemptTimeout);
    }

    [Fact]
    public async Task RelayFactorySurfacesRefusedProxyConnectionFailFast()
    {
        // A refused proxy endpoint fails the attempt immediately (no budget is consumed), so the
        // tightened connect budget must not slow down the typical failure path.
        var refused = new TcpListener(IPAddress.Loopback, 0);
        refused.Start();
        var port = ((IPEndPoint)refused.LocalEndpoint!).Port;
        refused.Stop();

        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        using var local = localPeer;
        var accepted = new TcpAcceptedConnection(relayLocal, Endpoint.From(IPAddress.Loopback, 40000));
        var factory = new TcpProxyRelayFactory(new SelfTrafficRegistry());
        var server = new Socks5Server("refused", "127.0.0.1", checked((ushort)port), null, null);

        var started = Stopwatch.StartNew();
        await Assert.ThrowsAsync<IOException>(() =>
            factory.EstablishAsync(Endpoint.From(IPAddress.Parse("192.0.2.9"), 80), accepted, server, CancellationToken.None).AsTask());
        started.Stop();

        Assert.True(started.Elapsed < TcpProxyRelayFactory.RelayConnectAttemptTimeout,
            $"a refused connect must fail fast, took {started.Elapsed.TotalMilliseconds:F0}ms");
        local.Dispose();
        relayLocal.Dispose();
    }

    [Fact]
    public async Task HalfClosePropagatesFinAndAllowsReverseResponse()
    {
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        var (upstreamPeer, relayUpstream) = await CreateSocketPairAsync();
        using var local = localPeer;
        using var upstream = upstreamPeer;
        using var relayUpstreamStream = new NetworkStream(relayUpstream, ownsSocket: true);
        await using var relay = new TcpProxyRelay(relayLocal, relayUpstreamStream, new NoopAsyncDisposable());

        await local.SendAsync(new byte[] { 1, 2, 3 }, SocketFlags.None);
        local.Shutdown(SocketShutdown.Send);

        var request = new byte[3];
        Assert.Equal(3, await upstream.ReceiveAsync(request, SocketFlags.None));
        Assert.Equal(new byte[] { 1, 2, 3 }, request);
        Assert.Equal(0, await ReceiveWithTimeoutAsync(upstream));

        await upstream.SendAsync(new byte[] { 4, 5 }, SocketFlags.None);
        upstream.Shutdown(SocketShutdown.Send);

        var response = new byte[2];
        Assert.Equal(2, await local.ReceiveAsync(response, SocketFlags.None));
        Assert.Equal(new byte[] { 4, 5 }, response);
        Assert.Equal(0, await ReceiveWithTimeoutAsync(local));
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OneSidedRelayFailureCancelsSiblingPumpImmediately()
    {
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        using var local = localPeer;
        await using var relay = new TcpProxyRelay(relayLocal, new FaultingStream(), new NoopAsyncDisposable());

        await local.SendAsync(new byte[] { 1 }, SocketFlags.None);
        await Assert.ThrowsAsync<IOException>(async () => await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static async Task<(Socket Peer, Socket Relay)> CreateSocketPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint!);
            var relay = await listener.AcceptSocketAsync();
            return (peer, relay);
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

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FaultingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("relay write failed");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WaitForCancellationAsync(cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(WaitForCancellationAsync(cancellationToken));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException(new IOException("relay write failed"));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("relay write failed"));

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
