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
    private static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarmupPollInterval = TimeSpan.FromMilliseconds(50);
    private const int TickMilliseconds = 10;

    /// <summary>
    /// Opt-in switch for the trace-capturing product-event census. Enabling Trace makes the
    /// product emit per-datagram events (udp.packet.sent/received), which allocates and slows
    /// the send/receive paths; default runs must stay undistorted, so productEvents is omitted
    /// from the row unless this is flipped for a loss-localization session. The Interlocked
    /// hops counters stay unconditional (zero distortion).
    /// </summary>
    private const bool CaptureProductEvents = false;

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        await using var receiver = new EchoReceiver(options.Flows);
        await using var server = new LoopbackSocks5UdpServer(receiver.Endpoint);
        var sink = new CountingUdpResponseSink();
        var productEvents = CaptureProductEvents ? new CountingRuntimeLogger() : null;
        var coordinator = new UdpProxyCoordinator(new Socks5UdpTransportFactory(new SelfTrafficRegistry(), UdpFrameBuilder.DefaultMaximumEthernetFrame), sink, options.Flows, logger: productEvents is not null ? productEvents : NullRuntimeLogger.Instance);
        SenderStats stats;
        try
        {
            var socksServer = new Socks5Server("soak", "127.0.0.1", checked((ushort)server.ControlEndpoint.Port), null, null);
            var flows = new FlowKey[options.Flows];
            for (var index = 0; index < flows.Length; index++) flows[index] = BenchmarkShared.CreateFlowKey(index);
            var sequences = new long[flows.Length];
            var payload = new byte[options.PayloadBytes];
            await WarmupAsync(coordinator, socksServer, flows, sequences, payload, receiver).ConfigureAwait(false);
            var markers = (long[])sequences.Clone();
            receiver.BeginWindow(markers);
            sink.BeginWindow(markers);
            stats = await RunWindowAsync(coordinator, socksServer, flows, sequences, payload, options).ConfigureAwait(false);
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
                hops = new
                {
                    relayReceived = server.RelayReceived,
                    relayDecodeDropped = server.RelayDecodeDropped,
                    relayForwarded = server.RelayForwarded,
                    relayReplies = server.RelayReplies,
                    relaySendFaults = server.RelaySendFaults,
                },
                productEvents = productEvents is not null ? StabilityShared.BuildProductEvents(productEvents) : null,
            });
    }

    private static async Task WarmupAsync(UdpProxyCoordinator coordinator, Socks5Server server, FlowKey[] flows, long[] sequences, byte[] payload, EchoReceiver receiver)
    {
        for (var flow = 0; flow < flows.Length; flow++)
        {
            sequences[flow]++;
            DatagramHeader.Write(payload, sequences[flow], flow);
            _ = await coordinator.TrySendAsync(flows[flow], server, payload, CancellationToken.None).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        while (receiver.ObservedFlowCount < flows.Length && stopwatch.Elapsed < WarmupTimeout)
        {
            await Task.Delay(WarmupPollInterval).ConfigureAwait(false);
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
            // The window elapsed; the sender exits through the drain phase.
        }

        return new SenderStats(sent, overflows, stopwatch.Elapsed.TotalSeconds);
    }

    private sealed record SenderStats(long SentDatagrams, long SendLoopOverflows, double ElapsedSeconds);

    private sealed class CountingUdpResponseSink : IUdpResponseSink
    {
        private long _injected;
        private volatile long[]? _windowMarkers;

        public long ResponsesInjected => Interlocked.Read(ref _injected);

        public void BeginWindow(long[] markers) => _windowMarkers = markers;

        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken)
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
