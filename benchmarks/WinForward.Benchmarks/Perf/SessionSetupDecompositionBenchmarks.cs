using System.Diagnostics;
using System.Globalization;
using System.Net;
using BenchmarkDotNet.Attributes;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Perf;

/// <summary>
/// Staged decomposition of <c>UdpSessionBenchmarks.PopulateSessionsNoopTransportAsync</c> (task
/// 09-21-session-creation-cost, design §2): each variant stops the real coordinator pipeline at
/// one boundary, so the delta between consecutive variants prices one per-session stage each. All
/// variants drive the real <see cref="UdpProxyCoordinator"/> against in-memory fake transports;
/// every variant asserts its own stop condition before its measured window closes. The teardown of
/// every stage except <c>StageS5</c> runs in <c>[IterationCleanup]</c> — BenchmarkDotNet's engine
/// reads the GC counters after the workload and before the iteration cleanup, so stage deltas
/// price setup work only and the teardown cost is the S5 (teardown inside the window, today's
/// probe shape) minus StageS4 (teardown outside) difference.
/// <para>
/// Measured stop boundaries: S1 = admission only (an enqueue-only <see cref="ISetupExecutor"/>
/// keeps the background pipeline from starting); S2 = background setup started (real executor,
/// dial-gated fake factory — the 8-wide setup limiter serializes, so only eight dials are in
/// flight and the rest of the setup tasks park on the limiter, which is the product's real wave
/// shape); S3 = the full construction pipeline with no buffered payload (a one-byte global setup
/// budget rejects every datagram as backpressure, so sessions are claimed, constructed, attached,
/// receive-started and flushed-empty); S4 = full readiness (the datagram flushes through the fake
/// transport). Two component probes (C1 claim, C2 session construct/start) and the fake baseline
/// (F) plus the flow-key construction probe (H) split the merged stages by difference.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class SessionSetupDecompositionBenchmarks
{
    /// <summary>The product's setup-limiter width; mirrors <c>UdpSessionSetup.MaximumConcurrentSetups</c>.</summary>
    private const int MaximumConcurrentSetups = 8;
    private static readonly TimeSpan s_readinessTimeout = TimeSpan.FromSeconds(60);
    private static readonly byte[] s_payload = [1];

    /// <summary>Two bytes against a one-byte setup budget: every admission is rejected (budget backpressure), so no datagram is buffered or flushed.</summary>
    private static readonly byte[] s_overBudgetPayload = [1, 2];

    [Params(1, 100, 1000)]
    public int Sessions { get; set; }

    private Socks5Server _socks = null!;
    private readonly List<Fixture> _pending = [];
    private readonly List<ComponentFixture> _pendingComponents = [];

    [GlobalSetup]
    public void Setup() => _socks = new Socks5Server("benchmark", "127.0.0.1", 1080, Username: null, Password: null);

    /// <summary>
    /// Disposes every fixture the measured iteration left behind. Synchronous by necessity:
    /// BenchmarkDotNet 0.15.8's generated boilerplate assigns iteration cleanup to a plain
    /// <c>Action</c>, and the teardown must not run inside the workload (the engine reads the GC
    /// counters before cleanup — that exclusion is the point of the stage variants). Every await
    /// inside the disposal chain uses <c>ConfigureAwait(false)</c> on thread-pool continuations, so
    /// blocking the benchmark host thread here cannot deadlock.
    /// </summary>
    [IterationCleanup]
    public void CleanupIteration()
    {
#pragma warning disable VSTHRD002 // Blocking is the harness contract: iteration cleanup is a synchronous Action and runs outside the allocation window.
        foreach (var fixture in _pending)
        {
            fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _pending.Clear();
        foreach (var component in _pendingComponents)
        {
            component.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _pendingComponents.Clear();
#pragma warning restore VSTHRD002
    }

    /// <summary>S0: the coordinator and its borrowed resources, no traffic. The fixed cost baseline (per invocation, not per session).</summary>
    [Benchmark]
    public void StageS0_CoordinatorOnly()
    {
        var fixture = CreateFixture(new BenchmarkUdpTransportFactory(), new SetupExecutor(), Sessions);
        Assert(fixture.Coordinator.SessionCount == 0, "the S0 baseline must not create sessions");
        _pending.Add(fixture);
    }

    /// <summary>S1: admission — slot, setup task enqueue, setup-queue charge + lease + copy. The enqueue-only executor keeps the background setup pipeline stopped.</summary>
    [Benchmark]
    public async Task StageS1_AdmissionOnlyAsync()
    {
        var executor = new AdmissionOnlySetupExecutor();
        var fixture = CreateFixture(new BenchmarkUdpTransportFactory(), executor, Sessions);
        var admitted = 0;
        for (var index = 0; index < Sessions; index++)
        {
            if (await TrySendAsync(fixture.Coordinator, index, s_payload).ConfigureAwait(false)) admitted++;
        }

        Assert(admitted == Sessions, "every S1 admission must be accepted");
        Assert(fixture.Coordinator.SessionCount == Sessions, "S1 must register one slot per admitted session");
        Assert(executor.Enqueued == Sessions, "S1 must enqueue one setup item per session");
        _pending.Add(fixture);
    }

    /// <summary>S2: admission plus background setup start — executor enqueue/worker, the setup state machine, the limiter queue, and the dial-start re-stamp for the eight setups that reach the gated dial.</summary>
    [Benchmark]
    public async Task StageS2_SetupStartedAsync()
    {
        var factory = new GatedTransportFactory();
        var executor = new CountingSetupExecutor();
        var fixture = CreateFixture(factory, executor, Sessions);
        var admitted = 0;
        for (var index = 0; index < Sessions; index++)
        {
            if (await TrySendAsync(fixture.Coordinator, index, s_payload).ConfigureAwait(false)) admitted++;
        }

        Assert(admitted == Sessions, "every S2 admission must be accepted");
        var expectedDials = Math.Min(Sessions, MaximumConcurrentSetups);
        var started = await WaitForStableCountAsync(
            () => executor.Started,
            () => factory.Entered >= expectedDials && fixture.Coordinator.SessionCount == Sessions,
            "S2 setup starts").ConfigureAwait(false);
        Assert(factory.Entered == expectedDials, "S2 must saturate the 8-wide setup limiter");
        Assert(started >= expectedDials, "S2 must have started at least the dialing setups");
        _pending.Add(fixture);
    }

    /// <summary>S3: the full construction pipeline without a buffered payload — association claim, session (context, quiescence scope, linked CTS), attach, receive-loop start (state machine + receive-window lease), flush-empty and ready flip. Readiness is asserted by probing each flow until its slot admits directly.</summary>
    [Benchmark]
    public async Task StageS3_PipelineNoPayloadAsync()
    {
        var factory = new BenchmarkUdpTransportFactory();
        var executor = new CountingSetupExecutor();
        var fixture = CreateFixture(factory, executor, Sessions, setupQueueBudget: 1);
        var admitted = 0;
        for (var index = 0; index < Sessions; index++)
        {
            if (await TrySendAsync(fixture.Coordinator, index, s_overBudgetPayload).ConfigureAwait(false)) admitted++;
        }

        Assert(admitted == 0, "the one-byte setup budget must reject every buffered datagram");
        await AwaitAllReadyAsync(fixture.Coordinator, factory, executor).ConfigureAwait(false);
        Assert(fixture.Coordinator.SessionCount == Sessions, "S3 must construct and attach one session per flow");
        _pending.Add(fixture);
    }

    /// <summary>S4: full readiness with the datagram flushed through the fake transport (today's probe shape, teardown outside the window).</summary>
    [Benchmark]
    public async Task StageS4_ReadinessTeardownOutsideAsync()
    {
        var fixture = CreateFixture(new BenchmarkUdpTransportFactory(), new CountingSetupExecutor(), Sessions);
        await PopulateAndAwaitReadyAsync(fixture).ConfigureAwait(false);
        _pending.Add(fixture);
    }

    /// <summary>S5: full readiness with the coordinator disposal inside the measured window (today's probe shape). The S5 minus S4 difference is the teardown cost (T).</summary>
    [Benchmark]
    public async Task StageS5_ReadinessTeardownInsideAsync()
    {
        var fixture = CreateFixture(new BenchmarkUdpTransportFactory(), new CountingSetupExecutor(), Sessions);
        await PopulateAndAwaitReadyAsync(fixture).ConfigureAwait(false);
        await fixture.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>F: the fake transport's own per-session cost (<c>BenchmarkUdpTransport</c> + two <c>IPEndPoint</c>s), reported and subtracted from every stage.</summary>
    [Benchmark]
    public async Task BaselineF_FakeTransportCreationAsync()
    {
        var factory = new BenchmarkUdpTransportFactory();
        for (var index = 0; index < Sessions; index++)
        {
            var transport = await factory.CreateAsync(_socks, CancellationToken.None).ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        Assert(factory.Sends == 0, "the F baseline must not send");
    }

    /// <summary>H: the probe harness's own per-session cost — flow-key construction (two parsed addresses + string formatting), present in the existing probe's measured window and subtracted alongside F.</summary>
    [Benchmark]
    public void BaselineH_FlowKeyConstruction()
    {
        for (var index = 0; index < Sessions; index++)
        {
            GC.KeepAlive(BenchmarkShared.CreateFlowKey(index));
        }
    }

    /// <summary>C1: relay-alias construction plus association claim and release (the setup pipeline's association tier, measured directly).</summary>
    [Benchmark]
    public void ComponentC1_AssociationClaimRelease()
    {
        var table = new UdpAssociationTable(Sessions, initialCapacity: Math.Min(Sessions, 1024));
        var now = TimeProvider.System.GetUtcNow();
        for (var index = 0; index < Sessions; index++)
        {
            var flow = BenchmarkShared.CreateFlowKey(index);
            var alias = new RelayAlias(FlowKey.Create(
                Endpoint.From(IPAddress.Loopback, checked((ushort)(10_000 + (index % 50_000)))),
                Endpoint.From(IPAddress.Loopback, 50_000),
                TransportProtocol.Udp,
                flow.Origin));
            if (!table.TryClaim(flow, alias, now, out var association, out var created) || !created)
            {
                throw new InvalidOperationException("the C1 component probe must claim a fresh association per session");
            }

            if (!table.TryRemove(association!)) throw new InvalidOperationException("the C1 component probe must release its association");
        }

        Assert(!table.TryFindOriginal(BenchmarkShared.CreateFlowKey(0), now, out _), "C1 must release every association");
    }

    /// <summary>C2: the session tier measured directly — context record, session, quiescence scope (linked CTS), receive-loop start with the receive-window lease rent. Readiness is asserted by the fake transport observing one receive entry per session.</summary>
    [Benchmark]
    public async Task ComponentC2_SessionConstructStartAsync()
    {
        const int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;
        var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));
        var shutdown = new CancellationTokenSource();
        var factory = new CountingFakeTransportFactory();
        var table = new UdpAssociationTable(Sessions, initialCapacity: Math.Min(Sessions, 1024));
        var now = TimeProvider.System.GetUtcNow();
        var sessions = new List<UdpProxySession>(Sessions);
        for (var index = 0; index < Sessions; index++)
        {
            var flow = BenchmarkShared.CreateFlowKey(index);
            var transport = new CountingFakeTransport(10_000 + index, factory);
            var alias = new RelayAlias(FlowKey.Create(
                Endpoint.From(transport.LocalEndpoint.Address, checked((ushort)transport.LocalEndpoint.Port)),
                Endpoint.From(transport.RelayEndpoint.Address, checked((ushort)transport.RelayEndpoint.Port)),
                TransportProtocol.Udp,
                flow.Origin));
            var association = table.Claim(flow, alias, now);
            var session = new UdpProxySession(new UdpProxySessionContext(
                flow,
                FlowGeneration: 0,
                association,
                transport,
                NoopUdpResponseSink.Instance,
                ClientMac: default,
                TimeProvider.System,
                static (_, _) => { },
                NullRuntimeLogger.Instance,
                receiveWindowPool,
                UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize),
                shutdown.Token));
            session.Start(static _ => { });
            sessions.Add(session);
        }

        await WaitUntilAsync(() => factory.ReceiveEntries >= Sessions, "C2 receive-loop starts").ConfigureAwait(false);
        Assert(sessions.Count == Sessions, "C2 must construct one session per flow");
        _pendingComponents.Add(new ComponentFixture(sessions, receiveWindowPool, shutdown));
    }

    private async Task PopulateAndAwaitReadyAsync(Fixture fixture)
    {
        var factory = (BenchmarkUdpTransportFactory)fixture.TransportFactory;
        var executor = (CountingSetupExecutor)fixture.SetupExecutor;
        var admitted = 0;
        for (var index = 0; index < Sessions; index++)
        {
            if (await TrySendAsync(fixture.Coordinator, index, s_payload).ConfigureAwait(false)) admitted++;
        }

        Assert(admitted == Sessions, "every admission must be accepted");
        await WaitUntilAsync(
            () => factory.Sends == Sessions && executor.Pending == 0,
            "full readiness (flush reached the fake transport and every setup pipeline returned)").ConfigureAwait(false);
        Assert(factory.Sends == Sessions, "the flush must reach the fake transport once per session");
    }

    /// <summary>Probes every flow with one datagram per round until each slot admits directly and every setup pipeline has returned; a not-yet-ready slot rejects the two-byte payload through the one-byte budget, a ready slot forwards the probe to the fake transport (counted in <c>Sends</c>).</summary>
    private async Task AwaitAllReadyAsync(UdpProxyCoordinator coordinator, BenchmarkUdpTransportFactory factory, CountingSetupExecutor executor)
    {
        var stopwatch = Stopwatch.StartNew();
        while (factory.Sends < Sessions || executor.Pending != 0)
        {
            if (stopwatch.Elapsed > s_readinessTimeout)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Only {factory.Sends} of {Sessions} sessions admitted directly within {s_readinessTimeout.TotalSeconds:0}s."));
            }

            for (var index = 0; index < Sessions && factory.Sends < Sessions; index++)
            {
                _ = await TrySendAsync(coordinator, index, s_overBudgetPayload).ConfigureAwait(false);
            }
        }

        Assert(coordinator.SessionCount == Sessions, "every flow must still own its slot");
    }

    private async ValueTask<bool> TrySendAsync(UdpProxyCoordinator coordinator, int index, byte[] payload) =>
        await coordinator.TrySendSpanAsync(BenchmarkShared.CreateFlowKey(index), _socks, payload, default, CancellationToken.None).ConfigureAwait(false);

    private static Fixture CreateFixture(IUdpProxyTransportFactory factory, ISetupExecutor executor, int capacity, long? setupQueueBudget = null)
    {
        const int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;
        var setupQueuePool = new NativeBufferPool(maximumFrameSize);
        var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));
        var options = new UdpProxyOptions { Capacity = capacity };
        if (setupQueueBudget is { } budget) options = options with { SetupQueueGlobalByteBudget = budget };
        var coordinator = new UdpProxyCoordinator(factory, NoopUdpResponseSink.Instance, setupQueuePool, receiveWindowPool, executor, options);
        return new Fixture(coordinator, factory, setupQueuePool, receiveWindowPool, executor);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        var spins = 0;
        while (!condition())
        {
            if (stopwatch.Elapsed > s_readinessTimeout)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{description} did not settle within {s_readinessTimeout.TotalSeconds:0}s."));
            }

            if (spins < 2_000)
            {
                spins++;
                Thread.SpinWait(20);
            }
            else
            {
                await Task.Delay(1).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Waits for the prerequisite, then for the counter to stop advancing (three equal 10 ms samples): the executor's worker-bound start phase has settled.</summary>
    private static async Task<long> WaitForStableCountAsync(Func<long> counter, Func<bool> prerequisite, string description)
    {
        await WaitUntilAsync(prerequisite, description).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var previous = -1L;
        var stable = 0;
        while (stable < 3)
        {
            if (stopwatch.Elapsed > s_readinessTimeout)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{description} never stopped advancing within {s_readinessTimeout.TotalSeconds:0}s."));
            }

            var current = counter();
            stable = current == previous ? stable + 1 : 0;
            previous = current;
            await Task.Delay(10).ConfigureAwait(false);
        }

        return previous;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>One measured invocation's coordinator and its borrowed resources; disposed together in <see cref="CleanupIteration"/>.</summary>
    private sealed class Fixture(
        UdpProxyCoordinator coordinator,
        IUdpProxyTransportFactory transportFactory,
        NativeBufferPool setupQueuePool,
        NativeBufferPool receiveWindowPool,
        ISetupExecutor setupExecutor) : IAsyncDisposable
    {
        public UdpProxyCoordinator Coordinator { get; } = coordinator;

        public IUdpProxyTransportFactory TransportFactory { get; } = transportFactory;

        public ISetupExecutor SetupExecutor { get; } = setupExecutor;

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync().ConfigureAwait(false);
            SetupExecutor.Dispose();
            setupQueuePool.Dispose();
            receiveWindowPool.Dispose();
        }
    }

    /// <summary>The C2 probe's fixtures: constructed sessions, their shared receive-window pool, and the lifetime token they link to.</summary>
    private sealed class ComponentFixture(List<UdpProxySession> sessions, NativeBufferPool receiveWindowPool, CancellationTokenSource shutdown) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            receiveWindowPool.Dispose();
            shutdown.Dispose();
        }
    }

    /// <summary>
    /// The S1 executor: rents exactly what <see cref="SetupExecutor.RentItem"/> rents on a cold
    /// free list (a fresh <see cref="SetupWorkItem"/> plus its payload planes) and accepts the
    /// enqueue, but starts no worker and no pipeline — the admission stage it serves must not pay
    /// the background setup machinery (that is the S2 delta). The item's completion is canceled on
    /// acceptance so the coordinator's disposal never waits on a setup that will not run.
    /// </summary>
    private sealed class AdmissionOnlySetupExecutor : ISetupExecutor
    {
        private long _enqueued;

        public long Enqueued => Interlocked.Read(ref _enqueued);

        public SetupWorkItem RentItem(Func<SetupWorkItem, Task> handler) => new() { _handler = handler };

        public bool TryEnqueue(SetupWorkItem item)
        {
            item._completion?.TrySetCanceled(CancellationToken.None);
            Interlocked.Increment(ref _enqueued);
            return true;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>The real executor behind a wrapper that counts handler invocations without allocating per rent (the tracked delegate is cached).</summary>
    private sealed class CountingSetupExecutor : ISetupExecutor
    {
        private readonly SetupExecutor _inner = new();
        private readonly Func<SetupWorkItem, Task> _tracked;
        private Func<SetupWorkItem, Task> _handler = null!;
        private long _started;

        public CountingSetupExecutor() => _tracked = InvokeTrackedAsync;

        /// <summary>Setup pipelines the executor's workers have invoked (the handler entered; its state machine is boxed at the first await inside the invocation).</summary>
        public long Started => Interlocked.Read(ref _started);

        /// <summary>Setup items enqueued and not yet completed (the inner executor's accounting); zero means every setup pipeline has fully returned.</summary>
        public int Pending => _inner.PendingCount;

        public SetupWorkItem RentItem(Func<SetupWorkItem, Task> handler)
        {
            _handler = handler;
            return _inner.RentItem(_tracked);
        }

        public bool TryEnqueue(SetupWorkItem item) => _inner.TryEnqueue(item);

        public void Dispose() => _inner.Dispose();

        private Task InvokeTrackedAsync(SetupWorkItem item)
        {
            Interlocked.Increment(ref _started);
            return _handler(item);
        }
    }

    /// <summary>The S2 dial gate: counts setups that left the limiter and blocks them in the transport factory, so the pipeline stops before the association claim and session construction. The coordinator's shutdown cancellation releases every blocked dial during cleanup.</summary>
    private sealed class GatedTransportFactory : IUdpProxyTransportFactory
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _entered;

        public long Entered => Interlocked.Read(ref _entered);

        public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _entered);
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The S2 dial gate must never open.");
        }
    }

    /// <summary>The C2 probe's transport: the fake's shape plus a receive-entry counter.</summary>
    private sealed class CountingFakeTransport(int localPort, CountingFakeTransportFactory owner) : IUdpProxyTransport
    {
        public IPEndPoint RelayEndpoint { get; } = new(IPAddress.Loopback, 50_000);
        public IPEndPoint LocalEndpoint { get; } = new(IPAddress.Loopback, localPort);

        public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            owner.NoteReceiveEntry();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The C2 receive should end through cancellation.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingFakeTransportFactory
    {
        private long _receiveEntries;

        public long ReceiveEntries => Interlocked.Read(ref _receiveEntries);

        public void NoteReceiveEntry() => Interlocked.Increment(ref _receiveEntries);
    }
}
