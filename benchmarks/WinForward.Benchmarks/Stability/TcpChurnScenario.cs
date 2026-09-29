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
/// New-flow churn through the real TCP redirect relay path (research F8): <c>--rate</c> connections
/// per second, each one dialled, SOCKS5-established, sent a small payload and closed, with
/// <c>--tcp-concurrency</c> workers in flight. Reports the per-connection first-byte distribution —
/// p50 through max, never a mean alone — plus establishment outcomes and the transient allocation the
/// churn produces.
/// <para>
/// The finding under test is that per-flow attribution work runs <em>on the pump thread</em> and
/// therefore shows up as a latency tail rather than a uniform slowdown. That shape is reproducible
/// without Windows through <c>--attribution-delay-ms</c> plus <c>--attribution-delay-percent</c>: the
/// stall is applied only to the selected fraction of connections, modelling the real rule (attribution
/// runs for the flows a process rule matches, not for every flow). Both classes are reported
/// separately, so "the tail moved and the middle did not" is readable straight from one artifact.
/// </para>
/// <para>
/// <c>--attribution-delay-percent</c> is realized as one connection in every
/// <c>max(1, 100 / min(100, percent))</c>, so 5 % and 1 % are exact while e.g. 30 % lands on 33 %; the
/// realized share is the delayed class's reported count over the attempts, which is what a reader
/// should compare against, not the requested percentage.
/// </para>
/// <para>
/// Report-only: the verdict row carries the load, the attribution settings and <c>gated: false</c>. On
/// Windows the same row can be produced with no synthetic stall, where the real attributor supplies it.
/// </para>
/// </summary>
internal static class TcpChurnScenario
{
    /// <summary>Latency samples kept per class; beyond it the run reports how many it dropped rather than silently biasing the tail.</summary>
    private const int MaximumLatencySamples = 131_072;

    private const int ChunkBytes = 64 * 1024;

    private static Endpoint Destination { get; } = Endpoint.From(IPAddress.Parse("192.0.2.80"), 443);

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        await using var server = new LoopbackSocks5TcpServer();
        var socksServer = new Socks5Server("churn", "127.0.0.1", checked((ushort)server.Endpoint.Port), Username: null, Password: null);
        var factory = new TcpProxyRelayFactory(new SelfTrafficRegistry());
        var gate = new RateGate(options.Rate);
        var counters = new ChurnCounters(options);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var gen0Before = GC.CollectionCount(0);
        var clock = Stopwatch.StartNew();
        var duration = TimeSpan.FromSeconds(options.DurationSeconds);
        var workers = new Task[options.TcpConcurrency];
        for (var index = 0; index < workers.Length; index++)
        {
            workers[index] = Task.Run(() => WorkerLoopAsync(factory, socksServer, gate, counters, options, clock, duration));
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        clock.Stop();
        context.WriteResult(
            "tcp.churn",
            new
            {
                rate = options.Rate,
                concurrency = options.TcpConcurrency,
                payloadBytes = options.PayloadBytes,
                durationSeconds = options.DurationSeconds,
                attributionDelayMs = options.AttributionDelayMs,
                attributionDelayPercent = options.AttributionDelayPercent,
            },
            counters.Snapshot(
                clock.Elapsed.TotalSeconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                GC.CollectionCount(0) - gen0Before,
                server));
    }

    private static async Task WorkerLoopAsync(TcpProxyRelayFactory factory, Socks5Server socksServer, RateGate gate, ChurnCounters counters, SoakOptions options, Stopwatch clock, TimeSpan duration)
    {
        while (clock.Elapsed < duration)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            if (clock.Elapsed >= duration) return;
            await RunConnectionAsync(factory, socksServer, counters, options).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One churned connection. The first-byte clock starts after the relay is established, so the
    /// number is the proxied round trip a client would feel, while the synthetic stall (paid by the
    /// selected fraction) sits before establishment, where the pump-side attribution work would be.
    /// </summary>
    private static async Task RunConnectionAsync(TcpProxyRelayFactory factory, Socks5Server socksServer, ChurnCounters counters, SoakOptions options)
    {
        var delayed = counters.NoteAttempt();

        // The clock covers everything the client waits for — dial, the per-flow setup that on Windows
        // includes pump-side attribution, the relay handshake and the first byte — because a stall that
        // is not inside the window is not the effect this row exists to measure.
        var stopwatch = Stopwatch.StartNew();
        Socket? client = null;
        Socket? relayLocal = null;
        ITcpRelay? relay = null;
        try
        {
            var (clientPeer, localSide) = await CreateSocketPairAsync().ConfigureAwait(false);
            client = clientPeer;
            relayLocal = localSide;
            if (delayed && options.AttributionDelayMs > 0) await Task.Delay(options.AttributionDelayMs).ConfigureAwait(false);
            relay = await EstablishRelayAsync(factory, socksServer, relayLocal).ConfigureAwait(false);
            await client.SendAsync(new byte[options.PayloadBytes], SocketFlags.None).ConfigureAwait(false);
            var received = await ReceiveAsync(client, options.PayloadBytes).ConfigureAwait(false);
            stopwatch.Stop();
            if (received != options.PayloadBytes)
            {
                counters.RecordFailure("shortRead");
                return;
            }

            counters.RecordSuccess(stopwatch.Elapsed.TotalMilliseconds, delayed, received);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException or TimeoutException)
        {
            counters.RecordFailure(exception.GetType().Name);
        }
        finally
        {
            if (relay is not null) await relay.DisposeAsync().ConfigureAwait(false);
            relayLocal?.Dispose();
            client?.Dispose();
        }
    }

    /// <summary>Drains the echoed payload; the caller's clock already covers the wait for its first byte.</summary>
    private static async Task<long> ReceiveAsync(Socket client, int expected)
    {
        var buffer = new byte[Math.Min(ChunkBytes, expected)];
        var count = await client.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
        long received = count;
        while (received < expected)
        {
            count = await client.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
            if (count == 0) break;
            received += count;
        }

        return received;
    }

    private static async Task<ITcpRelay> EstablishRelayAsync(TcpProxyRelayFactory factory, Socks5Server socksServer, Socket relayLocal)
    {
        var peer = (IPEndPoint)relayLocal.RemoteEndPoint!;
        var accepted = new TcpAcceptedConnection(relayLocal, Endpoint.From(peer.Address, checked((ushort)peer.Port)));
        return await factory.EstablishAsync(Destination, accepted, socksServer, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<(Socket Peer, Socket Relay)> CreateSocketPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint).ConfigureAwait(false);
            return (peer, await listener.AcceptSocketAsync().ConfigureAwait(false));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Aggregate pacing: workers take the next slot from one shared schedule, so <c>--rate</c> is the
    /// run's connection rate rather than each worker's.
    /// </summary>
    private sealed class RateGate(int rate)
    {
        private readonly Lock _lock = new();
        private long _nextTicks;

        public async Task WaitAsync()
        {
            TimeSpan delay;
            lock (_lock)
            {
                var interval = Stopwatch.Frequency / (double)Math.Max(1, rate);
                var now = Stopwatch.GetTimestamp();
                if (_nextTicks < now) _nextTicks = now;
                delay = TimeSpan.FromSeconds((_nextTicks - now) / Stopwatch.Frequency);
                _nextTicks += (long)interval;
            }

            if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
        }
    }

    private sealed class ChurnCounters(SoakOptions options)
    {
        /// <summary>Artifact rounding: the analyzer requires an explicit midpoint mode, and ToEven is the runtime's own default.</summary>
        private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.ToEven);

        private readonly Lock _lock = new();
        private readonly double[] _stableFirstByteMs = new double[MaximumLatencySamples];
        private readonly double[] _delayedFirstByteMs = new double[MaximumLatencySamples];
        /// <summary>Bounded failure-reason buffer: only the first <c>_failures</c> slots are written, so the rest stay null and the snapshot filters them out.</summary>
        private readonly string?[] _failureReasons = new string?[16];
        private int _attempts;
        private int _successes;
        private int _failures;
        private int _stableSamples;
        private int _delayedSamples;
        private int _droppedSamples;
        private long _stableFirstByteMicroseconds;
        private long _stableReceivedBytes;

        /// <summary>Registers one attempt and answers whether this connection is one of the delayed fraction.</summary>
        public bool NoteAttempt()
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (options.AttributionDelayMs <= 0 || options.AttributionDelayPercent <= 0) return false;
            var period = Math.Max(1, 100 / Math.Min(100, options.AttributionDelayPercent));
            return attempt % period == 0;
        }

        public void RecordSuccess(double firstByteMs, bool delayed, long receivedBytes)
        {
            lock (_lock)
            {
                _successes++;
                _stableReceivedBytes += receivedBytes;
                var samples = delayed ? _delayedFirstByteMs : _stableFirstByteMs;
                var count = delayed ? _delayedSamples : _stableSamples;
                if (count >= samples.Length)
                {
                    _droppedSamples++;
                    return;
                }

                samples[count] = firstByteMs;
                if (delayed)
                {
                    _delayedSamples = count + 1;
                }
                else
                {
                    _stableSamples = count + 1;
                    _stableFirstByteMicroseconds += (long)(firstByteMs * 1000);
                }
            }
        }

        public void RecordFailure(string reason)
        {
            lock (_lock)
            {
                _failures++;
                if (_failures <= _failureReasons.Length) _failureReasons[_failures - 1] = reason;
            }
        }

        public object Snapshot(double seconds, long allocatedBytes, int gen0Collections, LoopbackSocks5TcpServer server)
        {
            lock (_lock)
            {
                return new
                {
                    attempts = _attempts,
                    successes = _successes,
                    failures = _failures,
                    failureReasons = _failureReasons.Where(reason => reason is not null).Distinct(StringComparer.Ordinal).ToArray(),
                    droppedLatencySamples = _droppedSamples,
                    connectionsPerSecond = Round(_successes / seconds, 1),
                    stable = Describe(_stableFirstByteMs, _stableSamples, _stableSamples == 0 ? 0 : _stableFirstByteMicroseconds / 1000.0 / _stableSamples),
                    delayed = Describe(_delayedFirstByteMs, _delayedSamples, incrementalMeanMs: null),
                    receivedBytes = _stableReceivedBytes,
                    allocatedBytesPerSecond = Round(allocatedBytes / seconds, 0),
                    allocatedBytesPerConnection = _attempts == 0 ? 0 : Round((double)allocatedBytes / _attempts, 0),
                    gen0Collections,
                    serverConnectReplies = server.ConnectReplies,
                    serverBytesEchoed = server.BytesEchoed,
                    gated = false,
                    note = "Report-only churn row (design §3). The delayed class exists only when --attribution-delay-ms is set; on Windows the same shape comes from the real attributor, so this row is the instrument and the Windows run is the number.",
                };
            }
        }

        /// <summary>Distribution of one class; the delayed class is the F8 tail, the stable class is its control.</summary>
        private static object Describe(double[] samples, int count, double? incrementalMeanMs)
        {
            if (count == 0) return new { count = 0 };
            var copy = new double[count];
            Array.Copy(samples, copy, count);
            var distribution = LatencyDistribution.FromMilliseconds(copy);
            return new
            {
                count,
                meanMs = incrementalMeanMs ?? Round(distribution.Mean, 3),
                minMs = Round(distribution.Min, 3),
                p50Ms = Round(distribution.P50, 3),
                p95Ms = Round(distribution.P95, 3),
                p99Ms = Round(distribution.P99, 3),
                maxMs = Round(distribution.Max, 3),
            };
        }
    }
}

#pragma warning restore CA1416
