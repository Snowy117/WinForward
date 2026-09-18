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
    private readonly UdpProxyCoordinator _udp;
    private readonly TcpProxyCoordinator _tcp;
    private readonly NativeBufferPool? _synCopyPool;
    private readonly NativeBufferPool? _relayPool;
    private readonly NativeBufferPool? _udpDatagramPool;
    private readonly NativeBufferPool? _udpWindowPool;
    private readonly SetupExecutor? _setupExecutor;
    private readonly IRuntimeLogger _logger;
    private readonly Lock _gate = new();
    private Task? _disposeTask;

    internal const string SynCopyPoolName = "tcp.synCopy";
    internal const string RelayPoolName = "tcp.relay";
    internal const string UdpDatagramPoolName = "udp.setupQueue";
    internal const string UdpWindowPoolName = "udp.receiveWindow";
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
        NativeBufferPool? synCopyPool = null,
        NativeBufferPool? relayPool = null,
        NativeBufferPool? udpDatagramPool = null,
        NativeBufferPool? udpWindowPool = null,
        SetupExecutor? setupExecutor = null)
    {
        Dispatcher = dispatcher;
        Executor = executor;
        UdpTargets = udpTargets;
        _sweeper = sweeper;
        _udp = udp;
        _tcp = tcp;
        _logger = logger;
        _synCopyPool = synCopyPool;
        _relayPool = relayPool;
        _udpDatagramPool = udpDatagramPool;
        _udpWindowPool = udpWindowPool;
        _setupExecutor = setupExecutor;
    }

    internal FlowDispatcher Dispatcher { get; }

    internal NdisPacketActionExecutor Executor { get; }

    /// <summary>The refreshable UDP reinjection-target snapshot, swapped at every scope install.</summary>
    internal UdpAdapterTargetSource UdpTargets { get; }

    /// <summary>The durable TCP redirect coordinator (heartbeat usage source).</summary>
    internal TcpProxyCoordinator Tcp => _tcp;

    /// <summary>The durable UDP session coordinator (heartbeat usage source).</summary>
    internal UdpProxyCoordinator Udp => _udp;

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
        // The tcpFlowCapacity budget is the single source of truth for both the coordinator's
        // session gate and the redirect table's bounded capacity (design §4).
        var redirectTable = new TcpRedirectTable(capacity: configuration.TcpFlowCapacity);
        // One native pool backs retained SYNs and association reset templates (B1/B2); the
        // coordinator owns it when none is injected, so production passes it and disposes it here.
        var synCopyPool = new NativeBufferPool(NdisApiAbi.MaximumEthernetFrame);
        RegisterPool(runtimeCounters, SynCopyPoolName, synCopyPool);
        // One native pool backs the two per-direction relay pump windows (B11).
        var relayPool = new NativeBufferPool(TcpProxyRelayFactory.PumpBufferSize);
        RegisterPool(runtimeCounters, RelayPoolName, relayPool);
        // One address cache backs both proxies' SOCKS5 control connections (B9/R3): the configured
        // endpoint is resolved once here and reused on every TCP relay and UDP session setup.
        var addressCache = new Socks5AddressCache();
        // One pooled setup executor is shared by both coordinators (B5); the bundle owns it.
        var setupExecutor = new SetupExecutor(configuration.SetupWorkerCount);
        TcpProxyCoordinator tcpCoordinator;
        try
        {
            tcpCoordinator = CreateTcpCoordinator(configuration, reinjector, selfTraffic, logger, healthSignal, redirectTable, synCopyPool, relayPool, setupExecutor, addressCache);
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
            return await BuildWithUdpAsync(configuration, reinjector, selfTraffic, logger, healthSignal, runtimeCounters, tcpCoordinator, synCopyPool, relayPool, setupExecutor, addressCache).ConfigureAwait(false);
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
        pool.AccountingSink = rented => { _ = counters.Increment(rented ? rentKey : returnKey); };
    }

    private static async ValueTask<DurableCaptureBundle> BuildWithUdpAsync(
        ValidatedConfiguration configuration,
        IPacketReinjector reinjector,
        SelfTrafficRegistry selfTraffic,
        IRuntimeLogger logger,
        IInterceptionHealthSignal? healthSignal,
        RuntimeCounters counters,
        TcpProxyCoordinator tcpCoordinator,
        NativeBufferPool synCopyPool,
        NativeBufferPool relayPool,
        SetupExecutor setupExecutor,
        Socks5AddressCache addressCache)
    {
        // Single source of truth for every datagram-path buffer bound: the transport send buffer
        // (6 + 16 + cap), the coordinator receive windows (cap + 22 + 1), the reinjector's
        // rebuilt-frame cap, and the native ABI capture size must all agree. Only the ABI constant
        // should ever change; every component follows it from here.
        var maximumFrameSize = NdisApiAbi.MaximumEthernetFrame;
        var udpTargets = new UdpAdapterTargetSource();
        await PrimeSocks5AddressCacheAsync(configuration, addressCache, logger).ConfigureAwait(false);
        // One native pool backs every queued setup datagram (B4); the coordinator owns it when
        // none is injected, so production passes it and disposes it here after release.
        var udpDatagramPool = new NativeBufferPool(maximumFrameSize);
        RegisterPool(counters, UdpDatagramPoolName, udpDatagramPool);
        // One native pool backs every session receive window (B11); its size comes from the
        // coordinator so the pool and the session window can never disagree.
        var udpWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));
        RegisterPool(counters, UdpWindowPoolName, udpWindowPool);
        UdpProxyCoordinator udpCoordinator;
        try
        {
            udpCoordinator = CreateUdpCoordinator(reinjector, selfTraffic, logger, healthSignal, udpTargets, maximumFrameSize, udpDatagramPool, udpWindowPool, setupExecutor, addressCache);
        }
        catch
        {
            udpDatagramPool.Dispose();
            udpWindowPool.Dispose();
            throw;
        }
        try
        {
            return BuildBundle(configuration, reinjector, selfTraffic, logger, healthSignal, udpTargets, udpCoordinator, tcpCoordinator, synCopyPool, relayPool, udpDatagramPool, udpWindowPool, setupExecutor);
        }
        catch
        {
            await udpCoordinator.DisposeAsync().ConfigureAwait(false);
            udpDatagramPool.Dispose();
            udpWindowPool.Dispose();
            throw;
        }
    }

    private static async Task PrimeSocks5AddressCacheAsync(ValidatedConfiguration configuration, Socks5AddressCache cache, IRuntimeLogger logger)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var server in configuration.Servers.Values)
        {
            try
            {
                await cache.ResolveAsync(server.Host, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.Warn($"SOCKS5 address pre-resolution failed for '{server.Name}': {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static DurableCaptureBundle BuildBundle(
        ValidatedConfiguration configuration,
        IPacketReinjector reinjector,
        SelfTrafficRegistry selfTraffic,
        IRuntimeLogger logger,
        IInterceptionHealthSignal? healthSignal,
        UdpAdapterTargetSource udpTargets,
        UdpProxyCoordinator udpCoordinator,
        TcpProxyCoordinator tcpCoordinator,
        NativeBufferPool synCopyPool,
        NativeBufferPool relayPool,
        NativeBufferPool udpDatagramPool,
        NativeBufferPool udpWindowPool,
        SetupExecutor setupExecutor)
    {
        var executor = new NdisPacketActionExecutor(reinjector, logger, tcpCoordinator, udpCoordinator, healthSignal: healthSignal);
        var dispatcher = new FlowDispatcher(
            configuration, selfTraffic, executor, new WindowsProcessAttributor(),
            reverseHandler: tcpCoordinator,
            fragmentHandler: tcpCoordinator.HandleFragmentAsync,
            logger: logger);
        var idleExpirySweeper = new IdleExpirySweeper(dispatcher, tcpCoordinator, udpCoordinator, logger: logger);
        idleExpirySweeper.Start();
        return new DurableCaptureBundle(dispatcher, executor, udpTargets, idleExpirySweeper, udpCoordinator, tcpCoordinator, logger, synCopyPool, relayPool, udpDatagramPool, udpWindowPool, setupExecutor);
    }

    private static UdpProxyCoordinator CreateUdpCoordinator(
        IPacketReinjector reinjector,
        SelfTrafficRegistry selfTraffic,
        IRuntimeLogger logger,
        IInterceptionHealthSignal? healthSignal,
        UdpAdapterTargetSource udpTargets,
        int maximumFrameSize,
        NativeBufferPool udpDatagramPool,
        NativeBufferPool udpWindowPool,
        SetupExecutor setupExecutor,
        Socks5AddressCache addressCache)
        => new(
            new Socks5UdpTransportFactory(selfTraffic, maximumFrameSize, addressCache),
            new UdpResponseReinjector(reinjector, udpTargets, maximumFrameSize: maximumFrameSize, logger: logger, healthSignal: healthSignal),
            logger: logger,
            maximumFrameSize: maximumFrameSize,
            receiveWindowPool: udpWindowPool,
            setupQueuePool: udpDatagramPool,
            setupExecutor: setupExecutor);

    private static TcpProxyCoordinator CreateTcpCoordinator(
        ValidatedConfiguration configuration,
        IPacketReinjector reinjector,
        SelfTrafficRegistry selfTraffic,
        IRuntimeLogger logger,
        IInterceptionHealthSignal? healthSignal,
        TcpRedirectTable redirectTable,
        NativeBufferPool synCopyPool,
        NativeBufferPool relayPool,
        SetupExecutor setupExecutor,
        Socks5AddressCache addressCache)
        => new(
            new TcpRedirectListenerFactory(),
            new TcpProxyRelayFactory(selfTraffic, logger, relayPool, addressCache),
            new TcpRedirectInjector(reinjector),
            redirectTable,
            selfTraffic,
            new WindowsAdapterLocalAddressProvider(),
            logger,
            capacity: configuration.TcpFlowCapacity,
            healthSignal: healthSignal,
            synCopyPool: synCopyPool,
            setupExecutor: setupExecutor);

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
            UdpTargets.Update(null, new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase));
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
        var adapterTargets = new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in scope)
        {
            if (IsZeroMac(adapter.Mac))
            {
                noMacAdapters.Add(adapter);
                continue;
            }
            adapterTargets[adapter.StableId] = new UdpAdapterTarget(adapter.Adapter.RuntimeHandle, adapter.Mac);
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
    /// scope retires every lane: interception is paused, so no lane can accumulate.
    /// </summary>
    internal void OnScopeInstalled(IReadOnlyList<AdapterEnumerationItem> scope)
    {
        UpdateUdpTargets(scope);
        var handles = new nint[scope.Count];
        for (var index = 0; index < scope.Count; index++) handles[index] = scope[index].Adapter.RuntimeHandle;
        Executor.RetireLanesExcept(handles);
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
        }
        finally
        {
            try
            {
                await _udp.DisposeAsync().ConfigureAwait(false);
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
                        await _tcp.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        // After the coordinator released every lease it held, drain the syn-copy pool.
                        _synCopyPool?.Dispose();
                        _relayPool?.Dispose();
                        _setupExecutor?.Dispose();
                    }
                }
            }
        }
    }

    private static bool IsZeroMac(byte[] mac) => mac.All(static octet => octet == 0);
}
