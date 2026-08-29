#pragma warning disable CA1416 // TcpProxyRelayFactory/TcpAcceptedConnection/TcpProxyRelay carry SupportedOSPlatform(windows) but are platform-neutral managed code; only their production wiring is Windows-specific.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// SOCKS5 TCP throughput soak over the largest Linux-testable sub-chain: a real SOCKS5
/// handshake (greeting + CONNECT through a loopback fake server) followed by a full-duplex
/// relayed transfer of the configured byte count per iteration. The fake server echoes every
/// post-reply byte, so each iteration verifies the byte count in both relay directions. The
/// NDIS reinjection legs are Windows-only and intentionally out of scope. <c>socks5</c> mode
/// (default) wires the relay through <see cref="TcpProxyRelayFactory.EstablishAsync"/>;
/// <c>bare</c> mode wires the identical relay around a plain upstream socket pair with an
/// in-worker echo task, providing the same-shape control denominator for the SOCKS5-layer
/// throughput acceptance ratio (socks5/bare ≥ 0.70).
/// </summary>
internal static class TcpThroughputScenario
{
    private static readonly TimeSpan RelaySettleTimeout = TimeSpan.FromSeconds(30);
    private const int ChunkBytes = 65_536;
    private const int MaximumErrorSamples = 8;

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        await using var server = options.TcpRelayMode == TcpRelayMode.Socks5 ? new LoopbackSocks5TcpServer() : null;
        var socksServer = server is null ? null : new Socks5Server("throughput", "127.0.0.1", checked((ushort)server.Endpoint.Port), null, null);
        var factory = new TcpProxyRelayFactory(new SelfTrafficRegistry());
        var counters = new ThroughputCounters();
        var clock = Stopwatch.StartNew();
        var duration = TimeSpan.FromSeconds(options.DurationSeconds);
        var workers = new Task[options.TcpConcurrency];
        for (var index = 0; index < workers.Length; index++)
        {
            workers[index] = Task.Run(() => WorkerLoopAsync(factory, socksServer, options, counters, clock, duration));
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        context.WriteResult(
            "tcp.throughput",
            new
            {
                relayMode = options.TcpRelayMode == TcpRelayMode.Bare ? "bare" : "socks5",
                concurrency = options.TcpConcurrency,
                transferBytes = options.TcpTransferBytes,
                durationSeconds = options.DurationSeconds,
            },
            counters.Snapshot(server, options.TcpRelayMode));
    }

    private static async Task WorkerLoopAsync(TcpProxyRelayFactory factory, Socks5Server? socksServer, SoakOptions options, ThroughputCounters counters, Stopwatch clock, TimeSpan duration)
    {
        while (clock.Elapsed < duration)
        {
            await RunTransferAsync(factory, socksServer, options, counters).ConfigureAwait(false);
        }
    }

    private static async Task RunTransferAsync(TcpProxyRelayFactory factory, Socks5Server? socksServer, SoakOptions options, ThroughputCounters counters)
    {
        var stopwatch = Stopwatch.StartNew();
        Socket? client = null;
        Socket? localSide = null;
        Socket? upstreamPeer = null;
        ITcpRelay? relay = null;
        try
        {
            var (clientPeer, relayLocal) = await CreateSocketPairAsync().ConfigureAwait(false);
            client = clientPeer;
            localSide = relayLocal;

            var handshakeStopwatch = Stopwatch.StartNew();
            if (options.TcpRelayMode == TcpRelayMode.Bare)
            {
                // The finally disposes the echo peer after the relay so the echo task's pending
                // receive ends through teardown instead of leaking.
                (relay, upstreamPeer) = await CreateBareRelayAsync(relayLocal, counters).ConfigureAwait(false);
            }
            else
            {
                relay = await EstablishSocks5RelayAsync(factory, socksServer!, relayLocal).ConfigureAwait(false);
            }

            var handshakeMicroseconds = handshakeStopwatch.Elapsed.TotalMilliseconds * 1000;

            var sender = Task.Run(() => SendAsync(client, options.TcpTransferBytes));
            var received = await ReceiveAsync(client, options.TcpTransferBytes).ConfigureAwait(false);
            await sender.ConfigureAwait(false);
            try
            {
                await relay.Completion.WaitAsync(RelaySettleTimeout).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or SocketException or ObjectDisposedException or IOException)
            {
                // A half-closed echo peer can leave one pump blocked; the verified byte count
                // classifies the transfer, and the teardown below still releases everything.
            }

            stopwatch.Stop();
            counters.Record(received == options.TcpTransferBytes, received, stopwatch.Elapsed.TotalMilliseconds, handshakeMicroseconds, null);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException)
        {
            stopwatch.Stop();
            counters.Record(false, 0, stopwatch.Elapsed.TotalMilliseconds, 0, $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            if (relay is not null) await relay.DisposeAsync().ConfigureAwait(false);
            localSide?.Dispose();
            upstreamPeer?.Dispose();
            client?.Dispose();
        }
    }

    /// <summary>
    /// Control denominator: the same relay pumps and transfer shape without any SOCKS5
    /// establishment. The echo task mirrors the fake server's per-connection echo loop.
    /// </summary>
    private static async Task<(ITcpRelay Relay, Socket EchoPeer)> CreateBareRelayAsync(Socket relayLocal, ThroughputCounters counters)
    {
        var upstreamPair = await CreateSocketPairAsync().ConfigureAwait(false);
        var upstreamStream = new NetworkStream(upstreamPair.Relay, ownsSocket: true);
        var relay = new TcpProxyRelay(relayLocal, upstreamStream, new UpstreamOwner(upstreamStream));
        _ = Task.Run(() => EchoAsync(upstreamPair.Peer, counters));
        return (relay, upstreamPair.Peer);
    }

    private static async Task<ITcpRelay> EstablishSocks5RelayAsync(TcpProxyRelayFactory factory, Socks5Server socksServer, Socket relayLocal)
    {
        var peer = (IPEndPoint)relayLocal.RemoteEndPoint!;
        var accepted = new TcpAcceptedConnection(relayLocal, Endpoint.From(peer.Address, checked((ushort)peer.Port)));

        // On success the relay takes over the local socket; on failure the factory leaves it with
        // this caller. Socket disposal is idempotent, so the transfer's finally dispose covers
        // both outcomes without double-release.
        return await factory.EstablishAsync(Destination, accepted, socksServer, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>A synthetic original destination; the fake server accepts any CONNECT target.</summary>
    private static Endpoint Destination { get; } = Endpoint.From(IPAddress.Parse("192.0.2.80"), 443);

    /// <summary>Bare-mode stand-in for the fake server's per-connection echo loop (same 64 KiB chunking).</summary>
    private static async Task EchoAsync(Socket echoPeer, ThroughputCounters counters)
    {
        var buffer = new byte[ChunkBytes];
        try
        {
            while (true)
            {
                var count = await echoPeer.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
                if (count == 0) break;
                counters.NoteEchoed(count);
                await echoPeer.SendAsync(buffer.AsMemory(0, count), SocketFlags.None).ConfigureAwait(false);
            }

            // The fake server's connection handler returns into a using-disposed NetworkStream,
            // closing the whole accepted socket; propagate the half-close explicitly here so the
            // relay's upstream pump sees its EOF and Completion settles without the 30s timeout.
            echoPeer.Shutdown(SocketShutdown.Send);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // The transfer teardown closes the echo peer; the echoed-byte cross-check ends with it.
        }
    }

    /// <summary>Owns the bare-mode upstream stream for the relay's control disposal (mirrors TcpEofScenario).</summary>
    private sealed class UpstreamOwner(Stream upstream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            upstream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task SendAsync(Socket client, long transferBytes)
    {
        var buffer = new byte[ChunkBytes];
        long written = 0;
        while (written < transferBytes)
        {
            var count = (int)Math.Min(buffer.Length, transferBytes - written);
            await client.SendAsync(buffer.AsMemory(0, count), SocketFlags.None).ConfigureAwait(false);
            written += count;
        }

        client.Shutdown(SocketShutdown.Send);
    }

    private static async Task<long> ReceiveAsync(Socket client, long expected)
    {
        var buffer = new byte[ChunkBytes];
        long received = 0;
        while (received < expected)
        {
            var count = await client.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
            if (count == 0) break;
            received += count;
        }

        return received;
    }

    private static async Task<(Socket Peer, Socket Relay)> CreateSocketPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint!).ConfigureAwait(false);
            return (peer, await listener.AcceptSocketAsync().ConfigureAwait(false));
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class ThroughputCounters
    {
        private readonly Lock _gate = new();
        private readonly List<string> _errorSamples = [];
        private long _transfers;
        private long _completed;
        private long _failed;
        private long _bytesVerified;
        private long _bytesEchoed;
        private double _totalMilliseconds;
        private double _totalHandshakeMicroseconds;

        public void Record(bool completed, long received, double milliseconds, double handshakeMicroseconds, string? sample)
        {
            lock (_gate)
            {
                _transfers++;
                _totalMilliseconds += milliseconds;
                if (!completed)
                {
                    _failed++;
                    if (sample is not null && _errorSamples.Count < MaximumErrorSamples && !_errorSamples.Contains(sample))
                    {
                        _errorSamples.Add(sample);
                    }

                    return;
                }

                _completed++;
                _bytesVerified += received;
                _totalHandshakeMicroseconds += handshakeMicroseconds;
            }
        }

        public void NoteEchoed(long count)
        {
            lock (_gate) _bytesEchoed += count;
        }

        public object Snapshot(LoopbackSocks5TcpServer? server, TcpRelayMode mode)
        {
            lock (_gate)
            {
                // Per-direction throughput: every completed transfer moved transferBytes in
                // each direction through the relay, so this is directly comparable between the
                // socks5 and bare modes; the relay actually carried twice this rate.
                var throughputMBs = _totalMilliseconds > 0.0 ? _bytesVerified / _totalMilliseconds / 1000.0 : 0.0;
                return new
                {
                    transfers = _transfers,
                    completed = _completed,
                    failed = _failed,
                    bytesVerified = _bytesVerified,
                    relayedTotalBytes = _bytesVerified * 2,
                    throughputMBs,
                    meanHandshakeMicroseconds = _completed == 0 ? 0.0 : _totalHandshakeMicroseconds / _completed,
                    meanTransferMilliseconds = _transfers == 0 ? 0.0 : _totalMilliseconds / _transfers,
                    serverBytesEchoed = server?.BytesEchoed,
                    serverConnectReplies = server?.ConnectReplies,
                    echoTaskBytesEchoed = mode == TcpRelayMode.Bare ? _bytesEchoed : (long?)null,
                    errorSamples = _errorSamples.ToArray(),
                };
            }
        }
    }
}

#pragma warning restore CA1416
