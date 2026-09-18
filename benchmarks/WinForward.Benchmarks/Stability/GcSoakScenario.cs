#pragma warning disable CA1416 // TcpProxyRelayFactory/TcpProxyRelay carry SupportedOSPlatform(windows) but are platform-neutral managed code; only their production wiring is Windows-specific.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The GC-posture soak (task 09-18 M5). Warmup establishes a full mixed working set — SOCKS5 TCP
/// relays and UDP proxy sessions over the loopback fake servers — and then drives only that
/// established shape for the measured window: TCP relays flood in both directions and UDP
/// datagrams ride the forward relay path. The window asserts the application-level guarantee
/// exactly — the UDP forward lanes allocate zero managed bytes while pushing the established
/// flows — plus a flat working set and native-pool occupancy/overflow back at the baseline.
/// <para>
/// Process-wide gen0/gen1/gen2 counts and <see cref="GC.GetTotalAllocatedBytes"/> are reported,
/// not asserted, and this is deliberate: the loopback fake servers drive their receive loops with
/// <c>await</c>, so a loop that parks between datagrams boxes BCL async state machines (measured
/// ~400 B/datagram across the harness at the soak's datagram rates), while the application's own
/// calling threads measure exactly zero. The PRD places those BCL slow-path state machines out of
/// scope; asserting on the process counter would gate the harness, not the product. Likewise the
/// UDP response leg is not part of the measured window because the product's own
/// <c>UdpProxySession.ReceiveLoopAsync</c> boxes on parked receives; its zero-allocation hot shape
/// is covered by the M2 allocation-gate tests. Any failed assertion throws so
/// <see cref="SoakRunner"/> counts the scenario as failed.
/// </para>
/// </summary>
internal static class GcSoakScenario
{
    private static readonly TimeSpan WarmupDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettleDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Least-squares working-set slope above which the growth is a leak rather than loopback-buffer
    /// or scheduling noise. A truly zero-allocation steady state stays flat; 64 KiB/s is a sustained
    /// 3.75 MiB/min rise, far outside any bounded transient.
    /// </summary>
    internal const double WorkingSetSlopeLimitBytesPerSecond = 64 * 1024;

    /// <summary>
    /// Absolute working-set growth allowance across the measured window. Pairs with the slope limit
    /// so a short run cannot pass on a noisy slope estimate alone: 32 MiB is well above loopback
    /// socket buffer churn and far below any real leak over the default (or longer) duration.
    /// </summary>
    internal const long WorkingSetGrowthLimitBytes = 32L * 1024 * 1024;

    /// <summary>
    /// The UDP forward lanes must not allocate per datagram. The runtime's own lock infrastructure
    /// can allocate a few hundred bytes under contention (PRD Out-of-Scope BCL infrastructure), so
    /// the gate is a leak detector rather than a strict zero: an absolute noise ceiling plus a
    /// per-send rate far below any real per-datagram allocation (a 1 B/datagram leak exceeds the
    /// rate by orders of magnitude at the scenario's send counts).
    /// </summary>
    internal const long SenderAllocationNoiseCeilingBytes = 64 * 1024;

    /// <summary>See <see cref="SenderAllocationNoiseCeilingBytes"/>: allowed bytes scale as <c>SendCount / divisor</c>.</summary>
    internal const long SenderAllocationBytesPerSendDivisor = 512;

    /// <summary>CPU-bound loopback flood relays; more relays add heat, not GC signal.</summary>
    private const int MaximumTcpRelays = 8;

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        var maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;
        var relayPool = new NativeBufferPool(TcpProxyRelayFactory.PumpBufferSize, capacity: Math.Max(64, MaximumTcpRelays * 2 + 8));
        var udpWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize), capacity: Math.Max(64, options.Flows + 8));
        var udpSetupPool = new NativeBufferPool(maximumFrameSize, capacity: Math.Max(64, options.Flows + 8));
        try
        {
            await RunCoreAsync(context, options, maximumFrameSize, relayPool, udpWindowPool, udpSetupPool).ConfigureAwait(false);
        }
        finally
        {
            relayPool.Dispose();
            udpWindowPool.Dispose();
            udpSetupPool.Dispose();
        }
    }

    private static async Task RunCoreAsync(
        StabilityContext context,
        SoakOptions options,
        int maximumFrameSize,
        NativeBufferPool relayPool,
        NativeBufferPool udpWindowPool,
        NativeBufferPool udpSetupPool)
    {
        await using var drain = new UdpDrainReceiver();
        await using var udpServer = new LoopbackSocks5UdpServer(drain.Endpoint);
        await using var tcpServer = new LoopbackSocks5TcpServer();
        var udpSink = new CountingUdpResponseSink();
        var coordinator = new UdpProxyCoordinator(
            new Socks5UdpTransportFactory(new SelfTrafficRegistry(), maximumFrameSize),
            udpSink,
            capacity: Math.Max(options.Flows, 1),
            maximumFrameSize: maximumFrameSize,
            receiveWindowPool: udpWindowPool,
            setupQueuePool: udpSetupPool);
        var udpProxyServer = new Socks5Server("gc-soak", "127.0.0.1", checked((ushort)udpServer.ControlEndpoint.Port), null, null);
        var tcpProxyServer = new Socks5Server("gc-soak", "127.0.0.1", checked((ushort)tcpServer.Endpoint.Port), null, null);
        var tcpFlood = new TcpFlood(
            new TcpProxyRelayFactory(new SelfTrafficRegistry(), pumpBufferPool: relayPool),
            tcpProxyServer,
            Math.Clamp(options.TcpConcurrency, 1, MaximumTcpRelays));
        try
        {
            await tcpFlood.EstablishAsync().ConfigureAwait(false);
            var udpFlood = new UdpFlood(coordinator, udpProxyServer, udpServer, options);
            try
            {
                await udpFlood.EstablishAsync().ConfigureAwait(false);
                tcpFlood.Start();
                udpFlood.Start();
                var measurement = await MeasureAsync(options, relayPool, udpWindowPool, udpSetupPool, udpFlood.MarkWarm).ConfigureAwait(false);
                udpFlood.Stop();
                AssertOk(measurement, udpFlood, tcpServer.BytesEchoed);
                context.WriteResult(
                    "gc.soak",
                    new { durationSeconds = options.DurationSeconds, flows = options.Flows, tcpRelays = tcpFlood.RelayCount, pps = options.Pps },
                    BuildMetrics(measurement, drain, udpSink, tcpFlood, udpFlood, tcpServer.BytesEchoed));
            }
            finally
            {
                udpFlood.Stop();
            }
        }
        finally
        {
            try
            {
                await tcpFlood.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await coordinator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<GcSoakMeasurement> MeasureAsync(
        SoakOptions options,
        NativeBufferPool relayPool,
        NativeBufferPool udpWindowPool,
        NativeBufferPool udpSetupPool,
        Action markWarm)
    {
        await Task.Delay(WarmupDuration).ConfigureAwait(false);
        CollectAll();
        markWarm();
        var baselineGc = ReadGc();
        var managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
        var baselinePools = Snapshot(relayPool, udpWindowPool, udpSetupPool);
        using var sampler = new WorkingSetSampler(options.DurationSeconds);
        sampler.Start();
        await Task.Delay(TimeSpan.FromSeconds(options.DurationSeconds)).ConfigureAwait(false);
        var windowGc = ReadGc();
        var windowPools = Snapshot(relayPool, udpWindowPool, udpSetupPool);
        sampler.Stop();
        await Task.Delay(SettleDuration).ConfigureAwait(false);
        return new GcSoakMeasurement(baselineGc, windowGc, baselinePools, windowPools, sampler.Timestamps, sampler.WorkingSet, sampler.SampleCount, managedHeapBytes);
    }

    private static object BuildMetrics(GcSoakMeasurement measurement, UdpDrainReceiver drain, CountingUdpResponseSink sink, TcpFlood tcpFlood, UdpFlood udpFlood, long tcpBytesEchoed)
    {
        var samples = measurement.SampleCount;
        var slope = WorkingSetSlopeBytesPerSecond(measurement.Timestamps.AsSpan(0, samples), measurement.WorkingSet.AsSpan(0, samples));
        return new
        {
            gen0Collections = measurement.WindowGc.Gen0Collections - measurement.BaselineGc.Gen0Collections,
            gen1Collections = measurement.WindowGc.Gen1Collections - measurement.BaselineGc.Gen1Collections,
            gen2Collections = measurement.WindowGc.Gen2Collections - measurement.BaselineGc.Gen2Collections,
            allocatedBytes = measurement.WindowGc.AllocatedBytes - measurement.BaselineGc.AllocatedBytes,
            workingSetBaselineBytes = samples > 0 ? measurement.WorkingSet[0] : 0,
            workingSetFinalBytes = samples > 0 ? measurement.WorkingSet[samples - 1] : 0,
            workingSetSlopeBytesPerSecond = slope,
            workingSetSamples = samples,
            managedHeapBytes = measurement.ManagedHeapBytes,
            tcpRelays = tcpFlood.RelayCount,
            tcpBytesEchoed = tcpBytesEchoed,
            udpSends = udpFlood.SendCount,
            udpDrainReceivedBytes = drain.Received,
            udpResponsesInjected = sink.ResponsesInjected,
            udpSenderThreadAllocatedBytes = udpFlood.ThreadAllocatedBytes,
            poolOutstandingDelta = TotalOutstanding(measurement.WindowPools) - TotalOutstanding(measurement.BaselinePools),
            poolOverflowDelta = TotalOverflow(measurement.WindowPools) - TotalOverflow(measurement.BaselinePools),
        };
    }

    private static void AssertOk(GcSoakMeasurement measurement, UdpFlood udpFlood, long tcpBytesEchoed)
    {
        // Without this the zero-allocation gate could pass on a scenario that never pushed a
        // datagram through the warm path (dead sender threads, all sends rejected).
        if (udpFlood.SendCount <= 0)
        {
            throw new InvalidOperationException("gc-soak exercised no UDP forward sends; the zero-allocation assertion would be vacuous.");
        }

        if (tcpBytesEchoed <= 0)
        {
            throw new InvalidOperationException("gc-soak TCP relay leg echoed no payload; the mixed TCP+UDP load claim would be vacuous.");
        }

        if (!udpFlood.WarmBaselineComplete)
        {
            throw new InvalidOperationException("gc-soak sender threads never captured a warm allocation baseline; the zero-allocation assertion would be vacuous.");
        }

        // The application-level guarantee: the UDP forward path — the product's main path — ran
        // hundreds of thousands of datagrams through established sessions without allocating a
        // meaningful number of managed bytes on the calling threads. This is what the M5 soak can
        // assert exactly; see the type-level note for why process-wide collection counts are
        // reported instead.
        var allowedSenderBytes = SenderAllocationAllowance(udpFlood.SendCount);
        if (udpFlood.ThreadAllocatedBytes > allowedSenderBytes)
        {
            throw new InvalidOperationException(
                $"gc-soak application allocation exceeds the leak ceiling: the UDP forward lanes allocated {udpFlood.ThreadAllocatedBytes} managed bytes (allowed {allowedSenderBytes}) over {udpFlood.SendCount} sends.");
        }

        if (measurement.WindowPools != measurement.BaselinePools)
        {
            throw new InvalidOperationException(
                $"gc-soak native-pool state drifted: baseline {measurement.BaselinePools}, window-end {measurement.WindowPools}.");
        }

        var samples = measurement.SampleCount;
        if (samples < 1)
        {
            throw new InvalidOperationException("gc-soak working-set sampler produced no samples; the flat-memory assertion would be vacuous.");
        }

        var slope = WorkingSetSlopeBytesPerSecond(measurement.Timestamps.AsSpan(0, samples), measurement.WorkingSet.AsSpan(0, samples));
        if (slope > WorkingSetSlopeLimitBytesPerSecond)
        {
            throw new InvalidOperationException(
                $"gc-soak working set grew at {slope:F0} B/s (limit {WorkingSetSlopeLimitBytesPerSecond:F0} B/s) over {samples} samples.");
        }

        var growth = measurement.WorkingSet[samples - 1] - measurement.WorkingSet[0];
        if (growth > WorkingSetGrowthLimitBytes)
        {
            throw new InvalidOperationException(
                $"gc-soak working set grew {growth} bytes across the window (limit {WorkingSetGrowthLimitBytes}).");
        }
    }

    /// <summary>
    /// The managed-byte allowance the UDP forward lanes may show over <paramref name="sendCount"/>
    /// sends before the leak gate trips: the larger of the bounded BCL noise ceiling and a per-send
    /// rate far below any real per-datagram allocation.
    /// </summary>
    internal static long SenderAllocationAllowance(long sendCount)
        => Math.Max(SenderAllocationNoiseCeilingBytes, sendCount / SenderAllocationBytesPerSendDivisor);

    /// <summary>
    /// Least-squares slope of the working-set samples in bytes per second. Timestamps are
    /// <see cref="Stopwatch"/> ticks; the per-tick slope is scaled by the stopwatch frequency.
    /// Returns zero for fewer than two samples or a degenerate (zero-variance) time axis.
    /// </summary>
    internal static double WorkingSetSlopeBytesPerSecond(ReadOnlySpan<long> timestamps, ReadOnlySpan<long> workingSet)
    {
        if (timestamps.Length < 2 || timestamps.Length != workingSet.Length) return 0.0;
        var origin = timestamps[0];
        double sumX = 0;
        double sumY = 0;
        double sumXy = 0;
        double sumXx = 0;
        for (var index = 0; index < timestamps.Length; index++)
        {
            var x = (double)(timestamps[index] - origin);
            var y = (double)workingSet[index];
            sumX += x;
            sumY += y;
            sumXy += x * y;
            sumXx += x * x;
        }

        var count = timestamps.Length;
        var denominator = (count * sumXx) - (sumX * sumX);
        if (denominator <= 0.0) return 0.0;
        return ((count * sumXy) - (sumX * sumY)) / denominator * Stopwatch.Frequency;
    }

#pragma warning disable S1215 // Baseline hygiene: flush warmup garbage before recording the zero-GC mark.
    private static void CollectAll()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
#pragma warning restore S1215

    private static RuntimeGcSnapshot ReadGc() =>
        new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalAllocatedBytes(precise: true));

    private static PoolSnapshot Snapshot(NativeBufferPool relay, NativeBufferPool window, NativeBufferPool setup)
    {
        var relayStats = relay.Stats;
        var windowStats = window.Stats;
        var setupStats = setup.Stats;
        return new PoolSnapshot(
            relayStats.Outstanding, relayStats.OverflowAllocations,
            windowStats.Outstanding, windowStats.OverflowAllocations,
            setupStats.Outstanding, setupStats.OverflowAllocations);
    }

    private static long TotalOutstanding(PoolSnapshot snapshot) =>
        snapshot.RelayOutstanding + snapshot.WindowOutstanding + snapshot.SetupOutstanding;

    private static long TotalOverflow(PoolSnapshot snapshot) =>
        snapshot.RelayOverflow + snapshot.WindowOverflow + snapshot.SetupOverflow;

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PoolSnapshot(
        long RelayOutstanding,
        long RelayOverflow,
        long WindowOutstanding,
        long WindowOverflow,
        long SetupOutstanding,
        long SetupOverflow);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct GcSoakMeasurement(
        RuntimeGcSnapshot BaselineGc,
        RuntimeGcSnapshot WindowGc,
        PoolSnapshot BaselinePools,
        PoolSnapshot WindowPools,
        long[] Timestamps,
        long[] WorkingSet,
        int SampleCount,
        long ManagedHeapBytes);

    private sealed class CountingUdpResponseSink : IUdpResponseSink
    {
        private long _injected;

        public long ResponsesInjected => Interlocked.Read(ref _injected);

        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _injected);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Drains the datagrams the loopback SOCKS5 UDP server forwards, without ever replying: the
    /// measured window stays on the forward relay path, so no session receive loop is woken and no
    /// per-datagram async allocation enters the measurement. Blocking receives on dedicated
    /// threads are deliberately allocation-free while idle.
    /// </summary>
    private sealed class UdpDrainReceiver : IAsyncDisposable
    {
        private const int ReceiveLoopCount = 4;

        private readonly Socket _socket;
        private readonly Thread[] _threads = new Thread[ReceiveLoopCount];
        private volatile bool _stop;
        private long _received;

        public UdpDrainReceiver()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveBufferSize = 16 << 20 };
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Endpoint = (IPEndPoint)_socket.LocalEndPoint!;
            for (var index = 0; index < _threads.Length; index++)
            {
                var thread = new Thread(DrainLoop) { IsBackground = true, Name = $"gc-soak-drain-{index}" };
                _threads[index] = thread;
                thread.Start();
            }
        }

        public IPEndPoint Endpoint { get; }

        /// <summary>Total payload bytes drained (not a datagram count).</summary>
        public long Received => Interlocked.Read(ref _received);

        private void DrainLoop()
        {
            var buffer = new byte[65_536];
            EndPoint anySource = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop)
            {
                try
                {
                    Interlocked.Add(ref _received, _socket.ReceiveFrom(buffer, SocketFlags.None, ref anySource));
                }
                catch (SocketException)
                {
                    // A transient drain-socket fault must not end the harness loop.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _stop = true;
            _socket.Dispose();
            foreach (var thread in _threads) thread.Join(StopTimeout);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UdpFlood
    {
        private const int TickMilliseconds = 10;
        private const int MaximumSenderThreads = 4;

        private readonly UdpProxyCoordinator _coordinator;
        private readonly Socks5Server _server;
        private readonly LoopbackSocks5UdpServer _serverMetrics;
        private readonly int _pps;
        private readonly FlowKey[] _flows;
        private readonly long[] _sequences;
        private readonly byte[] _payload;
        private readonly Thread[] _threads;
        private readonly long[] _threadAllocated;
        private readonly long[] _warmBaseline;
        private readonly bool[] _warmCaptured;
        private volatile bool _warm;
        private int _warmCapturedCount;
        private volatile bool _stop;
        private long _sends;
        private Exception? _error;

        /// <summary>
        /// Managed bytes allocated on the sender threads between the warm marker (set once the
        /// scenario's warmup has elapsed) and thread exit. JIT and thread-startup allocations made
        /// during warmup are excluded, so a non-zero value here is a steady-state regression. The
        /// per-thread deltas are published by each <c>SendLoop</c> on exit, so this is only
        /// meaningful after <see cref="Stop"/> has joined the threads — reading it while they run
        /// observes the zero-initialized arrays.
        /// </summary>
        public long ThreadAllocatedBytes
        {
            get
            {
                long total = 0;
                foreach (var value in _threadAllocated) total += value;
                return total;
            }
        }

        /// <summary>Datagrams submitted on the measured warm path (excludes the setup-window sends).</summary>
        public long SendCount => Interlocked.Read(ref _sends);

        /// <summary>
        /// Marks the end of warmup: every sender thread snapshots its allocation counter on its next
        /// iteration and later reports only the delta from there. Call before <see cref="Stop"/>.
        /// </summary>
        public void MarkWarm() => _warm = true;

        /// <summary>
        /// True when every sender thread observed the warm marker and captured a baseline. If false,
        /// <see cref="ThreadAllocatedBytes"/> would read zero by omission and silently pass the gate.
        /// </summary>
        public bool WarmBaselineComplete => Volatile.Read(ref _warmCapturedCount) == _threads.Length;

        public UdpFlood(UdpProxyCoordinator coordinator, Socks5Server server, LoopbackSocks5UdpServer serverMetrics, SoakOptions options)
        {
            _coordinator = coordinator;
            _server = server;
            _serverMetrics = serverMetrics;
            _pps = options.Pps;
            _flows = new FlowKey[options.Flows];
            for (var index = 0; index < _flows.Length; index++) _flows[index] = BenchmarkShared.CreateFlowKey(index);
            _sequences = new long[_flows.Length];
            _payload = new byte[options.PayloadBytes];
            _threads = new Thread[Math.Min(_flows.Length, MaximumSenderThreads)];
            _threadAllocated = new long[_threads.Length];
            _warmBaseline = new long[_threads.Length];
            _warmCaptured = new bool[_threads.Length];
        }

        public async Task EstablishAsync()
        {
            for (var index = 0; index < _flows.Length; index++)
            {
                _sequences[index]++;
                DatagramHeader.Write(_payload, _sequences[index], index);
                _ = await _coordinator.TrySendAsync(_flows[index], _server, _payload, CancellationToken.None).ConfigureAwait(false);
            }

            var stopwatch = Stopwatch.StartNew();
            while (_coordinator.SessionCount < _flows.Length && stopwatch.Elapsed < SetupTimeout)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            if (_coordinator.SessionCount < _flows.Length)
            {
                throw new InvalidOperationException($"Only {_coordinator.SessionCount} of {_flows.Length} UDP flows established within the setup timeout.");
            }

            // SessionCount only proves the slots exist; each session still has to finish its SOCKS5
            // handshake and flush its buffered first datagram. The sender threads must not start
            // before then: datagrams sent while a slot is not ready take the setup-window path,
            // whose queue-node allocations would be charged to the measured sender threads and
            // masquerade as a warm-path regression.
            while (_serverMetrics.RelayForwarded < _flows.Length && stopwatch.Elapsed < SetupTimeout)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            if (_serverMetrics.RelayForwarded < _flows.Length)
            {
                throw new InvalidOperationException($"Only {_serverMetrics.RelayForwarded} of {_flows.Length} UDP flows relayed their first datagram within the setup timeout.");
            }
        }

        public void Start()
        {
            for (var index = 0; index < _threads.Length; index++)
            {
                var lane = index;
                var thread = new Thread(() => SendLoop(lane)) { IsBackground = true, Name = $"gc-soak-udp-{lane}" };
                _threads[index] = thread;
                thread.Start();
            }
        }

        private void SendLoop(int lane)
        {
            try
            {
                SendLoopCore(lane);
            }
            finally
            {
                _threadAllocated[lane] = _warmCaptured[lane]
                    ? GC.GetAllocatedBytesForCurrentThread() - _warmBaseline[lane]
                    : 0;
            }
        }

        private void SendLoopCore(int lane)
        {
            var perTick = _pps / (double)_threads.Length * TickMilliseconds / 1000.0;
            var tickTicks = Stopwatch.Frequency * TickMilliseconds / 1000;
            var accumulator = 0.0;
            var nextDeadline = Stopwatch.GetTimestamp();
            var flow = lane;
            while (!_stop)
            {
                if (_warm && !_warmCaptured[lane])
                {
                    _warmBaseline[lane] = GC.GetAllocatedBytesForCurrentThread();
                    _warmCaptured[lane] = true;
                    Interlocked.Increment(ref _warmCapturedCount);
                }

                accumulator += perTick;
                var count = (int)accumulator;
                accumulator -= count;
                try
                {
                    for (var index = 0; index < count && !_stop; index++)
                    {
                        if (flow >= _flows.Length) flow = lane;
                        Send(flow);
                        flow += _threads.Length;
                    }
                }
                catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException)
                {
                    _error ??= exception;
                    _stop = true;
                    return;
                }

                nextDeadline += tickTicks;
                var remaining = nextDeadline - Stopwatch.GetTimestamp();
                if (remaining > 0)
                {
                    var milliseconds = (int)(remaining * 1000 / Stopwatch.Frequency);
                    if (milliseconds > 0) Thread.Sleep(milliseconds);
                    else Thread.Yield();
                }
                else
                {
                    nextDeadline = Stopwatch.GetTimestamp();
                }
            }
        }

        private void Send(int flowIndex)
        {
            _sequences[flowIndex]++;
            DatagramHeader.Write(_payload, _sequences[flowIndex], flowIndex);
            var pending = _coordinator.TrySendSpanAsync(_flows[flowIndex], _server, _payload.AsSpan(), default, CancellationToken.None);
            Interlocked.Increment(ref _sends);
            // Dedicated load thread: deliberate synchronous consumption; blocking on the rare async
            // send tail cannot deadlock and the completed shape allocates nothing.
#pragma warning disable S5034
#pragma warning disable VSTHRD002
            _ = pending.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
#pragma warning restore S5034
        }

        public void Stop()
        {
            _stop = true;
            foreach (var thread in _threads)
            {
                thread?.Join(StopTimeout);
            }

            if (_error is not null) throw new InvalidOperationException("The gc-soak UDP flood failed.", _error);
        }
    }

    private sealed class TcpFlood : IAsyncDisposable
    {
        private const int ChunkBytes = 64 * 1024;
        private const int ClientReceiveBufferBytes = 4 * 1024 * 1024;
        private static readonly Endpoint Destination = Endpoint.From(IPAddress.Parse("192.0.2.80"), 443);

        private readonly TcpProxyRelayFactory _factory;
        private readonly Socks5Server _server;
        private readonly int _relayCount;
        private readonly RelaySlot?[] _slots;
        private volatile bool _stop;

        public TcpFlood(TcpProxyRelayFactory factory, Socks5Server server, int relayCount)
        {
            _factory = factory;
            _server = server;
            _relayCount = relayCount;
            _slots = new RelaySlot?[relayCount];
        }

        public int RelayCount => _relayCount;

        public async Task EstablishAsync()
        {
            for (var index = 0; index < _slots.Length; index++)
            {
                var (client, relayLocal) = await CreateSocketPairAsync().ConfigureAwait(false);
                try
                {
                    var peer = (IPEndPoint)relayLocal.RemoteEndPoint!;
                    var accepted = new TcpAcceptedConnection(relayLocal, Endpoint.From(peer.Address, checked((ushort)peer.Port)));
                    var relay = await _factory.EstablishAsync(Destination, accepted, _server, CancellationToken.None).ConfigureAwait(false);
                    _slots[index] = new RelaySlot(client, relay);
                }
                catch
                {
                    client.Dispose();
                    relayLocal.Dispose();
                    throw;
                }
            }
        }

        public void Start()
        {
            for (var index = 0; index < _slots.Length; index++)
            {
                var client = _slots[index]!.Client;
                _slots[index]!.Sender = StartThread($"gc-soak-tcp-send-{index}", () => SenderLoop(client));
                _slots[index]!.Receiver = StartThread($"gc-soak-tcp-recv-{index}", () => ReceiverLoop(client));
            }
        }

        private static Thread StartThread(string name, Action body)
        {
            var thread = new Thread(() => body()) { IsBackground = true, Name = name };
            thread.Start();
            return thread;
        }

        private void SenderLoop(Socket client)
        {
            var chunk = new byte[ChunkBytes];
            while (!_stop)
            {
                try
                {
                    _ = client.Send(chunk, SocketFlags.None);
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private void ReceiverLoop(Socket client)
        {
            var buffer = new byte[ChunkBytes];
            while (!_stop)
            {
                try
                {
                    if (client.Receive(buffer, SocketFlags.None) == 0) return;
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private static async Task<(Socket Client, Socket Relay)> CreateSocketPairAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = ClientReceiveBufferBytes };
            try
            {
                await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint!).ConfigureAwait(false);
                var relay = await listener.AcceptSocketAsync().ConfigureAwait(false);
                relay.ReceiveBufferSize = ClientReceiveBufferBytes;
                return (client, relay);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop = true;
            foreach (var slot in _slots) slot?.Client.Dispose();
            for (var index = 0; index < _slots.Length; index++)
            {
                var slot = _slots[index];
                if (slot is not null) await slot.Relay.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var slot in _slots)
            {
                slot?.Sender?.Join(StopTimeout);
                slot?.Receiver?.Join(StopTimeout);
            }
        }

        private sealed class RelaySlot(Socket client, ITcpRelay relay)
        {
            public Socket Client { get; } = client;

            public ITcpRelay Relay { get; } = relay;

            public Thread? Sender { get; set; }

            public Thread? Receiver { get; set; }
        }
    }

    /// <summary>
    /// Samples the process working set once per second on a dedicated thread. Registering the
    /// sample state up front and reading <see cref="Process.WorkingSet64"/> (allocation-free on
    /// this runtime) keeps the monitor itself out of the measured allocation signal.
    /// </summary>
    private sealed class WorkingSetSampler : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly Thread _thread;
        private readonly long[] _timestamps;
        private readonly long[] _workingSet;
        private volatile bool _stop;
        private int _sampleCount;

        public WorkingSetSampler(int durationSeconds)
        {
            var capacity = durationSeconds + 8;
            _timestamps = new long[capacity];
            _workingSet = new long[capacity];
            _thread = new Thread(SampleLoop) { IsBackground = true, Name = "gc-soak-sampler" };
        }

        public long[] Timestamps => _timestamps;

        public long[] WorkingSet => _workingSet;

        public int SampleCount => Volatile.Read(ref _sampleCount);

        public void Start() => _thread.Start();

        public void Stop()
        {
            _stop = true;
            _thread.Join(StopTimeout);
        }

        private void SampleLoop()
        {
            var stopwatch = Stopwatch.StartNew();
            while (!_stop)
            {
                var index = _sampleCount;
                if (index < _timestamps.Length)
                {
                    _timestamps[index] = stopwatch.ElapsedTicks;
                    _workingSet[index] = _process.WorkingSet64;
                    Volatile.Write(ref _sampleCount, index + 1);
                }

                Thread.Sleep(1000);
            }
        }

        public void Dispose()
        {
            Stop();
            _process.Dispose();
        }
    }
}

#pragma warning restore CA1416
