using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;
using WinForward.Windows;

namespace WinForward.Cli;

/// <summary>
/// The durable capture layer (task 09-07-adapter-list-refresh, design §2): built once per run and
/// never rebuilt across adapter-list refreshes. Owns the redirect table and both proxy
/// coordinators, the dispatcher/executor chain, the idle sweeper, and the refreshable UDP
/// reinjection-target snapshot; per-generation state (mode controller, pumps, transactional
/// runtime) is built around the shared packet processor by <see cref="LayeredCaptureRunner"/>
/// generations instead. Disposal is single-flight, ordered sweeper → UDP → TCP, and runs exactly
/// once after the final generation completes — that generation's cleanup has already released the
/// pumps and restored adapter modes (design R-1).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DurableCaptureBundle : IAsyncDisposable
{
    private readonly IdleExpirySweeper _sweeper;
    private readonly NativeBufferPool? _synCopyPool;
    private readonly NativeBufferPool? _relayPool;
    private readonly NativeBufferPool? _udpDatagramPool;
    private readonly NativeBufferPool? _udpWindowPool;
    private readonly NativeBufferPool? _attributionPool;
    private readonly SetupExecutor? _setupExecutor;
    private readonly IRuntimeLogger _logger;
    private readonly Lock _gate = new();
    private Task? _disposeTask;

    private const string SynCopyPoolName = "tcp.synCopy";
    private const string RelayPoolName = "tcp.relay";
    private const string UdpDatagramPoolName = "udp.setupQueue";
    private const string UdpWindowPoolName = "udp.receiveWindow";
    private const string AttributionPoolName = "flow.attribution";
    private HashSet<string>? _lastNoMacAdapters;
    private string? _lastZeroMacHostId;

    /// <summary>
    /// Direct fabrication over already-built collaborators; the production path is
    /// <see cref="CreateAsync"/>. Internal (not private) so tests can exercise
    /// <see cref="UpdateUdpTargets"/> against fake coordinators without the driver-backed
    /// composition.
    /// </summary>
    internal DurableCaptureBundle(
        FlowDispatcher dispatcher,
        NdisPacketActionExecutor executor,
        UdpAdapterTargetSource udpTargets,
        IdleExpirySweeper sweeper,
        UdpProxyCoordinator udp,
        TcpProxyCoordinator tcp,
        IRuntimeLogger logger,
        AdapterSlotTable? adapterSlots = null,
        NativeBufferPool? synCopyPool = null,
        NativeBufferPool? relayPool = null,
        NativeBufferPool? udpDatagramPool = null,
        NativeBufferPool? udpWindowPool = null,
        NativeBufferPool? attributionPool = null,
        SetupExecutor? setupExecutor = null,
        UdpAssociationPool? udpAssociationPool = null,
        ActivityBucketClock? activityClock = null,
        FlowAttributionWakeRegistry? wakeRegistry = null)
    {
        Dispatcher = dispatcher;
        Executor = executor;
        UdpTargets = udpTargets;
        _sweeper = sweeper;
        Udp = udp;
        Tcp = tcp;
        _logger = logger;
        AdapterSlots = adapterSlots ?? new AdapterSlotTable();
        _synCopyPool = synCopyPool;
        _relayPool = relayPool;
        _udpDatagramPool = udpDatagramPool;
        _udpWindowPool = udpWindowPool;
        _attributionPool = attributionPool;
        _setupExecutor = setupExecutor;
        UdpAssociations = udpAssociationPool;
        WakeRegistry = wakeRegistry;
        ActivityClock = activityClock ?? new ActivityBucketClock();
    }

    internal FlowDispatcher Dispatcher { get; }

    /// <summary>
    /// The one activity clock of this composition, ticked once per pump iteration by
    /// <see cref="FlushPendingInjections"/> and shared with the flow table and both coordinators, so
    /// every activity stamp and every sweep cutoff are on the same bucket.
    /// </summary>
    private ActivityBucketClock ActivityClock { get; }

    private NdisPacketActionExecutor Executor { get; }

    /// <summary>The refreshable UDP reinjection-target snapshot, swapped at every scope install.</summary>
    internal UdpAdapterTargetSource UdpTargets { get; }

    /// <summary>
    /// The process-lived adapter interning table every key's slot and every reinjection target
    /// comes from. <c>Program.cs</c> hands it to the durable packet processor and the capture
    /// generation factory so slots are resolved once per adapter per generation.
    /// </summary>
    internal AdapterSlotTable AdapterSlots { get; }

    /// <summary>The durable TCP redirect coordinator (heartbeat usage source).</summary>
    internal TcpProxyCoordinator Tcp { get; }

    /// <summary>The durable UDP session coordinator (heartbeat usage source).</summary>
    internal UdpProxyCoordinator Udp { get; }

    /// <summary>The per-server association pool behind every UDP transport (heartbeat usage source).</summary>
    internal UdpAssociationPool? UdpAssociations { get; }

    /// <summary>
    /// The pipeline-owned wake events, one per adapter that registered a driver signal. The capture
    /// generation composes the composites; the bundle owns the registry's lifetime.
    /// </summary>
    internal FlowAttributionWakeRegistry? WakeRegistry { get; }

    /// <summary>
    /// Builds the durable layer for a run. Nothing in the bundle references a specific adapter
    /// enumeration: the UDP target snapshot starts scope-less and is populated by the capture
    /// runner's scope-installed callback at generation 0 and after every refresh.
    /// <paramref name="healthSignal"/> (task 09-17 R1-B) receives the interception-path failure
    /// observations the capture runner may answer with a forced refresh; null keeps every site
    /// on the no-op signal, so existing compositions are unchanged.
    /// </summary>
    internal static async ValueTask<DurableCaptureBundle> CreateAsync(
        ValidatedConfiguration configuration,
        IPacketReinjector reinjector,
        SelfTrafficRegistry selfTraffic,
        IRuntimeLogger logger,
        IInterceptionHealthSignal? healthSignal = null,
        RuntimeCounters? counters = null)
    {
        var runtimeCounters = counters ?? RuntimeCounters.Shared;
        // One interning table for the whole process: keys carry a slot, so it must outlive every
        // capture generation and every adapter-list refresh (design §3.1).
        var adapterSlots = new AdapterSlotTable();
        // One activity clock for the whole composition: the flow table's warm stamps, both
        // coordinators' per-packet stamps and both sweeps' cutoffs must all land on the same bucket.
        var activityClock = new ActivityBucketClock();
        // The tcpFlowCapacity budget is the single source of truth for both the coordinator's
        // session gate and the redirect table's bounded capacity (design §4).
        var redirectTable = new TcpRedirectTable(capacity: configuration.TcpFlowCapacity);
        // One native pool backs retained SYNs and association reset templates (B1/B2); the
        // bundle owns it and the coordinator borrows it, so it is disposed here after teardown.
        var synCopyPool = new NativeBufferPool(NdisApiAbi.MaximumEthernetFrame);
        RegisterPool(runtimeCounters, SynCopyPoolName, synCopyPool);
        // One native pool backs the two per-direction relay pump windows (B11).
        var relayPool = new NativeBufferPool(TcpProxyRelayFactory.PumpBufferSize);
        RegisterPool(runtimeCounters, RelayPoolName, relayPool);
        // One address cache backs both proxies' SOCKS5 control connections (B9/R3): the configured
        // endpoint is resolved once here and reused on every TCP relay and UDP session setup.
        var addressCache = new Socks5AddressCache();
        // One pooled setup executor is shared by both coordinators (B5); the bundle owns it. The
        // configuration layer keeps 0 = auto (absent); the sentinel is translated here so the
        // executor's worker count has exactly one meaning (null = platform default).
        var setupExecutor = new SetupExecutor(configuration.SetupWorkerCount == 0 ? null : configuration.SetupWorkerCount);
        TcpProxyCoordinator tcpCoordinator;
        try
        {
            tcpCoordinator = TcpRedirectComposer.Create(configuration, reinjector, selfTraffic, logger, healthSignal, new TcpRedirectComposition(redirectTable, adapterSlots, synCopyPool, relayPool, setupExecutor, addressCache, activityClock));
        }
        catch
        {
            synCopyPool.Dispose();
            relayPool.Dispose();
            setupExecutor.Dispose();
            throw;
        }
        try
        {
            return await BuildWithUdpAsync(configuration, reinjector, selfTraffic, logger, healthSignal, runtimeCounters, tcpCoordinator, adapterSlots, synCopyPool, relayPool, setupExecutor, addressCache, activityClock).ConfigureAwait(false);
        }
        catch
        {
            await tcpCoordinator.DisposeAsync().ConfigureAwait(false);
            synCopyPool.Dispose();
            relayPool.Dispose();
            setupExecutor.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Registers a bundle-owned native pool with the runtime counters and points its accounting
    /// sink at pre-computed key strings, mirroring <c>Program.WireFramePoolDiagnostics</c>; the
    /// sink allocates nothing per rent/return and never throws.
    /// </summary>
    private static void RegisterPool(RuntimeCounters counters, string poolName, NativeBufferPool pool)
    {
        counters.RegisterPool(poolName);
        var rentKey = RuntimeCounters.PoolRentedKey(poolName);
        var returnKey = RuntimeCounters.PoolReturnedKey(poolName);
        pool.AccountingSink = rented => counters.Increment(rented ? rentKey : returnKey);
    }

    private static async ValueTask<DurableCaptureBundle> BuildWithUdpAsync(
        ValidatedConfiguration configuration,
        IPacketReinjector reinjector,
        SelfTrafficRegistry selfTraffic,
        IRuntimeLogger logger,
        IInterceptionHealthSignal? healthSignal,
        RuntimeCounters counters,
        TcpProxyCoordinator tcpCoordinator,
        AdapterSlotTable adapterSlots,
        NativeBufferPool synCopyPool,
        NativeBufferPool relayPool,
        SetupExecutor setupExecutor,
        Socks5AddressCache addressCache,
        ActivityBucketClock activityClock)
    {
        // Single source of truth for every datagram-path buffer bound: the transport send buffer
        // (6 + 16 + cap), the coordinator receive windows (cap + 22 + 1), the reinjector's
        // rebuilt-frame cap, and the native ABI capture size must all agree. Only the ABI constant
        // should ever change; every component follows it from here.
        const int maximumFrameSize = NdisApiAbi.MaximumEthernetFrame;
        var udpTargets = new UdpAdapterTargetSource(adapterSlots);
        await UdpProxyComposer.PrimeSocks5AddressCacheAsync(configuration, addressCache, logger).ConfigureAwait(false);
        // One native pool backs every queued setup datagram (B4); the bundle owns it and the
        // coordinator borrows it, so it is disposed here after release.
        var udpDatagramPool = new NativeBufferPool(maximumFrameSize);
        RegisterPool(counters, UdpDatagramPoolName, udpDatagramPool);
        // One native pool backs every session receive window (B11); its size comes from the
        // coordinator so the pool and the session window can never disagree.
        var udpWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));
        RegisterPool(counters, UdpWindowPoolName, udpWindowPool);
        // One native pool backs every retained attribution packet. Its capacity is the pipeline's
        // own global byte budget divided by the buffer size, so the pool can hold the budget's
        // worth of frames without an overflow allocation.
        var attributionPool = new NativeBufferPool(maximumFrameSize, (int)(FlowAttributionPendingIndex.DefaultGlobalByteBudget / maximumFrameSize));
        RegisterPool(counters, AttributionPoolName, attributionPool);
        // One association pool backs every UDP flow's control connection (Step 2); the bundle owns
        // it and the coordinator's transports borrow leases from it, so it outlives the coordinator.
        // The two bounds multiply into the shared head, so both ride the validated configuration.
        var associationPool = UdpProxyComposer.CreateAssociationPool(configuration, selfTraffic, addressCache, logger);
        UdpProxyCoordinator udpCoordinator;
        try
        {
            udpCoordinator = UdpProxyComposer.Create(reinjector, selfTraffic, logger, healthSignal, new UdpProxyComposition(udpTargets, adapterSlots, maximumFrameSize, udpDatagramPool, udpWindowPool, setupExecutor, addressCache, associationPool, SessionCapacity: configuration.UdpSessionCapacity, RelayReceiveBufferBytes: configuration.UdpRelayReceiveBufferBytes, ActivityClock: activityClock));
        }
        catch
        {
            await associationPool.DisposeAsync().ConfigureAwait(false);
            udpDatagramPool.Dispose();
            udpWindowPool.Dispose();
            attributionPool.Dispose();
            throw;
        }
        try
        {
            return BuildBundle(configuration, reinjector, selfTraffic, logger, healthSignal, udpTargets, adapterSlots, udpCoordinator, tcpCoordinator, synCopyPool, relayPool, udpDatagramPool, udpWindowPool, attributionPool, setupExecutor, associationPool, activityClock);
        }
        catch
        {
            await udpCoordinator.DisposeAsync().ConfigureAwait(false);
            await associationPool.DisposeAsync().ConfigureAwait(false);
            udpDatagramPool.Dispose();
            udpWindowPool.Dispose();
            attributionPool.Dispose();
            throw;
        }
    }

    private static DurableCaptureBundle BuildBundle(
        ValidatedConfiguration configuration,
        IPacketReinjector reinjector,
        SelfTrafficRegistry selfTraffic,
        IRuntimeLogger logger,
        IInterceptionHealthSignal? healthSignal,
        UdpAdapterTargetSource udpTargets,
        AdapterSlotTable adapterSlots,
        UdpProxyCoordinator udpCoordinator,
        TcpProxyCoordinator tcpCoordinator,
        NativeBufferPool synCopyPool,
        NativeBufferPool relayPool,
        NativeBufferPool udpDatagramPool,
        NativeBufferPool udpWindowPool,
        NativeBufferPool attributionPool,
        SetupExecutor setupExecutor,
        UdpAssociationPool associationPool,
        ActivityBucketClock activityClock)
    {
        var executor = new NdisPacketActionExecutor(reinjector, logger, tcpCoordinator, udpCoordinator, healthSignal: healthSignal);
        var dispatcher = new FlowDispatcher(
            configuration, selfTraffic, executor, new WindowsProcessAttributor(),
            reverseHandler: tcpCoordinator,
            fragmentHandler: tcpCoordinator.HandleFragmentAsync,
            logger: logger,
            activityClock: activityClock,
            attributionPool: attributionPool,
            setupExecutor: setupExecutor);
        var idleExpirySweeper = new IdleExpirySweeper(dispatcher, tcpCoordinator, udpCoordinator, relayIdleTimeout: configuration.UdpSessionIdleTimeout, logger: logger, attributionSweep: dispatcher.Attribution is { } attributionPipeline ? attributionPipeline.RemoveExpired : null);
        idleExpirySweeper.Start();
        var wakeRegistry = new FlowAttributionWakeRegistry();
        if (dispatcher.Attribution is { } pipeline) pipeline.Wake = wakeRegistry;
        return new DurableCaptureBundle(dispatcher, executor, udpTargets, idleExpirySweeper, udpCoordinator, tcpCoordinator, logger, adapterSlots, synCopyPool, relayPool, udpDatagramPool, udpWindowPool, attributionPool, setupExecutor, associationPool, activityClock, wakeRegistry);
    }

    /// <summary>
    /// The capture runner's scope-installed callback: swaps the UDP reinjection-target snapshot to
    /// the installed scope (design §3.4/R-5). The scope's first adapter becomes the host-flow
    /// fallback; adapters reporting an all-zero MAC are skipped with a warn (forwarded responses
    /// toward them drop fail-closed, host responses use the fallback), and a zero-MAC host
    /// fallback keeps today's zero-placeholder semantics. An empty scope clears the snapshot —
    /// interception is paused, so every response resolution drops fail-closed. The no-MAC warns
    /// are change-gated (task 09-17 R2.4): every refresh re-installs the scope, so per-install
    /// warns flooded the log with the same line — the group warn fires only on the first
    /// occurrence and whenever the zero-MAC adapter set changes, and an empty zero-MAC set
    /// resets the memory so the next occurrence warns again.
    /// </summary>
    internal void UpdateUdpTargets(IReadOnlyList<AdapterEnumerationItem> scope)
    {
        if (scope.Count == 0)
        {
            UdpTargets.Update(host: null, new Dictionary<ushort, UdpAdapterTarget>());
            _lastNoMacAdapters = null;
            _lastZeroMacHostId = null;
            return;
        }

        var host = scope[0];
        var hostMac = host.Mac;
        if (IsZeroMac(hostMac))
        {
            if (!string.Equals(_lastZeroMacHostId, host.StableId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Event(RuntimeLogLevel.Warn, "udp.targets.noMac",
                    new("kind", "hostFallback"),
                    new("host", host.StableId),
                    new("name", host.Adapter.FriendlyName));
            }
            _lastZeroMacHostId = host.StableId;
            hostMac = new byte[NdisApiAbi.EthernetAddressLength];
        }
        else
        {
            _lastZeroMacHostId = null;
        }

        var noMacAdapters = new List<AdapterEnumerationItem>();
        var adapterTargets = new Dictionary<ushort, UdpAdapterTarget>();
        foreach (var adapter in scope)
        {
            if (IsZeroMac(adapter.Mac))
            {
                noMacAdapters.Add(adapter);
                continue;
            }
            // The same interning table the flow keys use: the scope's adapters were interned when
            // the generation was built, so the target map and the keys cannot disagree about which
            // adapter a flow came from.
            if (AdapterSlots.TryGetSlot(adapter.StableId, out var slot))
            {
                adapterTargets[slot] = new UdpAdapterTarget(adapter.Adapter.RuntimeHandle, adapter.Mac);
            }
        }
        WarnNoMacAdaptersOnChange(noMacAdapters);

        UdpTargets.Update(new UdpAdapterTarget(host.Adapter.RuntimeHandle, hostMac), adapterTargets);
    }

    /// <summary>
    /// The change-gated group warn for zero-MAC adapters: compares the current zero-MAC stable-ID
    /// set against the last emitted one and warns only on the first occurrence or a membership
    /// change. An all-zero set (every adapter has a MAC) resets the memory, so the next
    /// occurrence warns again instead of being compared against a stale set. Called only between
    /// generations from the runner's scope-installed callback, so plain fields need no lock.
    /// </summary>
    private void WarnNoMacAdaptersOnChange(List<AdapterEnumerationItem> noMacAdapters)
    {
        var current = new HashSet<string>(noMacAdapters.Select(static adapter => adapter.StableId), StringComparer.OrdinalIgnoreCase);
        if (current.Count == 0)
        {
            _lastNoMacAdapters = null;
            return;
        }
        if (_lastNoMacAdapters is { } previous && previous.SetEquals(current)) return;
        _lastNoMacAdapters = current;
        _logger.Event(RuntimeLogLevel.Warn, "udp.targets.noMac",
            new("kind", "adapters"),
            new("adapters", string.Join("; ", noMacAdapters.Select(static adapter => $"{adapter.Adapter.FriendlyName}({adapter.StableId})"))),
            new("count", (long)noMacAdapters.Count));
    }

    /// <summary>
    /// The capture runner's scope-installed callback home: swaps the UDP reinjection-target
    /// snapshot (see <see cref="UpdateUdpTargets"/>) and then retires the executor's pass lanes
    /// for adapters that left the scope. Both steps run strictly between generations — the runner
    /// completes the outgoing generation's run (including its loop-exit lane flush) before
    /// installing the next scope, so no pump can still be appending to a retired lane. An empty
    /// scope retires every lane: interception is paused, so no lane can accumulate. The redirect
    /// lanes take the same scope: a redirect lane is only drained by the pump it is keyed on, so a
    /// cross-adapter target that left the scope must stop deferring (its frames keep the immediate
    /// send, which fails closed through the per-flow tail).
    /// </summary>
    internal void OnScopeInstalled(IReadOnlyList<AdapterEnumerationItem> scope)
    {
        UpdateUdpTargets(scope);
        var handles = new nint[scope.Count];
        for (var index = 0; index < scope.Count; index++) handles[index] = scope[index].Adapter.RuntimeHandle;
        Executor.RetireLanesExcept(handles);
        Tcp.UpdateRedirectTargets(handles);
    }

    /// <summary>
    /// The pump's batch-completed callback: every pass frame accumulated for
    /// <paramref name="adapterHandle"/> leaves in one batched send per direction, then every
    /// deferred redirect frame for that adapter. The pass flush deliberately runs first: the one
    /// plausible same-flow interleaving is a data frame that passed before its flow's association
    /// existed (a redirect-setup race) followed by redirect frames once the background setup
    /// completes, and pass-first preserves that capture order.
    /// </summary>
    internal void FlushPendingInjections(nint adapterHandle)
    {
        // The one per-iteration clock read of the data path (the pump invokes this once per iteration
        // and once at loop exit, including empty polls): every activity stamp and sweep cutoff is served
        // from the bucket this publishes, so no packet path reads a clock.
        ActivityClock.Tick();
        // Decided attributions first: their pass frames must reach the lanes before this iteration's
        // flush, and this callback is also the loop-exit one, so a delivery here is never left
        // lane'd when RetireLanesExcept runs.
        Dispatcher.Attribution?.DeliverDecided(adapterHandle);
        Executor.FlushPendingPasses(adapterHandle);
        Tcp.FlushPendingRedirectInjections(adapterHandle);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _sweeper.DisposeAsync().ConfigureAwait(false);
            // After the sweeper (no more TTL ticks) and before any pool: sealing the pipeline is
            // what stops new attribution work, and every pending entry settles while the pumps have
            // stopped and the pools it releases into are still alive.
            if (Dispatcher.Attribution is { } attribution) await attribution.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await Udp.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    // After the UDP coordinator drained every session (and with it every association
                    // lease), close the associations: no lease may outlive its pool.
                    if (UdpAssociations is not null) await UdpAssociations.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        // After the UDP coordinator drained and released every queued setup lease
                        // and every session receive window.
                        _udpDatagramPool?.Dispose();
                        _udpWindowPool?.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            await Tcp.DisposeAsync().ConfigureAwait(false);
                        }
                        finally
                        {
                            // After the coordinator released every lease it held, drain the syn-copy pool.
                            _synCopyPool?.Dispose();
                            _attributionPool?.Dispose();
                            _relayPool?.Dispose();
                            // Last of the wake owners: the pipeline that signalled these events is
                            // already sealed and every pump has stopped, so no waiter can be parked.
                            WakeRegistry?.Dispose();
                            _setupExecutor?.Dispose();
                        }
                    }
                }
            }
        }
    }

    private static bool IsZeroMac(byte[] mac) => mac.All(static octet => octet == 0);
}
