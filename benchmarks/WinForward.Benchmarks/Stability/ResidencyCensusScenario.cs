#pragma warning disable CA1416 // TcpProxyRelayFactory/TcpAcceptedConnection/TcpProxyRelay carry SupportedOSPlatform(windows) but are platform-neutral managed code; only their production wiring is Windows-specific.
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// Live-residency census (research A1/A4 memory items plus F6's TCP half): at <c>--flows</c> live TCP
/// flows and <c>--udp-flows</c> live UDP sessions, the resident cost of each population measured as a
/// per-flow delta against a zero-flow baseline taken in the same process. Three populations are built in
/// order — the flow table seeded through the production claim call, <c>--flows</c> real loopback SOCKS5
/// relays held open (two 64 KiB pump windows each), and <c>--udp-flows</c> coordinator sessions over fake
/// transports — and a forced full collection immediately precedes every sample, so a stage cannot look
/// cheap merely because it dropped garbage.
/// <para>
/// The populations are cumulative, so each row carries two deltas: against the baseline (which still
/// contains every earlier stage) and against the previous stage (which is the one attributable to this
/// stage's own population). A per-flow figure is only meaningful where its column actually moves with
/// the population, so every row carries a note naming what dominates it — the flow table's cost is its
/// pre-allocated capacity rather than its live states, and the relay pump windows are native memory that
/// lands in private bytes rather than on the managed heap. This is the artifact the A4 FlowTable rebuild
/// (−19 MB steady state) and the relay-window item (−10 MB @100 conns) are judged against.
/// </para>
/// <para>
/// Report-only (design §3): no timing or byte threshold can fail the run. The only aborts are the
/// population proofs — the flow table's own count, the loopback server's CONNECT-reply count, and the
/// transport factory's created count cross-checked against the coordinator's session count — because a
/// census of a population that silently failed to build would be a fabricated number.
/// </para>
/// </summary>
internal static class ResidencyCensusScenario
{
    /// <summary>
    /// The flow-table capacity every census stage runs at: the table's own default (65,536), which is
    /// the production shape A1 item 1a describes ("<c>_states</c> pre-sized to 65,536"). A scaled-down
    /// table would price the harness instead of the process.
    /// </summary>
    private const int ProductionFlowCapacity = 65_536;

    private static readonly byte[] s_udpPopulatePayload = [1];

    /// <summary>Bounds every population proof and the UDP populate loop; a timeout aborts the run instead of reporting a smaller census.</summary>
    private static readonly TimeSpan s_populationTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan s_populateRoundDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>The SOCKS5 CONNECT target; the loopback server echoes whatever it is asked for.</summary>
    private static Endpoint Destination { get; } = Endpoint.From(IPAddress.Parse("192.0.2.80"), 443);

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        using var process = Process.GetCurrentProcess();
        var baseline = Capture(process, "baseline", "process-start", 0, flowTable: null, relays: 0, udpSessions: 0);
        Emit(context, baseline, baseline, baseline);

        var table = BuildFlowTable(options.Flows);
        RequirePopulation("flowTable", table.Count, options.Flows, "--flows live flow states");
        var afterTable = Capture(process, "flowTable", "baseline", options.Flows, table, relays: 0, udpSessions: 0);
        Emit(context, afterTable, baseline, baseline);

        await using var tcpServer = new LoopbackSocks5TcpServer();
        var relays = await EstablishRelaysAsync(options.Flows, tcpServer).ConfigureAwait(false);
        try
        {
            // The server's reply counter is written by its own connection task after the establishing
            // call returns, so the proof waits for it rather than snapshotting a racing read.
            // ReSharper disable once AccessToDisposedClosure // the awaited poll reads the live server's counter and returns before the await using scope disposes tcpServer below.
            await WaitUntilAsync("tcpRelayWindows", () => tcpServer.ConnectReplies, options.Flows, "loopback SOCKS5 CONNECT replies").ConfigureAwait(false);
            RequirePopulation("tcpRelayWindows", tcpServer.ConnectReplies, options.Flows, "loopback SOCKS5 CONNECT replies");
            RequirePopulation("tcpRelayWindows", relays.Count, options.Flows, "established relays");
            var afterRelays = Capture(process, "tcpRelayWindows", "flowTable", options.Flows, table, relays.Count, udpSessions: 0);
            Emit(context, afterRelays, baseline, afterTable);

            // The relays stay live through the UDP stage, so the UDP row's previous-stage delta prices
            // the sessions alone; disposal is in the finally below.
            await using var udp = new UdpCensusStage(options.UdpFlows);
            await udp.PopulateAsync().ConfigureAwait(false);
            RequirePopulation("udpSessions", udp.CreatedTransports, options.UdpFlows, "fake UDP transports created");
            RequirePopulation("udpSessions", udp.LiveSessions, options.UdpFlows, "coordinator live UDP sessions");
            var afterUdp = Capture(process, "udpSessions", "tcpRelayWindows", options.UdpFlows, table, relays.Count, udp.LiveSessions);
            Emit(context, afterUdp, baseline, afterRelays);
            WriteVerdict(context, options, afterTable, afterRelays, afterUdp);
        }
        finally
        {
            await DisposeRelaysAsync(relays).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One sample: a forced full collection first (the contract that makes stages comparable), then the
    /// managed heap, the GC's committed bytes, the process's working set and private bytes, the
    /// per-generation collection counts, and the process's descriptor count. Descriptors are read last
    /// because the <c>/proc/self/fd</c> enumeration allocates: its garbage belongs to the next stage's
    /// collection, not to this sample's managed figure.
    /// </summary>
    private static CensusSample Capture(Process process, string stage, string follows, int population, FlowTable? flowTable, int relays, int udpSessions)
    {
        // The forced full collection is the census contract, not a GC workaround: without it a stage
        // could read cheap because it merely dropped garbage, and its delta would be a GC artifact.
#pragma warning disable S1215 // Collected-before-sample is the measurement; every stage is sampled from the same post-full-GC state.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var managedBytes = GC.GetTotalMemory(forceFullCollection: true);
#pragma warning restore S1215
        var totalCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes;
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        process.Refresh();
        var workingSetBytes = process.WorkingSet64;
        var privateBytes = process.PrivateMemorySize64;
        var descriptors = ProcessResourceSampler.OpenFileDescriptors();
        return new CensusSample(
            stage,
            follows,
            population,
            flowTable?.Count ?? 0,
            flowTable?.Capacity ?? 0,
            relays,
            udpSessions,
            managedBytes,
            totalCommittedBytes,
            workingSetBytes,
            privateBytes,
            descriptors,
            gen0,
            gen1,
            gen2,
            NoteFor(stage));
    }

    /// <summary>
    /// The production flow-table shape seeded with <paramref name="flows"/> live states through the real
    /// claim call, so the census prices the table the process actually constructs. A claim that fails at
    /// this cardinality aborts rather than yielding a smaller population than the run was asked for.
    /// </summary>
    private static FlowTable BuildFlowTable(int flows)
    {
        var table = new FlowTable(capacity: ProductionFlowCapacity);
        for (var index = 0; index < flows; index++)
        {
            if (!table.TryClaimResolved(BenchmarkShared.CreateFlowKey(index), static () => FlowDecision.Fallback(FlowAction.Pass), out _))
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The residency census could not claim flow {index} of {flows}."));
            }
        }

        return table;
    }

    /// <summary>
    /// <paramref name="flows"/> real relays through the loopback SOCKS5 server, established one at a
    /// time and held open — the same establishment path <c>TcpChurnScenario</c> drives, so each relay
    /// owns its two 64 KiB pump windows for its whole life. A failure mid-way disposes what was built
    /// and rethrows; the caller's population proof then never sees a partial population.
    /// </summary>
    private static async Task<List<RelayHandle>> EstablishRelaysAsync(int flows, LoopbackSocks5TcpServer server)
    {
        var socksServer = new Socks5Server("residency", "127.0.0.1", checked((ushort)server.Endpoint.Port), Username: null, Password: null);
        var factory = new TcpProxyRelayFactory(new SelfTrafficRegistry());
        var relays = new List<RelayHandle>(flows);
        try
        {
            for (var index = 0; index < flows; index++)
            {
                var (client, relayLocal) = await CreateSocketPairAsync().ConfigureAwait(false);
                var peer = (IPEndPoint)relayLocal.RemoteEndPoint!;
                var accepted = new TcpAcceptedConnection(relayLocal, Endpoint.From(peer.Address, checked((ushort)peer.Port)));
                var relay = await factory.EstablishAsync(Destination, accepted, socksServer, CancellationToken.None).ConfigureAwait(false);
                relays.Add(new RelayHandle(client, relayLocal, relay));
            }

            return relays;
        }
        catch
        {
            await DisposeRelaysAsync(relays).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The relay's client leg plus its accepted local socket; both must stay open for the relay to stay established.</summary>
    private static async Task DisposeRelaysAsync(List<RelayHandle> relays)
    {
        foreach (var handle in relays)
        {
            await handle.Relay.DisposeAsync().ConfigureAwait(false);
            handle.RelayLocal.Dispose();
            handle.Client.Dispose();
        }

        relays.Clear();
    }

    /// <summary>One loopback TCP pair: the client end and the accepted socket the relay proxies.</summary>
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

    /// <summary>Bounded wait for a counter another thread owns; a timeout is a hard abort, never a smaller census.</summary>
    private static async Task WaitUntilAsync(string stage, Func<long> observed, long expected, string what)
    {
        var clock = Stopwatch.StartNew();
        while (observed() < expected)
        {
            if (clock.Elapsed >= s_populationTimeout) throw PopulationFailure(stage, observed(), expected, what);
            await Task.Delay(s_populateRoundDelay).ConfigureAwait(false);
        }
    }

    /// <summary>The population proof: a census of a population that silently failed to build would be a fabricated number.</summary>
    private static void RequirePopulation(string stage, long actual, long expected, string what)
    {
        if (actual != expected) throw PopulationFailure(stage, actual, expected, what);
    }

    private static InvalidOperationException PopulationFailure(string stage, long actual, long expected, string what) =>
        new(string.Create(CultureInfo.InvariantCulture, $"The {stage} census expected {expected} {what} but found {actual}; refusing to report residency for a population that did not build."));

    private static void Emit(StabilityContext context, CensusSample sample, CensusSample baseline, CensusSample previous)
    {
        context.WriteResult(
            "residency.census",
            new { stage = sample.Stage, follows = sample.Follows, population = sample.Population },
            new
            {
                live = new { flowStates = sample.FlowTableCount, tcpRelays = sample.Relays, udpSessions = sample.UdpSessions },
                flowTableCapacity = sample.FlowTableCapacity,
                managedBytes = sample.ManagedBytes,
                totalCommittedBytes = sample.TotalCommittedBytes,
                workingSetBytes = sample.WorkingSetBytes,
                privateBytes = sample.PrivateBytes,
                descriptors = sample.Descriptors,
                gen0Collections = sample.Gen0Collections,
                gen1Collections = sample.Gen1Collections,
                gen2Collections = sample.Gen2Collections,
                vsBaseline = Delta(sample, baseline, sample.Population),
                vsPreviousStage = new { stage = previous.Stage, delta = Delta(sample, previous, sample.Population) },
                note = sample.Note,
            });
    }

    /// <summary>
    /// The derived reading: absolute deltas in every column plus the per-flow form the run was asked
    /// for, <c>(sample − reference) / population</c>, over the stage's own population. Both the managed
    /// and the native columns are divided, because which one carries a population is the point of the
    /// census (the flow table is managed, the relay windows are not).
    /// </summary>
    private static object Delta(CensusSample sample, CensusSample reference, int population)
    {
        var managedBytes = sample.ManagedBytes - reference.ManagedBytes;
        var totalCommittedBytes = sample.TotalCommittedBytes - reference.TotalCommittedBytes;
        var workingSetBytes = sample.WorkingSetBytes - reference.WorkingSetBytes;
        var privateBytes = sample.PrivateBytes - reference.PrivateBytes;
        var descriptors = sample.Descriptors - reference.Descriptors;
        return new
        {
            managedBytes,
            totalCommittedBytes,
            workingSetBytes,
            privateBytes,
            descriptors,
            gen0Collections = sample.Gen0Collections - reference.Gen0Collections,
            gen1Collections = sample.Gen1Collections - reference.Gen1Collections,
            gen2Collections = sample.Gen2Collections - reference.Gen2Collections,
            perFlow = new
            {
                managedBytes = PerFlow(managedBytes, population),
                totalCommittedBytes = PerFlow(totalCommittedBytes, population),
                workingSetBytes = PerFlow(workingSetBytes, population),
                privateBytes = PerFlow(privateBytes, population),
                descriptors = PerFlow(descriptors, population),
            },
        };
    }

    private static void WriteVerdict(StabilityContext context, SoakOptions options, CensusSample table, CensusSample relays, CensusSample udp)
    {
        context.WriteResult(
            "residency.census.verdict",
            new { flows = options.Flows, udpFlows = options.UdpFlows },
            new
            {
                gated = false,
                populations = new { flowStates = table.FlowTableCount, tcpRelays = relays.Relays, udpSessions = udp.UdpSessions },
                flowTableCapacity = table.FlowTableCapacity,
                stages = new[] { "baseline", table.Stage, relays.Stage, udp.Stage },
                note = "Report-only census (design §3): every byte threshold here is a recorded baseline, not a pass line, because residency depends on GC state and the platform allocator (and the A4/relay items will change it by design). The run aborts only when a population proof fails. Compare rows with vsPreviousStage for per-flow attribution; run 3× and quote the spread.",
            });
    }

    /// <summary>Per-flow division; zero for the zero-flow baseline, where the delta is the whole process.</summary>
    private static double PerFlow(long delta, int population) => population == 0 ? 0 : Round((double)delta / population, 1);

    /// <summary>Artifact rounding: the analyzer requires an explicit midpoint mode, and ToEven is the runtime's own default.</summary>
    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.ToEven);

    /// <summary>
    /// What dominates each stage's numbers. The note travels with the row because a per-flow figure
    /// quoted without it (e.g. the flow table's 65,536-state pre-allocation divided by 100 live flows)
    /// would read as per-flow cost when it is not.
    /// </summary>
    private static string NoteFor(string stage) => stage switch
    {
        "baseline" => "Zero-flow baseline: no flow table, relay, or UDP session exists yet. Every later per-flow figure is a delta against this sample, and the baseline is taken before any stage pre-sizes a pool.",
        "flowTable" => "Flow table at the production 65,536-state capacity. The delta is the table's own pre-allocated indexes and state pool (A1 item 1a) rather than the live states, so the per-flow figure is capacity-dominated and a larger --flows hardly moves it: run the census twice at different populations to see what the live states themselves add.",
        "tcpRelayWindows" => "One real loopback SOCKS5 relay per flow, held open and idle: two 64 KiB native pump windows per relay (A1 item 2 / A4 item 9). The windows are native, so they are absent from managedBytes and TotalCommittedBytes; on this host PrivateMemorySize64 is /proc/self VmData, which also carries the allocator's per-thread arena reservations and moves by tens of MB when threads appear, so the working-set column is the one that tracks the windows and privateBytes must not be read as per-relay cost.",
        "udpSessions" => "Coordinator sessions over fake transports: the session, slot and association objects plus the per-session setup-queue and receive-window buffers. The fake transport creates no socket, so the descriptor column here is the harness's own and not the real dial path's 1.070 descriptors per live session measured by the 09-28 series, and the same VmData arena caveat as the relay stage applies to privateBytes.",
        _ => string.Empty,
    };

    /// <summary>One sampled instant; the fields are exactly the columns the census exists to report.</summary>
    private sealed record CensusSample(
        string Stage,
        string Follows,
        int Population,
        int FlowTableCount,
        int FlowTableCapacity,
        int Relays,
        int UdpSessions,
        long ManagedBytes,
        long TotalCommittedBytes,
        long WorkingSetBytes,
        long PrivateBytes,
        int Descriptors,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        string Note);

    private sealed record RelayHandle(Socket Client, Socket RelayLocal, ITcpRelay Relay);

    /// <summary>
    /// The UDP half of the census: <c>--udp-flows</c> coordinator sessions over fake transports, built
    /// with the footprint scenario's populate pattern — offer every key, let the setup-failure cooldown
    /// lapse, and wait for the factory's own created count rather than assuming the first offer worked.
    /// The fake transport is deliberate: the measured part is the session/slot/association residency, and a
    /// real dial would put loopback socket work and timeouts into the census loop.
    /// </summary>
    private sealed class UdpCensusStage : IAsyncDisposable
    {
        private readonly FlowKey[] _keys;
        private readonly CountingTransportFactory _factory = new(new BenchmarkUdpTransportFactory());
        private readonly NativeBufferPool _setupQueuePool;
        private readonly NativeBufferPool _receiveWindowPool;
        private readonly SetupExecutor _setupExecutor = new();
        private readonly UdpProxyCoordinator _coordinator;

        public UdpCensusStage(int sessions)
        {
            Sessions = sessions;
            const int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;
            _setupQueuePool = new NativeBufferPool(maximumFrameSize);
            _receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));
            _coordinator = new UdpProxyCoordinator(_factory, NoopUdpResponseSink.Instance, _setupQueuePool, _receiveWindowPool, _setupExecutor, new UdpProxyOptions { Capacity = sessions });
            _keys = new FlowKey[sessions];
            for (var index = 0; index < _keys.Length; index++) _keys[index] = BenchmarkShared.CreateFlowKey(index);
        }

        private int Sessions { get; }

        /// <summary>The fake transport factory's own count: the proof that every session reached a transport, not just a slot.</summary>
        public int CreatedTransports => _factory.Created;

        /// <summary>The coordinator's gate-consistent live session count; the second, independent half of the population proof.</summary>
        public int LiveSessions => _coordinator.SessionCount;

        public async Task PopulateAsync()
        {
            var server = new Socks5Server("residency", "127.0.0.1", 1080, Username: null, Password: null);
            var clock = Stopwatch.StartNew();
            while (_factory.Created < Sessions)
            {
                foreach (var key in _keys)
                {
                    // A false return is the setup-failure cooldown; the next round re-offers the flow.
                    _ = await _coordinator.TrySendSpanAsync(key, ProxyTarget.FromServer(server), s_udpPopulatePayload, default, CancellationToken.None).ConfigureAwait(false);
                }

                if (_factory.Created >= Sessions) return;
                if (clock.Elapsed >= s_populationTimeout) throw PopulationFailure("udpSessions", _factory.Created, Sessions, "fake UDP transports created");
                await Task.Delay(s_populateRoundDelay).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _coordinator.DisposeAsync().ConfigureAwait(false);
            _setupExecutor.Dispose();
            _receiveWindowPool.Dispose();
            _setupQueuePool.Dispose();
        }
    }

    /// <summary>Counts the transports the coordinator actually created; the census asserts on this count before it samples.</summary>
    private sealed class CountingTransportFactory(IUdpProxyTransportFactory inner) : IUdpProxyTransportFactory
    {
        private int _created;

        public int Created => Volatile.Read(ref _created);

        public async ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
        {
            var transport = await inner.CreateAsync(target, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _created);
            return transport;
        }
    }
}

#pragma warning restore CA1416
