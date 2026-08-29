using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// A raw-socket loopback UDP soak mirroring the udp.lossRate topology minus all WinForward
/// product code: one paced sender round-robining over per-flow client sockets, one dedicated
/// forwarder socket per client (the SOCKS5 relay role), and a single echo destination tracking
/// per-flow loss, reordering, and duplicates. Its achieved pps is the environment ceiling that
/// acceptance comparisons measure udp.lossRate against, isolating OS + runtime socket cost from
/// product overhead. Metrics count only the steady-state window after the same warmup shape as
/// udp.lossRate (one datagram per flow, then wait until each flow id is observed at the echo or
/// the bounded timeout elapses), so establishment and teardown tails are excluded by design.
/// </summary>
internal static class UdpRawBaselineScenario
{
    private static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarmupPollInterval = TimeSpan.FromMilliseconds(50);
    private const int TickMilliseconds = 10;

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        await using var echo = new EchoReceiver(options.Flows);
        var forwarders = new List<Forwarder>(options.Flows);
        var clients = new List<ClientReceiver>(options.Flows);
        SenderStats stats;
        try
        {
            for (var index = 0; index < options.Flows; index++)
            {
                forwarders.Add(new Forwarder(echo.Endpoint));
                clients.Add(new ClientReceiver());
            }

            var sequences = new long[clients.Count];
            var payload = new byte[options.PayloadBytes];
            await WarmupAsync(clients, forwarders, sequences, payload, echo).ConfigureAwait(false);
            var markers = (long[])sequences.Clone();
            echo.BeginWindow(markers);
            foreach (var client in clients) client.BeginWindow(markers);

            stats = await RunWindowAsync(clients, forwarders, sequences, payload, options).ConfigureAwait(false);
            await Task.Delay(DrainTime).ConfigureAwait(false);
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var forwarder in forwarders)
            {
                await forwarder.DisposeAsync().ConfigureAwait(false);
            }
        }

        var responsesInjected = 0L;
        foreach (var client in clients) responsesInjected += client.Received;

        var lossRate = stats.SentDatagrams == 0 ? 0.0 : Math.Clamp(1.0 - echo.Received / (double)stats.SentDatagrams, 0.0, 1.0);
        var achievedPps = stats.ElapsedSeconds > 0.0 ? stats.SentDatagrams / stats.ElapsedSeconds : 0.0;
        context.WriteResult(
            "udp.rawBaseline",
            new { pps = options.Pps, durationSeconds = options.DurationSeconds, payloadBytes = options.PayloadBytes, flows = options.Flows },
            new
            {
                sentDatagrams = stats.SentDatagrams,
                destinationReceived = echo.Received,
                lossRate,
                outOfOrder = echo.OutOfOrder,
                duplicates = echo.Duplicates,
                responsesInjected,
                achievedPps,
                sendLoopOverflows = stats.SendLoopOverflows,
            });
    }

    private static async Task WarmupAsync(IReadOnlyList<ClientReceiver> clients, IReadOnlyList<Forwarder> forwarders, long[] sequences, byte[] payload, EchoReceiver echo)
    {
        for (var flow = 0; flow < clients.Count; flow++)
        {
            sequences[flow]++;
            DatagramHeader.Write(payload, sequences[flow], flow);
            _ = await clients[flow].TrySendAsync(payload, forwarders[flow].Endpoint, CancellationToken.None).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        while (echo.ObservedFlowCount < clients.Count && stopwatch.Elapsed < WarmupTimeout)
        {
            await Task.Delay(WarmupPollInterval).ConfigureAwait(false);
        }
    }

    private static async Task<SenderStats> RunWindowAsync(IReadOnlyList<ClientReceiver> clients, IReadOnlyList<Forwarder> forwarders, long[] sequences, byte[] payload, SoakOptions options)
    {
        long sent = 0;
        long overflows = 0;
        var perTick = options.Pps * TickMilliseconds / 1000.0;
        var accumulator = 0.0;
        var tick = TimeSpan.FromMilliseconds(TickMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        var nextDeadline = stopwatch.Elapsed;
        var nextFlow = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSeconds));
        try
        {
            while (true)
            {
                accumulator += perTick;
                var datagrams = (int)accumulator;
                accumulator -= datagrams;
                for (var index = 0; index < datagrams; index++)
                {
                    var flow = nextFlow++ % clients.Count;
                    sequences[flow]++;
                    DatagramHeader.Write(payload, sequences[flow], flow);
                    if (await clients[flow].TrySendAsync(payload, forwarders[flow].Endpoint, cancellation.Token).ConfigureAwait(false)) sent++;
                }

                nextDeadline += tick;
                var remaining = nextDeadline - stopwatch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    overflows++;
                    nextDeadline = stopwatch.Elapsed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The window elapsed; the sender exits through the drain phase.
        }

        return new SenderStats(sent, overflows, stopwatch.Elapsed.TotalSeconds);
    }

    private sealed record SenderStats(long SentDatagrams, long SendLoopOverflows, double ElapsedSeconds);

    /// <summary>
    /// One forwarder socket per flow, mirroring the harness SOCKS5 relay role without the codec:
    /// client datagrams go to the echo destination (remembering the client's source endpoint),
    /// echo replies go back to that last client. Buffer sizing matches the harness relay (4 MiB).
    /// </summary>
    private sealed class Forwarder : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly Task _loop;
        private readonly IPEndPoint _echoDestination;
        private IPEndPoint? _lastClient;

        public Forwarder(IPEndPoint echoDestination)
        {
            _echoDestination = echoDestination;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.ReceiveBufferSize = 4 << 20;
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Endpoint = (IPEndPoint)_socket.LocalEndPoint!;
            _loop = Task.Run(ReceiveLoopAsync);
        }

        public IPEndPoint Endpoint { get; }

        public async ValueTask DisposeAsync()
        {
            _socket.Dispose();
            await _loop.ConfigureAwait(false);
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[65_536];
            EndPoint anySource = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anySource).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var sender = (IPEndPoint)result.RemoteEndPoint!;
                var payload = buffer.AsMemory(0, result.ReceivedBytes);
                try
                {
                    if (IsEchoSource(sender))
                    {
                        if (_lastClient is not null)
                        {
                            _ = await _socket.SendToAsync(payload, SocketFlags.None, _lastClient).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        _lastClient = sender;
                        _ = await _socket.SendToAsync(payload, SocketFlags.None, _echoDestination).ConfigureAwait(false);
                    }
                }
                catch (SocketException)
                {
                    // The peer vanished mid-datagram; the loop keeps serving the soak.
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        private bool IsEchoSource(IPEndPoint sender) => sender.Port == _echoDestination.Port && sender.Address.Equals(_echoDestination.Address);
    }

    /// <summary>
    /// One client socket per flow, mirroring the product per-session relay socket (512 KiB
    /// receive buffer): the paced sender transmits through it and its receive loop counts the
    /// in-window datagrams that returned through the forwarder.
    /// </summary>
    private sealed class ClientReceiver : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly Task _loop;
        private volatile long[]? _windowMarkers;
        private long _received;

        public ClientReceiver()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.ReceiveBufferSize = 512 << 10;
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _loop = Task.Run(ReceiveLoopAsync);
        }

        public long Received => Interlocked.Read(ref _received);

        public void BeginWindow(long[] markers) => _windowMarkers = markers;

        public async ValueTask DisposeAsync()
        {
            _socket.Dispose();
            await _loop.ConfigureAwait(false);
        }

        public async Task<bool> TrySendAsync(ReadOnlyMemory<byte> payload, IPEndPoint target, CancellationToken cancellationToken)
        {
            try
            {
                _ = await _socket.SendToAsync(payload, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (SocketException)
            {
                // A failed raw send mirrors a failed proxy send: not counted, loop continues.
                return false;
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[65_536];
            EndPoint anySource = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anySource).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var payload = buffer.AsMemory(0, result.ReceivedBytes);
                if (DatagramHeader.TryRead(payload.Span, out var sequence, out var flowId)
                    && DatagramHeader.IsInWindow(sequence, flowId, _windowMarkers))
                {
                    Interlocked.Increment(ref _received);
                }
            }
        }
    }
}
