using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WinForward.Benchmarks.Perf;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

internal static class UdpLossScenario
{
    private static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(2);
    private const int TickMilliseconds = 10;

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        await using var receiver = new EchoReceiver();
        await using var server = new LoopbackSocks5UdpServer(receiver.Endpoint);
        var sink = new CountingUdpResponseSink();
        var coordinator = new UdpProxyCoordinator(new Socks5UdpTransportFactory(new SelfTrafficRegistry()), sink, options.Flows);
        SenderStats stats;
        try
        {
            var socksServer = new Socks5Server("soak", "127.0.0.1", checked((ushort)server.ControlEndpoint.Port), null, null);
            var flows = new FlowKey[options.Flows];
            for (var index = 0; index < flows.Length; index++) flows[index] = BenchmarkShared.CreateFlowKey(index);
            stats = await RunSenderAsync(coordinator, socksServer, flows, options).ConfigureAwait(false);
            await Task.Delay(DrainTime).ConfigureAwait(false);
        }
        finally
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
        }

        var lossRate = stats.SentDatagrams == 0 ? 0.0 : Math.Clamp(1.0 - receiver.Received / (double)stats.SentDatagrams, 0.0, 1.0);
        var achievedPps = stats.ElapsedSeconds > 0.0 ? stats.SentDatagrams / stats.ElapsedSeconds : 0.0;
        context.WriteResult(
            "udp.lossRate",
            new { pps = options.Pps, durationSeconds = options.DurationSeconds, payloadBytes = options.PayloadBytes, flows = options.Flows },
            new
            {
                sentDatagrams = stats.SentDatagrams,
                destinationReceived = receiver.Received,
                lossRate,
                outOfOrder = receiver.OutOfOrder,
                duplicates = receiver.Duplicates,
                responsesInjected = sink.ResponsesInjected,
                achievedPps,
                sendLoopOverflows = stats.SendLoopOverflows,
            });
    }

    private static async Task<SenderStats> RunSenderAsync(UdpProxyCoordinator coordinator, Socks5Server server, FlowKey[] flows, SoakOptions options)
    {
        var sequences = new long[flows.Length];
        var payload = new byte[options.PayloadBytes];
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
                    var flow = nextFlow++ % flows.Length;
                    sequences[flow]++;
                    BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(0, 8), (ulong)sequences[flow]);
                    BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8, 4), flow);
                    if (await coordinator.TrySendAsync(flows[flow], server, payload, cancellation.Token).ConfigureAwait(false)) sent++;
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
            // The duration elapsed; the sender exits through the drain phase.
        }

        return new SenderStats(sent, overflows, stopwatch.Elapsed.TotalSeconds);
    }

    private sealed record SenderStats(long SentDatagrams, long SendLoopOverflows, double ElapsedSeconds);

    private sealed class EchoReceiver : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly Task _loop;
        private readonly Dictionary<int, FlowState> _flows = new();

        public EchoReceiver()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.ReceiveBufferSize = 4 << 20;
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Endpoint = (IPEndPoint)_socket.LocalEndPoint!;
            _loop = Task.Run(ReceiveLoopAsync);
        }

        public IPEndPoint Endpoint { get; }

        public long Received { get; private set; }

        public long OutOfOrder { get; private set; }

        public long Duplicates { get; private set; }

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

                var payload = buffer.AsMemory(0, result.ReceivedBytes);
                if (payload.Length >= 12)
                {
                    var sequence = BinaryPrimitives.ReadInt64BigEndian(payload.Span[..8]);
                    var flowId = BinaryPrimitives.ReadInt32BigEndian(payload.Span.Slice(8, 4));
                    if (!_flows.TryGetValue(flowId, out var state))
                    {
                        state = new FlowState();
                        _flows.Add(flowId, state);
                    }

                    if (sequence > state.LastSeen)
                    {
                        state.LastSeen = sequence;
                    }
                    else if (state.RingContains(sequence))
                    {
                        Duplicates++;
                    }
                    else
                    {
                        OutOfOrder++;
                    }

                    state.Push(sequence);
                    Received++;
                }

                try
                {
                    _ = await _socket.SendToAsync(payload, SocketFlags.None, result.RemoteEndPoint!).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // A relay socket that vanished mid-echo must not end the measurement loop.
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }
    }

    private sealed class FlowState
    {
        private readonly long[] _recent = new long[64];
        private int _count;
        private int _next;

        public long LastSeen { get; set; }

        public bool RingContains(long sequence)
        {
            for (var index = 0; index < _count; index++)
            {
                if (_recent[index] == sequence) return true;
            }

            return false;
        }

        public void Push(long sequence)
        {
            _recent[_next] = sequence;
            _next = (_next + 1) % _recent.Length;
            if (_count < _recent.Length) _count++;
        }
    }

    private sealed class CountingUdpResponseSink : IUdpResponseSink
    {
        private long _injected;

        public long ResponsesInjected => Interlocked.Read(ref _injected);

        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _injected);
            return ValueTask.CompletedTask;
        }
    }
}
