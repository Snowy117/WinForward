using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;

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
        Assert.Equal(TimeSpan.FromSeconds(10), TcpProxyRelayFactory.s_relayConnectAttemptTimeout);
    }

    [Fact]
    public async Task RelayFactorySurfacesRefusedProxyConnectionFailFast()
    {
        // A refused proxy endpoint fails the attempt immediately (no budget is consumed), so the
        // tightened connect budget must not slow down the typical failure path.
        var refused = new TcpListener(IPAddress.Loopback, 0);
        refused.Start();
        var port = ((IPEndPoint)refused.LocalEndpoint).Port;
        refused.Stop();

        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        using var local = localPeer;
        var accepted = new TcpAcceptedConnection(relayLocal, Endpoint.From(IPAddress.Loopback, 40000));
        var factory = new TcpProxyRelayFactory(new SelfTrafficRegistry());
        var server = new Socks5Server("refused", "127.0.0.1", checked((ushort)port), Username: null, Password: null);

        var started = Stopwatch.StartNew();
        await Assert.ThrowsAsync<IOException>(() =>
            factory.EstablishAsync(Endpoint.From(IPAddress.Parse("192.0.2.9"), 80), accepted, server, CancellationToken.None).AsTask());
        started.Stop();

        Assert.True(started.Elapsed < TcpProxyRelayFactory.s_relayConnectAttemptTimeout,
            string.Create(CultureInfo.InvariantCulture, $"a refused connect must fail fast, took {started.Elapsed.TotalMilliseconds:F0}ms"));
        relayLocal.Dispose();
    }

    [Fact]
    public async Task HalfClosePropagatesFinAndAllowsReverseResponse()
    {
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        var (upstreamPeer, relayUpstream) = await CreateSocketPairAsync();
        using var local = localPeer;
        using var upstream = upstreamPeer;
        await using var relayUpstreamStream = new NetworkStream(relayUpstream, ownsSocket: true);
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

    [Fact]
    public async Task MidStreamFailureCancelsSiblingPumpAfterRepeatedStallWindowRearms()
    {
        // P1/X8a: the stall window is one reused CTS per direction, re-armed at most once per
        // second via TryReset. After both pumps have completed several read+write cycles, a
        // fault in one pump must still cancel the sibling immediately through the surviving
        // lifetime-token link — re-arming (and throttling it) must never unlink session
        // cancellation.
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        var (upstreamPeer, relayUpstream) = await CreateSocketPairAsync();
        using var local = localPeer;
        using var upstream = upstreamPeer;
        await using var relayUpstreamStream = new NetworkStream(relayUpstream, ownsSocket: true);
        await using var relay = new TcpProxyRelay(relayLocal, new FaultAfterWritesStream(relayUpstreamStream, allowedWrites: 1), new NoopAsyncDisposable());

        await local.SendAsync(new byte[] { 1, 2, 3 }, SocketFlags.None);
        var request = new byte[3];
        Assert.Equal(3, await upstream.ReceiveAsync(request, SocketFlags.None));
        await upstream.SendAsync(new byte[] { 4, 5 }, SocketFlags.None);
        var response = new byte[2];
        Assert.Equal(2, await local.ReceiveAsync(response, SocketFlags.None));

        await local.SendAsync(new byte[] { 6 }, SocketFlags.None);
        await Assert.ThrowsAsync<IOException>(async () => await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void StallRearmThrottleIsOneSecond()
    {
        Assert.Equal(Stopwatch.Frequency, TcpProxyRelay.s_armThrottleTicks);
    }

    [Fact]
    public void StallRearmIsDueOnlyForFirstArmAndAfterThrottleInterval()
    {
        // X8a: the first arm is unconditional; within one second of the last arm the re-arm is
        // skipped (the previous arm's 30-minute window still covers the operations), and an
        // arm past the interval goes through.
        var now = Stopwatch.GetTimestamp();
        var halfSecond = TcpProxyRelay.s_armThrottleTicks / 2;

        Assert.True(TcpProxyRelay.IsRearmDue(0, now));
        Assert.False(TcpProxyRelay.IsRearmDue(now, now + halfSecond));
        Assert.False(TcpProxyRelay.IsRearmDue(now, now + TcpProxyRelay.s_armThrottleTicks));
        Assert.True(TcpProxyRelay.IsRearmDue(now, now + TcpProxyRelay.s_armThrottleTicks + 1));
        Assert.True(TcpProxyRelay.IsRearmDue(now, now + (10 * TcpProxyRelay.s_armThrottleTicks)));
    }

    [Fact]
    public async Task PumpWindowPoolRentsOneLeasePerDirectionAndReturnsBothOnCompletion()
    {
        using var pool = new NativeBufferPool(TcpProxyRelayFactory.PumpBufferSize, capacity: 4);
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        var (upstreamPeer, relayUpstream) = await CreateSocketPairAsync();
        using var local = localPeer;
        using var upstream = upstreamPeer;
        await using var relayUpstreamStream = new NetworkStream(relayUpstream, ownsSocket: true);
        await using var relay = new TcpProxyRelay(relayLocal, relayUpstreamStream, new NoopAsyncDisposable(), pumpBufferPool: pool);

        // One lease per pump direction, held for the whole pump lifetime.
        await WaitForAsync(() => pool.Stats.Outstanding == 2);
        Assert.Equal(TcpProxyRelayFactory.PumpBufferSize, pool.BufferSize);

        await local.SendAsync(new byte[] { 1, 2, 3 }, SocketFlags.None);
        local.Shutdown(SocketShutdown.Send);
        var request = new byte[3];
        Assert.Equal(3, await upstream.ReceiveAsync(request, SocketFlags.None));
        Assert.Equal(0, await ReceiveWithTimeoutAsync(upstream));

        await upstream.SendAsync(new byte[] { 4, 5 }, SocketFlags.None);
        upstream.Shutdown(SocketShutdown.Send);
        var response = new byte[2];
        Assert.Equal(2, await local.ReceiveAsync(response, SocketFlags.None));
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        await WaitForAsync(() => pool.Stats.Outstanding == 0);
        Assert.Equal(2, pool.Stats.Rented);
        Assert.Equal(2, pool.Stats.Returned);
        Assert.Equal(2, pool.Stats.InPool);
        Assert.Equal(0, pool.Stats.DisposedCount);
    }

    [Fact]
    public async Task DisposeLeavesCompletionCompletedAndReturnsPumpBuffers()
    {
        // R1: the relay owns its pump lifetimes. DisposeAsync must not return while a pump is
        // still running: it awaits the completion, so the disposal-manufactured fault is observed
        // and swallowed rather than surfacing as an unobserved task exception, and both pooled
        // direction buffers are already back by the time it returns.
        using var pool = new NativeBufferPool(TcpProxyRelayFactory.PumpBufferSize, capacity: 4);
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        using var local = localPeer;
        await using var relay = new TcpProxyRelay(relayLocal, new FaultingStream(), new NoopAsyncDisposable(), pumpBufferPool: pool);

        await WaitForAsync(() => pool.Stats.Outstanding == 2);
        await local.SendAsync(new byte[] { 1 }, SocketFlags.None);

        // ReSharper disable once DisposeOnUsingVariable // The test awaits this DisposeAsync to assert the post-disposal state; the await using stays as the dispose-on-failure safety net and the repeat disposal is an idempotent no-op.
        // D-C3-3: one pump is parked in a read that never completes on its own, so only the
        // disposal ordering (seal + cancel, socket close, then drain) can make this return.
        await relay.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(relay.Completion.IsCompleted);
        await WaitForAsync(() => pool.Stats.Outstanding == 0);
        Assert.Equal(0, pool.Stats.DisposedCount);
    }

    [Fact]
    public async Task ConcurrentDisposalRunsTheOwnerTeardownOnce()
    {
        // D11: the scope's single-flight covers only the drain, so the one-shot claim is what keeps
        // two disposal callers from running the socket/control teardown twice. The control's
        // disposal is counted and gated: the second caller has to join the drain while the first is
        // parked in the owner teardown, so a double-run would show a count of two.
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        using var local = localPeer;
        var control = new CountingControlDisposable();
        await using var relay = new TcpProxyRelay(relayLocal, new FaultingStream(), control);

        var first = DisposeRelayAsync(relay);
        var second = DisposeRelayAsync(relay);
        try
        {
            await WaitForAsync(() => control.DisposeCount >= 1);
            await Task.Delay(50);
            // A double-run parks a second caller in the gated control disposal; the finally
            // releases the gate so the regression surfaces as a count assertion, not a hang.
            Assert.Equal(1, control.DisposeCount);
        }
        finally
        {
            control.Release();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, control.DisposeCount);
    }

    private static async Task DisposeRelayAsync(TcpProxyRelay relay) => await relay.DisposeAsync().ConfigureAwait(false);

    private static async Task<(Socket Peer, Socket Relay)> CreateSocketPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
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

    /// <summary>Counts owner-teardown invocations and holds them until released, so a caller is provably mid-teardown.</summary>
    private sealed class CountingControlDisposable : IAsyncDisposable
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Release() => _gate.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            await _gate.Task.ConfigureAwait(false);
        }
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

    /// <summary>Transparently relays reads and the first N writes, then faults every later write.</summary>
    private sealed class FaultAfterWritesStream(Stream inner, int allowedWrites) : Stream
    {
        private int _remainingWrites = allowedWrites;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Decrement(ref _remainingWrites) < 0) throw new IOException("relay write failed");
            inner.Write(buffer, offset, count);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Interlocked.Decrement(ref _remainingWrites) < 0 ? Task.FromException(new IOException("relay write failed")) : inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => Interlocked.Decrement(ref _remainingWrites) < 0 ? ValueTask.FromException(new IOException("relay write failed")) : inner.WriteAsync(buffer, cancellationToken);

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
