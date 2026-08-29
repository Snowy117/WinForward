using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;
using WinForward.Windows;

namespace WinForward.Benchmarks;

internal static class Program
{
    private static long s_sink;

    public static async Task<int> Main(string[] args)
    {
        var options = BenchmarkOptions.Parse(args);
        await using var output = options.OutputPath is null
            ? null
            : new StreamWriter(options.OutputPath, append: false);

        var context = new BenchmarkContext(options, output);
        context.WriteMetadata();

        foreach (var frameSize in options.FrameSizes)
        {
            await RunParserBenchmarksAsync(context, frameSize).ConfigureAwait(false);
            await RunNativeBufferBenchmarksAsync(context, frameSize).ConfigureAwait(false);
        }

        foreach (var cardinality in options.Cardinalities)
        {
            await RunFlowTableBenchmarksAsync(context, cardinality).ConfigureAwait(false);
            await RunSelfTrafficBenchmarksAsync(context, cardinality).ConfigureAwait(false);
        }

        foreach (var sessionCount in new[] { 1, 100, 1_000 })
        {
            await RunUdpSessionBenchmarkAsync(context, sessionCount).ConfigureAwait(false);
        }

        await RunDispatcherBenchmarksAsync(context).ConfigureAwait(false);
        await RunCapturePumpBenchmarkAsync(context).ConfigureAwait(false);

        if (options.IncludeRelay)
        {
            foreach (var chunkSize in options.RelayChunkSizes)
            {
                await RunRelayBenchmarkAsync(context, chunkSize).ConfigureAwait(false);
            }
        }

        GC.KeepAlive(Volatile.Read(ref s_sink));
        return 0;
    }

#pragma warning disable MA0051 // The benchmark matrix is easier to audit when its paired cases stay together.
    private static async ValueTask RunParserBenchmarksAsync(BenchmarkContext context, int frameSize)
    {
        var payloadLength = frameSize - 14 - 20 - 8;
        var frame = CreateIpv4UdpFrame(frameSize);
        var destination = IPAddress.Parse("192.0.2.53");
        var socksFrame = Socks5UdpCodec.Encode(destination, 53, frame.AsSpan(frame.Length - payloadLength));

        await context.RunAsync(
            "parser.ipv4Udp",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["frameBytes"] = frameSize },
            context.Options.Count,
            iterations =>
            {
                long value = 0;
                for (var index = 0; index < iterations; index++)
                {
                    if (!IPTcpUdpPacket.TryParse(frame, out var packet)) throw new InvalidOperationException("Parser rejected the benchmark frame.");
                    value += packet.SourcePort + packet.DestinationPort;
                }
                Volatile.Write(ref s_sink, value);
                return ValueTask.FromResult((long)frameSize * iterations);
            }).ConfigureAwait(false);

        await context.RunAsync(
            "parser.ipv4UdpPayload",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["frameBytes"] = frameSize },
            context.Options.Count,
            iterations =>
            {
                long value = 0;
                for (var index = 0; index < iterations; index++)
                {
                    if (!IPUdpPacket.TryParse(frame, out var packet)) throw new InvalidOperationException("UDP parser rejected the benchmark frame.");
                    value += packet.Payload.Length;
                }
                Volatile.Write(ref s_sink, value);
                return ValueTask.FromResult((long)frameSize * iterations);
            }).ConfigureAwait(false);

        await context.RunAsync(
            "socks5Udp.decode",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["payloadBytes"] = payloadLength },
            context.Options.Count,
            iterations =>
            {
                long value = 0;
                for (var index = 0; index < iterations; index++)
                {
                    if (!Socks5UdpCodec.TryDecode(socksFrame, out var packet)) throw new InvalidOperationException("SOCKS5 UDP decoder rejected the benchmark frame.");
                    value += packet.Payload.Length;
                }
                Volatile.Write(ref s_sink, value);
                return ValueTask.FromResult((long)socksFrame.Length * iterations);
            }).ConfigureAwait(false);

        await context.RunAsync(
            "socks5Udp.encode",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["payloadBytes"] = payloadLength },
            Math.Min(context.Options.Count, 100_000),
            iterations =>
            {
                long value = 0;
                var payload = frame.AsSpan(frame.Length - payloadLength);
                for (var index = 0; index < iterations; index++)
                {
                    value += Socks5UdpCodec.Encode(destination, 53, payload).Length;
                }
                Volatile.Write(ref s_sink, value);
                return ValueTask.FromResult((long)(payloadLength + 10) * iterations);
            }).ConfigureAwait(false);

        await context.RunAsync(
            "socks5Udp.encodeSpan",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["payloadBytes"] = payloadLength },
            Math.Min(context.Options.Count, 100_000),
            iterations =>
            {
                long value = 0;
                var payload = frame.AsSpan(frame.Length - payloadLength);
                var reusable = new byte[22 + 65535];
                for (var index = 0; index < iterations; index++)
                {
                    if (!Socks5UdpCodec.TryEncode(IPAddressValue.From(destination), 53, payload, reusable, out var written)) throw new InvalidOperationException("SOCKS5 UDP span encoder rejected the benchmark payload.");
                    value += written;
                }
                Volatile.Write(ref s_sink, value);
                return ValueTask.FromResult((long)(payloadLength + 10) * iterations);
            }).ConfigureAwait(false);
    }
#pragma warning restore MA0051

    private static async ValueTask RunNativeBufferBenchmarksAsync(BenchmarkContext context, int frameSize)
    {
        var frame = CreateIpv4UdpFrame(frameSize);
        var iterations = Math.Min(context.Options.Count, 100_000);
        await context.RunAsync(
            "ndisBuffer.allocateSetDispose",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["frameBytes"] = frameSize },
            iterations,
            count =>
            {
                long value = 0;
                for (var index = 0; index < count; index++)
                {
                    using var buffer = new NdisPacketBuffer();
                    buffer.SetFrame(frame, NdisApiAbi.PacketFlagOnSend, (nint)1);
                    value += buffer.Length;
                }
                Volatile.Write(ref s_sink, value);
                return ValueTask.FromResult((long)frameSize * count);
            }).ConfigureAwait(false);

        await context.RunAsync(
            "ndisBuffer.reuseSet",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["frameBytes"] = frameSize },
            iterations,
            count =>
            {
                long value = 0;
                using var buffer = new NdisPacketBuffer();
                for (var index = 0; index < count; index++)
                {
                    buffer.SetFrame(frame, NdisApiAbi.PacketFlagOnSend, (nint)1);
                    value += buffer.Length;
                }
                Volatile.Write(ref s_sink, value);
                return ValueTask.FromResult((long)frameSize * count);
            }).ConfigureAwait(false);
    }

    private static async ValueTask RunFlowTableBenchmarksAsync(BenchmarkContext context, int cardinality)
    {
        var table = new FlowTable(capacity: Math.Max(1, cardinality + 2));
        for (var index = 0; index < cardinality; index++)
        {
            var key = CreateFlowKey(index);
            if (!table.TryClaimResolved(key, static () => FlowDecision.Fallback(FlowAction.Pass), out _))
            {
                throw new InvalidOperationException("Unable to populate the flow-table benchmark.");
            }
        }

        var missing = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("198.18.255.254"), 65534),
            Endpoint.From(IPAddress.Parse("203.0.113.254"), 65535),
            TransportProtocol.Udp,
            FlowOriginKind.Forwarded,
            new AdapterContext("missing", "missing", 99));
        var hit = cardinality == 0 ? missing : CreateFlowKey(cardinality / 2) with
        {
            Origin = FlowOriginKind.Forwarded,
            OriginAdapterId = "other-adapter",
            OriginAdapterGeneration = 42,
        };
        var iterations = CardinalityIterations(context.Options.Count, cardinality);

        await context.RunAsync(
            "flowTable.resolveMissing",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["cardinality"] = cardinality },
            iterations,
            count =>
            {
                long value = 0;
                for (var index = 0; index < count; index++) value += table.TryResolve(missing, out _) ? 1 : 0;
                Volatile.Write(ref s_sink, value);
                return ValueTask.FromResult(0L);
            }).ConfigureAwait(false);

        if (cardinality != 0)
        {
            await context.RunAsync(
                "flowTable.resolveCrossAdapterHit",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["cardinality"] = cardinality },
                iterations,
                count =>
                {
                    long value = 0;
                    for (var index = 0; index < count; index++) value += table.TryResolve(hit, out var state) ? state!.Generation : 0;
                    Volatile.Write(ref s_sink, value);
                    return ValueTask.FromResult(0L);
                }).ConfigureAwait(false);
        }
    }

    private static async ValueTask RunSelfTrafficBenchmarksAsync(BenchmarkContext context, int cardinality)
    {
        var registry = new SelfTrafficRegistry();
        var tokens = new List<IDisposable>(cardinality);
        for (var index = 0; index < cardinality; index++)
        {
            var remote = Endpoint.From(IPAddress.Parse($"198.19.{index / 256 % 256}.{index % 256}"), checked((ushort)(10_000 + index % 50_000)));
            tokens.Add(registry.Register(new SelfTrafficRegistry.SelfTrafficKey(
                TransportProtocol.Udp,
                Endpoint.From(IPAddress.Any, checked((ushort)(1_024 + index % 50_000))),
                remote)));
        }

        try
        {
            var missing = new FlowContext(
                FlowKey.Create(
                    Endpoint.From(IPAddress.Parse("192.0.2.10"), 60_000),
                    Endpoint.From(IPAddress.Parse("203.0.113.10"), 53),
                    TransportProtocol.Udp,
                    FlowOriginKind.Host),
                null, null, null, null, 53);
            var iterations = CardinalityIterations(context.Options.Count, cardinality);
            await context.RunAsync(
                "selfTraffic.wildcardMiss",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["cardinality"] = cardinality },
                iterations,
                count =>
                {
                    long value = 0;
                    for (var index = 0; index < count; index++) value += registry.IsOwned(missing) ? 1 : 0;
                    Volatile.Write(ref s_sink, value);
                    return ValueTask.FromResult(0L);
                }).ConfigureAwait(false);
        }
        finally
        {
            foreach (var token in tokens) token.Dispose();
        }
    }

    private static async ValueTask RunDispatcherBenchmarksAsync(BenchmarkContext context)
    {
        var passConfiguration = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        var executor = new CountingExecutor();
        var logger = new ThresholdOnlyLogger(RuntimeLogLevel.Info);
        var dispatcher = new FlowDispatcher(passConfiguration, new NeverOwnedGuard(), executor, logger: logger);
        var key = CreateFlowKey(0);

        await context.RunAsync(
            "dispatcher.warmPass.disabledTrace",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["logLevel"] = "info" },
            Math.Min(context.Options.Count, 100_000),
            async iterations =>
            {
                long bytes = 0;
                for (var index = 0; index < iterations; index++)
                {
                    var lease = new PacketLease(new byte[64]);
                    await dispatcher.DispatchAsync(new CapturedFlowPacket(lease, CreateContext(key), PacketSequence: index + 1), CancellationToken.None).ConfigureAwait(false);
                    bytes += lease.Frame.Length;
                }
                Volatile.Write(ref s_sink, executor.PassCount);
                return bytes;
            }).ConfigureAwait(false);
    }

#pragma warning disable CA1416 // The pump and processor are Windows-attributed; the benchmark drives their managed-only pipeline through a fake reader, so it runs on any OS.
    private static async ValueTask RunCapturePumpBenchmarkAsync(BenchmarkContext context)
    {
        foreach (var frameSize in new[] { 128, 1400 })
        {
            foreach (var batchCapacity in new[] { 32, 1 })
            {
                await RunCapturePumpCaseAsync(context, frameSize, batchCapacity).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask RunCapturePumpCaseAsync(BenchmarkContext context, int frameSize, int batchCapacity)
    {
        const int steadyStateRounds = 5;
        var measurements = new CapturePumpMeasurements(frameSize, batchCapacity, distinctFlows: 1_024);
        // The sustained load must outlast the CPU frequency ramp-up, otherwise the first cases are
        // measured at a cold clock: floor the warmup at 100k packets, bounded by the round size.
        var warmupCount = Math.Min(context.Options.Count, Math.Max(context.Options.WarmupCount, 100_000));
        if (warmupCount > 0) _ = await measurements.RunWarmupRoundAsync(warmupCount).ConfigureAwait(false);
        for (var round = 1; round <= steadyStateRounds; round++)
        {
            await context.RunAsync(
                "capturePump.endToEnd",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["frameBytes"] = frameSize,
                    ["batchCapacity"] = batchCapacity,
                    ["round"] = round,
                    ["distinctFlows"] = measurements.DistinctFlows,
                },
                context.Options.Count,
                measurements.RunMeasuredRoundAsync,
                warmupIterations: 0).ConfigureAwait(false);
        }

        context.WriteRecord(new
        {
            type = "result",
            scenario = "capturePump.steadyState",
            parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["frameBytes"] = frameSize,
                ["batchCapacity"] = batchCapacity,
                ["rounds"] = steadyStateRounds,
            },
            steadyStatePps = measurements.RoundPps.Count == 0 ? 0 : measurements.RoundPps.Max(),
            measurements.RoundPps,
            gen0PerMillionPackets = measurements.TotalPackets == 0 ? 0 : measurements.TotalGen0Collections * 1_000_000.0 / measurements.TotalPackets,
        });
    }

    /// <summary>
    /// Runs one finite pump workload per invocation and accumulates the per-round throughput and
    /// Gen0 collection counts the steady-state aggregate is derived from. Warmup invocations run
    /// the same pipeline but are excluded from the accumulated statistics.
    /// </summary>
    private sealed class CapturePumpMeasurements(int frameSize, int batchCapacity, int distinctFlows)
    {
        private long _totalPackets;
        private long _totalGen0Collections;

        public int DistinctFlows { get; } = distinctFlows;
        public List<double> RoundPps { get; } = [];
        public long TotalPackets => _totalPackets;
        public long TotalGen0Collections => _totalGen0Collections;

        public ValueTask<long> RunWarmupRoundAsync(int count) => RunRoundAsync(count, recordStats: false);

        public ValueTask<long> RunMeasuredRoundAsync(int count) => RunRoundAsync(count, recordStats: true);

        private async ValueTask<long> RunRoundAsync(int count, bool recordStats)
        {
            // Fresh pipeline state per invocation so the measured pass count matches the packet count.
            var passConfiguration = new ValidatedConfiguration(
                new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
                new PolicySnapshot([], FlowAction.Pass));
            var executor = new CountingExecutor();
            var logger = new ThresholdOnlyLogger(RuntimeLogLevel.Info);
            var dispatcher = new FlowDispatcher(passConfiguration, new NeverOwnedGuard(), executor, logger: logger);
            var processor = new CapturePacketProcessor(dispatcher, logger);
            var adapter = new WindowsAdapter("bench-adapter", "Benchmark Adapter", @"\DEVICE\{00000000-B3NCH-4ARK-0000-000000000000}", (nint)0x55, 1);
            var frame = CreateIpv4TcpFrame(frameSize);

            using var completion = new CancellationTokenSource();
            var reader = new FiniteCaptureReader(frame, count, DistinctFlows, completion, NdisApiAbi.PacketFlagOnSend);
            await using var pump = new NdisCapturePump(reader, adapter.RuntimeHandle, (packet, cancellationToken) => processor.ProcessAsync(packet, adapter, cancellationToken), TimeSpan.FromMilliseconds(1), batchCapacity);

            var gen0Before = GC.CollectionCount(0);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await pump.RunAsync(completion.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (completion.IsCancellationRequested)
            {
                // Exhaustion cancellation is the pump's normal shutdown path (NdisCapturePumpTests uses the same termination).
            }

            stopwatch.Stop();
            if (recordStats)
            {
                _totalGen0Collections += GC.CollectionCount(0) - gen0Before;
                _totalPackets += count;
                if (count > 0) RoundPps.Add(count / stopwatch.Elapsed.TotalSeconds);
            }

            Volatile.Write(ref s_sink, executor.PassCount);
            if (executor.PassCount != count) throw new InvalidOperationException($"The capture pump benchmark processed {executor.PassCount} of {count} packets.");
            return (long)frameSize * count;
        }

        private static byte[] CreateIpv4TcpFrame(int frameSize)
        {
            if (frameSize < 64 || frameSize > UdpFrameBuilder.MaximumEthernetFrame) throw new ArgumentOutOfRangeException(nameof(frameSize));
            var frame = new byte[frameSize];
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12, 2), 0x0800);
            frame[14] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), checked((ushort)(frameSize - 14)));
            frame[22] = 64;
            frame[23] = 6;
            IPAddress.Parse("192.0.2.10").TryWriteBytes(frame.AsSpan(26, 4), out _);
            IPAddress.Parse("192.0.2.80").TryWriteBytes(frame.AsSpan(30, 4), out _);
            // The source port is rewritten per packet by the capture-pump reader to rotate flow keys.
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 53_000);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 443);
            frame[46] = 0x50;
            for (var index = 54; index < frame.Length; index++) frame[index] = unchecked((byte)index);
            return frame;
        }
    }
#pragma warning restore CA1416

    private static async ValueTask RunUdpSessionBenchmarkAsync(BenchmarkContext context, int sessionCount)
    {
        UdpProxyCoordinator? coordinator = null;
        try
        {
            await context.RunAsync(
                "udpSessions.active",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["sessions"] = sessionCount,
                    ["maximumFrameBytes"] = UdpFrameBuilder.DefaultMaximumEthernetFrame,
                    ["requestedReceiveBufferBytes"] = UdpFrameBuilder.DefaultMaximumEthernetFrame + 23,
                },
                sessionCount,
                async count =>
                {
                    coordinator = new UdpProxyCoordinator(new BenchmarkUdpTransportFactory(), NoopUdpResponseSink.Instance, count);
                    var server = new Socks5Server("benchmark", "127.0.0.1", 1080, null, null);
                    for (var index = 0; index < count; index++)
                    {
                        if (!await coordinator.TrySendAsync(CreateFlowKey(index), server, new byte[] { 1 }, CancellationToken.None).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException("Unable to populate the UDP session benchmark.");
                        }
                    }
                    return 0;
                },
                warmupIterations: 0).ConfigureAwait(false);
        }
        finally
        {
            if (coordinator is not null) await coordinator.DisposeAsync().ConfigureAwait(false);
        }
    }

#pragma warning disable CA1416 // TcpProxyRelay is platform-neutral; only its production factory is Windows-specific.
    private static async ValueTask RunRelayBenchmarkAsync(BenchmarkContext context, int chunkSize)
    {
        var transferBytes = chunkSize == 1 ? 256 * 1024 : 16 * 1024 * 1024;
        await context.RunAsync(
            "tcpRelay.oneWay",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["chunkBytes"] = chunkSize, ["transferBytes"] = transferBytes },
            transferBytes,
            async _ =>
            {
                var (localPeer, relayLocal) = await CreateSocketPairAsync().ConfigureAwait(false);
                var (upstreamPeer, relayUpstream) = await CreateSocketPairAsync().ConfigureAwait(false);
                using (localPeer)
                using (upstreamPeer)
                using (var relayUpstreamStream = new NetworkStream(relayUpstream, ownsSocket: true))
                await using (var relay = new TcpProxyRelay(relayLocal, relayUpstreamStream, NoopAsyncDisposable.Instance))
                {
                    var sendBuffer = new byte[chunkSize];
                    var receiveBuffer = new byte[Math.Max(chunkSize, 8192)];
                    var sender = Task.Run(async () =>
                    {
                        var remaining = transferBytes;
                        while (remaining > 0)
                        {
                            var count = Math.Min(sendBuffer.Length, remaining);
                            await localPeer.SendAsync(sendBuffer.AsMemory(0, count), SocketFlags.None).ConfigureAwait(false);
                            remaining -= count;
                        }
                        localPeer.Shutdown(SocketShutdown.Send);
                    });
                    var received = 0;
                    while (true)
                    {
                        var count = await upstreamPeer.ReceiveAsync(receiveBuffer, SocketFlags.None).ConfigureAwait(false);
                        if (count == 0) break;
                        received += count;
                    }
                    await sender.ConfigureAwait(false);
                    upstreamPeer.Shutdown(SocketShutdown.Send);
                    await relay.Completion.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    Volatile.Write(ref s_sink, received);
                    if (received != transferBytes) throw new InvalidOperationException($"Relay copied {received} of {transferBytes} bytes.");
                }
                return transferBytes;
            }, warmupIterations: 0).ConfigureAwait(false);
    }
#pragma warning restore CA1416

    private static int CardinalityIterations(int requested, int cardinality) => cardinality switch
    {
        >= 65_000 => Math.Min(requested, 1_000),
        >= 16_000 => Math.Min(requested, 5_000),
        >= 1_000 => Math.Min(requested, 20_000),
        _ => requested,
    };

    private static FlowKey CreateFlowKey(int index)
    {
        var first = index / 65_536;
        var second = index % 65_536;
        var local = Endpoint.From(IPAddress.Parse($"10.{first % 256}.{second / 256}.{second % 256}"), checked((ushort)(1_024 + index % 50_000)));
        var remote = Endpoint.From(IPAddress.Parse($"172.{16 + first % 16}.{second / 256}.{second % 256}"), checked((ushort)(1 + index % 65_535)));
        return FlowKey.Create(local, remote, TransportProtocol.Udp, FlowOriginKind.Host, new AdapterContext($"adapter-{index % 4}", null, index % 4));
    }

    private static FlowContext CreateContext(FlowKey key) => new(key, null, null, key.OriginAdapterId, null, key.Remote.Port);

    private static byte[] CreateIpv4UdpFrame(int frameSize)
    {
        if (frameSize < 64 || frameSize > UdpFrameBuilder.MaximumEthernetFrame) throw new ArgumentOutOfRangeException(nameof(frameSize));
        var frame = new byte[frameSize];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12, 2), 0x0800);
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), checked((ushort)(frameSize - 14)));
        frame[22] = 64;
        frame[23] = 17;
        IPAddress.Parse("192.0.2.10").TryWriteBytes(frame.AsSpan(26, 4), out _);
        IPAddress.Parse("192.0.2.53").TryWriteBytes(frame.AsSpan(30, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 53_000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38, 2), checked((ushort)(frameSize - 34)));
        for (var index = 42; index < frame.Length; index++) frame[index] = unchecked((byte)index);
        return frame;
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

    private sealed class NeverOwnedGuard : ISelfTrafficGuard
    {
        public bool IsOwned(FlowContext context) => false;
    }

    private sealed class CountingExecutor : IPacketActionExecutor
    {
        public long PassCount;
        public ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
        {
            PassCount++;
            return ValueTask.CompletedTask;
        }
        public ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class ThresholdOnlyLogger(RuntimeLogLevel threshold) : IRuntimeLogger
    {
        public bool IsEnabled(RuntimeLogLevel level) => level <= threshold;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static readonly NoopAsyncDisposable Instance = new();
        private NoopAsyncDisposable() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BenchmarkUdpTransportFactory : IUdpProxyTransportFactory
    {
        private int _nextPort = 10_000;

        public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IUdpProxyTransport>(new BenchmarkUdpTransport(Interlocked.Increment(ref _nextPort)));
    }

    private sealed class BenchmarkUdpTransport(int localPort) : IUdpProxyTransport
    {
        public IPEndPoint RelayEndpoint { get; } = new(IPAddress.Loopback, 50_000);
        public IPEndPoint LocalEndpoint { get; } = new(IPAddress.Loopback, localPort);

        public ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The benchmark receive should end through cancellation.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopUdpResponseSink : IUdpResponseSink
    {
        public static readonly NoopUdpResponseSink Instance = new();
        private NoopUdpResponseSink() { }
        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Supplies a finite stream of synthetic frames to <see cref="NdisCapturePump"/> without native
    /// hardware. Every call fills as many batch slots as requested (up to the remaining supply) so
    /// the pump never pays the empty-queue poll delay; when the supply is exhausted the reader
    /// cancels the completion token and reports an empty queue, which is the pump's normal
    /// termination path. Each frame gets a rotating TCP source port so the pipeline observes a
    /// bounded mix of first-observation flow claims and cached resolutions.
    /// </summary>
    private sealed class FiniteCaptureReader(byte[] frame, long totalPackets, int distinctFlows, CancellationTokenSource completion, uint deviceFlags) : INdisPacketReader
    {
        private long _remaining = totalPackets;
        private int _sequence;

        public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
        {
            var remaining = _remaining;
            if (remaining <= 0)
            {
                completion.Cancel();
                return 0;
            }

            var count = (int)Math.Min(remaining, (long)buffers.Length);
            for (var index = 0; index < count; index++)
            {
                var sourcePort = (ushort)(1_024 + _sequence % distinctFlows);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), sourcePort);
                buffers[index].SetFrame(frame, deviceFlags, adapterHandle);
                _sequence++;
            }

            _remaining -= count;
            return count;
        }
    }
}

internal sealed record BenchmarkOptions(
    int Count,
    int WarmupCount,
    IReadOnlyList<int> FrameSizes,
    IReadOnlyList<int> Cardinalities,
    IReadOnlyList<int> RelayChunkSizes,
    bool IncludeRelay,
    string? OutputPath)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        var count = 200_000;
        var warmup = 20_000;
        var frameSizes = new[] { 64, 512, 1514 };
        var cardinalities = new[] { 0, 1_000, 16_384, 65_535 };
        var relayChunks = new[] { 1, 1024, 8192, 65_536 };
        var includeRelay = true;
        string? output = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--count": count = ParsePositive(args, ref index); break;
                case "--warmup": warmup = ParseNonNegative(args, ref index); break;
                case "--output": output = ParseString(args, ref index); break;
                case "--no-relay": includeRelay = false; break;
                case "--quick":
                    count = 20_000;
                    warmup = 2_000;
                    cardinalities = [0, 1_000];
                    relayChunks = [1024, 8192];
                    break;
                default: throw new ArgumentException($"Unknown benchmark argument: {args[index]}", nameof(args));
            }
        }
        return new(count, warmup, frameSizes, cardinalities, relayChunks, includeRelay, output);
    }

    private static int ParsePositive(string[] args, ref int index)
    {
        var value = ParseNonNegative(args, ref index);
        return value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(args), "Value must be positive.");
    }

    private static int ParseNonNegative(string[] args, ref int index)
    {
        var value = ParseString(args, ref index);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"Invalid integer benchmark argument: {value}", nameof(args));
    }

    private static string ParseString(string[] args, ref int index)
    {
        index++;
        return index < args.Length ? args[index] : throw new ArgumentException("Missing benchmark argument value.", nameof(args));
    }
}

internal sealed class BenchmarkContext(BenchmarkOptions options, TextWriter? output)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public BenchmarkOptions Options { get; } = options;

    public void WriteMetadata()
    {
        Write(new
        {
            type = "metadata",
            schemaVersion = 1,
            timestampUtc = DateTimeOffset.UtcNow,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            managedOnly = true,
            processId = Environment.ProcessId,
            count = Options.Count,
            warmupCount = Options.WarmupCount,
            frameSizes = Options.FrameSizes,
            cardinalities = Options.Cardinalities,
            relayChunkSizes = Options.RelayChunkSizes,
        });
    }

    /// <summary>
    /// Emits a derived record (for example a multi-round steady-state aggregate) through the same
    /// JSON path as the standard results, so console and file outputs stay consistent.
    /// </summary>
    public void WriteRecord<T>(T value) => Write(value);

    public async ValueTask RunAsync(
        string scenario,
        IReadOnlyDictionary<string, object?> parameters,
        int iterations,
        Func<int, ValueTask<long>> action,
        int? warmupIterations = null)
    {
        var warmup = warmupIterations ?? Math.Min(Options.WarmupCount, iterations);
        if (warmup > 0) _ = await action(warmup).ConfigureAwait(false);
#pragma warning disable S1215 // Forced collections establish a repeatable allocation baseline between cases.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
#pragma warning restore S1215

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        var workingSetBefore = process.WorkingSet64;
        var cpuBefore = process.TotalProcessorTime;
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var stopwatch = Stopwatch.StartNew();
        var copiedBytes = await action(iterations).ConfigureAwait(false);
        stopwatch.Stop();
        process.Refresh();
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
        var cpu = process.TotalProcessorTime - cpuBefore;
        var workingSetDelta = process.WorkingSet64 - workingSetBefore;
        var seconds = stopwatch.Elapsed.TotalSeconds;

        Write(new
        {
            type = "result",
            scenario,
            parameters,
            operations = iterations,
            elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            nanosecondsPerOperation = stopwatch.Elapsed.TotalNanoseconds / iterations,
            operationsPerSecond = iterations / seconds,
            allocatedBytes,
            allocatedBytesPerOperation = (double)allocatedBytes / iterations,
            copiedBytes,
            copiedBytesPerOperation = (double)copiedBytes / iterations,
            cpuMilliseconds = cpu.TotalMilliseconds,
            workingSetDeltaBytes = workingSetDelta,
            gen0Collections = GC.CollectionCount(0) - gen0Before,
            gen1Collections = GC.CollectionCount(1) - gen1Before,
            gen2Collections = GC.CollectionCount(2) - gen2Before,
        });
    }

    private void Write<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, s_jsonOptions);
        Console.WriteLine(json);
        output?.WriteLine(json);
        output?.Flush();
    }
}
