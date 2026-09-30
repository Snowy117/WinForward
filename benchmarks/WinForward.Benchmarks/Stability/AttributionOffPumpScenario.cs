#pragma warning disable CA1416 // The capture pump and the owner tables are Windows-attributed; this scenario drives the managed pipeline through fakes, so it runs on any OS (same rationale as PumpIdleWakeScenario).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Windows;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The acceptance instrument for F8 (process attribution off the capture pump). It drives the
/// <b>real</b> <see cref="FlowDispatcher"/> from one dedicated "pump" thread over a scripted
/// sequence of new host flows, with an attributor that costs <c>--attribution-cost-ms</c> and
/// records the thread every call ran on.
/// <list type="bullet">
/// <item><c>attribution</c> — the pipeline arm: exact attribution thread counts (on the pump thread
/// and on a setup worker), admission and delivery counts, a <c>pumpBlockedMs</c> series and a
/// first-packet-delay series. The pump thread performs the same blocking wait the real pump's
/// <c>InvokeHandler</c> does, so the series measures the stall the pump actually pays;</item>
/// <item><c>attribution.ownerBurst</c> — the epoch-coalescing arm: the real
/// <see cref="WindowsProcessAttributor"/> over a scripted owner-table reader, with N concurrent
/// lookups, reporting how many system-wide scans the burst cost.</item>
/// </list>
/// <b>The row measures the pipeline, never <c>iphlpapi</c>.</b> The real enumeration cannot run on
/// this host, so its cost is modelled by the attributor's delay and the table is scripted; what the
/// row establishes is where the work runs and what the pump pays for it.
/// </summary>
internal static class AttributionOffPumpScenario
{
    private const int PacketsPerFlow = 4;
    private const nint AdapterHandle = 0x1F8;
    private static readonly TimeSpan s_settleTimeout = TimeSpan.FromSeconds(60);

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        var flows = options.Flows;
        var costMs = options.AttributionCostMs;
        using var pool = new NativeBufferPool(1514);
        using var setup = new SetupExecutor(workerCount: 4, ringCapacity: 256);
        var attributor = new CostedAttributor(TimeSpan.FromMilliseconds(costMs));
        var executor = new StampingExecutor();
        var dispatcher = new FlowDispatcher(
            BuildConfiguration(),
            new NeverOwnedGuard(),
            executor,
            attributor,
            flowCapacity: 65_536,
            attributionPool: pool,
            setupExecutor: setup);
        var pipeline = dispatcher.Attribution;
        // The pool's first (allocating) rent happens outside every measured window.
        using var warm = pool.Rent();

        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var gen0Before = GC.CollectionCount(0);
        var run = RunPump(dispatcher, pipeline, executor, flows);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore;
        var gen0 = GC.CollectionCount(0) - gen0Before;
        var admitted = pipeline is null ? flows : (int)pipeline.Admissions;
        var retained = pipeline?.RetainedPacketCount ?? 0;
        var delivered = executor.Delivered;
        if (pipeline is not null) await pipeline.DisposeAsync().ConfigureAwait(false);

        var pumpThreadCalls = attributor.CallsOnThread(run.PumpThreadId);
        context.WriteResult(
            "attribution",
            new { flows, packetsPerFlow = PacketsPerFlow, costMs, pumpThreadId = run.PumpThreadId, pipelineAttached = pipeline is not null },
            new
            {
                newFlows = flows,
                packetsPerFlow = PacketsPerFlow,
                admittedEntries = admitted,
                retainedPackets = retained,
                attributionsTotal = attributor.Calls,
                attributionsOnPumpThread = pumpThreadCalls,
                attributionsOnSetupWorker = attributor.Calls - pumpThreadCalls,
                reAdmissions = pipeline?.ReAdmissionCount ?? 0,
                deliveredPackets = delivered,
                refusedPackets = pipeline?.RefusedPacketCount ?? 0,
                pumpBlockedMs = LatencyDistribution.FromMilliseconds([.. run.BlockedTicks]),
                firstPacketDelayMs = LatencyDistribution.FromMilliseconds([.. run.FirstPacketDelays]),
                ownerTableScansPerBurst = attributor.Calls,
                gen0Collections = gen0,
                allocatedBytesPerNewFlow = flows == 0 ? 0 : allocated / flows,
                gated = false,
                note = "Report-only series (design §7). The exact acceptance counts are attributionsOnPumpThread (pre-change: one per new flow; post-change: 0) and attributionsOnSetupWorker (post-change: one per admitted entry). The row measures the PIPELINE over a fake attributor, never iphlpapi: the system-wide enumeration cannot run on this host, so its cost is modelled by --attribution-cost-ms. ownerTableScansPerBurst here is the modelled attribution-attempt count; the real epoch-coalescing series is the attribution.ownerBurst row plus the ProcessOwnerTableCacheTests facts. allocatedBytesPerNewFlow is process-wide and includes the harness, so it compares only between runs of this row.",
            });

        await WriteOwnerBurstRowAsync(context, costMs).ConfigureAwait(false);
    }

    /// <summary>One dedicated "pump" thread driving the whole scripted sequence and its drains.</summary>
    private static PumpRun RunPump(FlowDispatcher dispatcher, FlowAttributionPipeline? pipeline, StampingExecutor executor, int flows)
    {
        var pumpThreadId = 0;
        var blockedTicks = new List<double>(flows * PacketsPerFlow);
        var pump = new Thread(() =>
        {
            pumpThreadId = Environment.CurrentManagedThreadId;
            var watch = new Stopwatch();
            for (var flow = 0; flow < flows; flow++)
            {
                var port = (ushort)(40_000 + flow);
                for (byte packet = 0; packet < PacketsPerFlow; packet++)
                {
                    var captured = BuildPacket(port, (byte)(packet + 1));
                    var dispatchedAt = Stopwatch.GetTimestamp();
                    watch.Restart();
                    var pending = dispatcher.DispatchAsync(captured, CancellationToken.None);
                    // The pump's own wait: a synchronously-completing handler is a plain result
                    // read, anything else parks this dedicated thread (NdisCapturePump.InvokeHandler).
#pragma warning disable VSTHRD002 // The scenario's dedicated pump thread mirrors NdisCapturePump.InvokeHandler, which blocks this thread on a pending handler by contract.
                    if (!pending.IsCompletedSuccessfully) pending.AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                    watch.Stop();
                    blockedTicks.Add(StabilityShared.TicksToMilliseconds(watch.ElapsedTicks));
                    executor.NoteDispatch(port, captured.Lease.Disposition, dispatchedAt);
                    pipeline?.DeliverDecided(AdapterHandle);
                }
            }

            var deadline = Stopwatch.GetTimestamp() + (long)(s_settleTimeout.TotalSeconds * Stopwatch.Frequency);
            while (executor.Undelivered > 0 || (pipeline is not null && pipeline.PendingCount > 0))
            {
                pipeline?.DeliverDecided(AdapterHandle);
                if (Stopwatch.GetTimestamp() > deadline)
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The pipeline never settled: pending={pipeline?.PendingCount ?? 0}, undelivered={executor.Undelivered}."));
                }

                Thread.Sleep(1);
            }
        })
        {
            IsBackground = true,
            Name = "wf-scenario-pump",
        };

        pump.Start();
        pump.Join();
        return new PumpRun(pumpThreadId, blockedTicks, [.. executor.FirstPacketDelaysMs()]);
    }

    private sealed record PumpRun(int PumpThreadId, List<double> BlockedTicks, List<double> FirstPacketDelays);

    /// <summary>
    /// The coalescing arm: N concurrent flows whose owner rows are scripted, driven through the real
    /// attributor so the counted reads are the product's own scans.
    /// </summary>
    private static async Task WriteOwnerBurstRowAsync(StabilityContext context, int costMs)
    {
        const int burst = 16;
        const ushort basePort = 41_000;
        var reader = new ScriptedOwnerReader();
        var attributor = new WindowsProcessAttributor(cacheCapacity: 64, reader, ProcessOwnerTableCache.DefaultWindowMs);
        var keys = new FlowKey[burst];
        var rows = new TcpOwnerRow[burst];
        for (var index = 0; index < burst; index++)
        {
            keys[index] = TcpKey((ushort)(basePort + index));
            rows[index] = new TcpOwnerRow(
                Endpoint.From(IPAddress.Parse("192.0.2.10"), (ushort)(basePort + index)),
                Endpoint.From(IPAddress.Parse("192.0.2.53"), 443),
                (uint)(5000 + index));
        }

        reader.Script(OwnerTableKind.Tcp4, [], rows);
        var results = new Task<ProcessIdentity?>[burst];
        for (var index = 0; index < burst; index++)
        {
            var key = keys[index];
            results[index] = Task.Run(async () => await attributor.FindAsync(key, CancellationToken.None).ConfigureAwait(false));
        }

        await Task.WhenAll(results).ConfigureAwait(false);
        var reads = reader.ReadCount;
        context.WriteResult(
            "attribution.ownerBurst",
            new { burst, costMs, windowMs = ProcessOwnerTableCache.DefaultWindowMs },
            new
            {
                burstFlows = burst,
                ownerTableScansPerBurst = reads,
                // Banker's rounding, the sibling scenarios' Round convention; the latency
                // percentiles are nearest-rank selections and round nothing.
                scansPerFlow = Math.Round((double)reads / burst, 6, MidpointRounding.ToEven),
                scriptedRows = rows.Length,
                gated = false,
                note = "Report-only series, never a threshold (amended AC-4): a burst's own sockets bind after any snapshot taken before it, so serving them costs at least one scan per epoch. What is exact is that the burst costs one scan rather than one per flow, asserted by ProcessOwnerTableCacheTests.NConcurrentMissesInsideTheWindowReadTheTableExactlyOnce. The scripted PIDs are synthetic, so the identity is not the claim.",
            });
    }

    private static ValidatedConfiguration BuildConfiguration()
    {
        var rules = new[]
        {
            new PolicyRule(
                new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome.exe" }),
                new FlowDecision(FlowAction.Pass, 0, ProxyServerName: null)),
        };
        return new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot(rules, FlowAction.Pass));
    }

    private static FlowKey TcpKey(ushort localPort) => FlowKey.Create(
        Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(IPAddress.Parse("192.0.2.53"), 443),
        TransportProtocol.Tcp,
        FlowOriginKind.Host);

    private static CapturedFlowPacket BuildPacket(ushort localPort, byte marker)
    {
        var frame = new byte[128];
        frame[0] = marker;
        return new CapturedFlowPacket(new PacketLease(frame), BenchmarkShared.CreateContext(TcpKey(localPort)), new PacketCaptureMetadata(0, AdapterHandle));
    }

    /// <summary>A scripted owner table: one table per kind, replaced by the row scripting call.</summary>
    private sealed class ScriptedOwnerReader : IProcessOwnerTableReader
    {
        private OwnerTable _table = OwnerTable.Unavailable;
        private long _reads;

        public long ReadCount => Interlocked.Read(ref _reads);

        public void Script(OwnerTableKind kind, UdpOwnerRow[] udpRows, TcpOwnerRow[] tcpRows) => _table = new OwnerTable(kind, udpRows, tcpRows);

        public OwnerTable Read(OwnerTableKind kind)
        {
            _ = Interlocked.Increment(ref _reads);
            return _table;
        }
    }

    private sealed class NeverOwnedGuard : ISelfTrafficGuard
    {
        public bool IsOwned(FlowContext context) => false;

        public bool IsWildcardOwned(FlowContext context) => false;
    }

    /// <summary>The modelled attribution: its cost stands in for the system-wide scan plus the process open.</summary>
    private sealed class CostedAttributor(TimeSpan cost) : IProcessAttributor
    {
        private readonly ConcurrentQueue<int> _threads = new();

        public int Calls => _threads.Count;

        public async ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken cancellationToken)
        {
            _threads.Enqueue(Environment.CurrentManagedThreadId);
            if (cost > TimeSpan.Zero) await Task.Delay(cost, cancellationToken).ConfigureAwait(false);
            return new ProcessIdentity("chrome.exe", FullPath: null);
        }

        public int CallsOnThread(int threadId) => _threads.Count(id => id == threadId);
    }

    /// <summary>
    /// Counts executor actions and, per flow, stamps the delay from that flow's first admission to
    /// its first delivered packet.
    /// </summary>
    private sealed class StampingExecutor : IPacketActionExecutor
    {
        private readonly ConcurrentDictionary<ushort, long> _dispatchTicks = new();
        private readonly ConcurrentDictionary<ushort, long> _firstDeliveryTicks = new();
        private int _delivered;

        public int Delivered => Volatile.Read(ref _delivered);

        /// <summary>Packets admitted but not yet delivered; the pump drains until this reaches zero.</summary>
        public int Undelivered => _dispatchTicks.Count - _firstDeliveryTicks.Count;

        public void NoteDispatch(ushort localPort, PacketDisposition? disposition, long ticks)
        {
            if (disposition != PacketDisposition.Deferred) return;
            _ = _dispatchTicks.TryAdd(localPort, ticks);
        }

        public IEnumerable<double> FirstPacketDelaysMs()
        {
            foreach (var pair in _dispatchTicks)
            {
                if (!_firstDeliveryTicks.TryGetValue(pair.Key, out var deliveredAt)) continue;
                yield return StabilityShared.TicksToMilliseconds(deliveredAt - pair.Value);
            }
        }

        public ValueTask PassAsync(CapturedFlowPacket packet) => RecordAsync(packet);

        public ValueTask BlockAsync(CapturedFlowPacket packet) => RecordAsync(packet);

        public ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken) => RecordAsync(packet);

        private ValueTask RecordAsync(CapturedFlowPacket packet)
        {
            _ = _firstDeliveryTicks.TryAdd(packet.Context.Key.LocalPort, Stopwatch.GetTimestamp());
            _ = Interlocked.Increment(ref _delivered);
            return ValueTask.CompletedTask;
        }
    }
}
