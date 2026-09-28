using System.Diagnostics;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// UDP loss soak through the real proxy dial path. Every metric counts only the steady-state
/// window: warmup first establishes all flows, per-flow sequence markers snapshot at window
/// start, and the post-window drain still credits in-window stragglers, so flow-establishment
/// and teardown-tail datagrams are excluded by design.
/// </summary>
internal static class UdpLossScenario
{
    private static readonly TimeSpan s_drainTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_warmupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_warmupPollInterval = TimeSpan.FromMilliseconds(50);
    private const int TickMilliseconds = 10;

    /// <summary>
    /// Opt-in switch for the verbose product-event census. The row always carries the warn-level
    /// census, whose events are rate-limited or one-shot and therefore cost nothing on the datagram
    /// paths; flipping this adds the per-datagram trace/debug counts, which allocate and slow the
    /// send/receive paths, so it is reserved for a loss-localization session. The Interlocked hops
    /// counters stay unconditional (zero distortion).
    /// </summary>
    private const bool CaptureProductEvents = false;

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        await using var receiver = new EchoReceiver(options.Flows);
        await using var server = new LoopbackSocks5UdpServer(receiver.Endpoint);
        var sink = new CountingUdpResponseSink();
        var productEvents = new CountingRuntimeLogger(CaptureProductEvents);
        await using var scope = new CoordinatorScope(sink, options.Flows, productEvents);
        var coordinator = scope.Coordinator;
        SenderStats stats;
        try
        {
            var socksServer = new Socks5Server("soak", "127.0.0.1", checked((ushort)server.ControlEndpoint.Port), Username: null, Password: null);
            var flows = new FlowKey[options.Flows];
            for (var index = 0; index < flows.Length; index++) flows[index] = BenchmarkShared.CreateFlowKey(index);
            var sequences = new long[flows.Length];
            var payload = new byte[options.PayloadBytes];
            await WarmupAsync(coordinator, socksServer, flows, sequences, payload, receiver).ConfigureAwait(false);
            var markers = (long[])sequences.Clone();
            receiver.BeginWindow(markers);
            sink.BeginWindow(markers);
            stats = await RunWindowAsync(coordinator, socksServer, flows, sequences, payload, options).ConfigureAwait(false);
            await Task.Delay(s_drainTime).ConfigureAwait(false);
        }
        finally
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
        }

        var lossRate = stats.SentDatagrams == 0 ? 0.0 : Math.Clamp(1.0 - (receiver.Received / (double)stats.SentDatagrams), 0.0, 1.0);
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
                hops = new
                {
                    relayReceived = server.RelayReceived,
                    relayDecodeDropped = server.RelayDecodeDropped,
                    relayForwarded = server.RelayForwarded,
                    relayReplies = server.RelayReplies,
                    relaySendFaults = server.RelaySendFaults,
                },
                productEvents = StabilityShared.BuildProductEvents(productEvents),
            });
    }

    private static async Task WarmupAsync(UdpProxyCoordinator coordinator, Socks5Server server, FlowKey[] flows, long[] sequences, byte[] payload, EchoReceiver receiver)
    {
        for (var flow = 0; flow < flows.Length; flow++)
        {
            sequences[flow]++;
            DatagramHeader.Write(payload, sequences[flow], flow);
            _ = await coordinator.TrySendSpanAsync(flows[flow], server, payload, default, CancellationToken.None).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        while (receiver.ObservedFlowCount < flows.Length && stopwatch.Elapsed < s_warmupTimeout)
        {
            await Task.Delay(s_warmupPollInterval).ConfigureAwait(false);
        }
    }

    private static async Task<SenderStats> RunWindowAsync(UdpProxyCoordinator coordinator, Socks5Server server, FlowKey[] flows, long[] sequences, byte[] payload, SoakOptions options)
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
                    var flow = nextFlow++ % flows.Length;
                    sequences[flow]++;
                    DatagramHeader.Write(payload, sequences[flow], flow);
                    if (await coordinator.TrySendSpanAsync(flows[flow], server, payload, default, cancellation.Token).ConfigureAwait(false)) sent++;
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
    /// Owns the borrowed native pools and setup executor a coordinator requires (Phase A / R6) and
    /// disposes them after the coordinator, since a coordinator never disposes its collaborators.
    /// </summary>
    private sealed class CoordinatorScope : IAsyncDisposable
    {
        private readonly NativeBufferPool _setupQueuePool;
        private readonly NativeBufferPool _receiveWindowPool;
        private readonly SetupExecutor _setupExecutor;
        private readonly UdpAssociationPool _associations;

        public CoordinatorScope(IUdpResponseSink sink, int capacity, IRuntimeLogger logger)
        {
            const int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;
            _setupQueuePool = new NativeBufferPool(maximumFrameSize);
            _receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));
            _setupExecutor = new SetupExecutor();
            var registry = new SelfTrafficRegistry();
            // The acceptance run exercises the production default: share, with passive detection.
            // The census logger goes to the pool too, because the fallback warn is a pool event.
            _associations = new UdpAssociationPool(registry, UdpAssociationReuseMode.Auto, logger: logger);
            Coordinator = new UdpProxyCoordinator(
                new Socks5UdpTransportFactory(_associations, registry, maximumFrameSize),
                sink,
                _setupQueuePool,
                _receiveWindowPool,
                _setupExecutor,
                new UdpProxyOptions { Capacity = capacity, Logger = logger });
        }

        public UdpProxyCoordinator Coordinator { get; }

        public async ValueTask DisposeAsync()
        {
            // The coordinator drained every session (and with it every association lease) before
            // the pool is closed.
            await _associations.DisposeAsync().ConfigureAwait(false);
            _setupExecutor.Dispose();
            _receiveWindowPool.Dispose();
            _setupQueuePool.Dispose();
        }
    }

    private sealed class CountingUdpResponseSink : IUdpResponseSink
    {
        private long _injected;
        private volatile long[]? _windowMarkers;

        public long ResponsesInjected => Interlocked.Read(ref _injected);

        public void BeginWindow(long[] markers) => _windowMarkers = markers;

        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken)
        {
            if (DatagramHeader.TryRead(payload.Span, out var sequence, out var flowId)
                && DatagramHeader.IsInWindow(sequence, flowId, _windowMarkers))
            {
                Interlocked.Increment(ref _injected);
            }

            return ValueTask.CompletedTask;
        }
    }
}
