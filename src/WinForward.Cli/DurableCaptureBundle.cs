using System.Runtime.Versioning;
using WinForward.Configuration;
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
    private readonly IRuntimeLogger _logger;
    private readonly Lock _gate = new();
    private Task? _disposeTask;

    private DurableCaptureBundle(
        FlowDispatcher dispatcher,
        NdisPacketActionExecutor executor,
        UdpAdapterTargetSource udpTargets,
        IdleExpirySweeper sweeper,
        UdpProxyCoordinator udp,
        TcpProxyCoordinator tcp,
        IRuntimeLogger logger)
    {
        Dispatcher = dispatcher;
        Executor = executor;
        UdpTargets = udpTargets;
        _sweeper = sweeper;
        _udp = udp;
        _tcp = tcp;
        _logger = logger;
    }

    internal FlowDispatcher Dispatcher { get; }

    internal NdisPacketActionExecutor Executor { get; }

    /// <summary>The refreshable UDP reinjection-target snapshot, swapped at every scope install.</summary>
    internal UdpAdapterTargetSource UdpTargets { get; }

    /// <summary>
    /// Builds the durable layer for a run. Nothing in the bundle references a specific adapter
    /// enumeration: the UDP target snapshot starts scope-less and is populated by the capture
    /// runner's scope-installed callback at generation 0 and after every refresh.
    /// </summary>
    internal static async ValueTask<DurableCaptureBundle> CreateAsync(
        ValidatedConfiguration configuration,
        IPacketReinjector reinjector,
        SelfTrafficRegistry selfTraffic,
        IRuntimeLogger logger)
    {
        // The tcpFlowCapacity budget is the single source of truth for both the coordinator's
        // session gate and the redirect table's bounded capacity (design §4).
        var redirectTable = new TcpRedirectTable(capacity: configuration.TcpFlowCapacity);
        var tcpCoordinator = new TcpProxyCoordinator(
            new TcpRedirectListenerFactory(),
            new TcpProxyRelayFactory(selfTraffic, logger),
            new TcpRedirectInjector(reinjector),
            redirectTable,
            selfTraffic,
            new WindowsAdapterLocalAddressProvider(),
            logger,
            capacity: configuration.TcpFlowCapacity);
        try
        {
            // Single source of truth for every datagram-path buffer bound: the transport send
            // buffer (6 + 16 + cap), the coordinator receive windows (cap + 22 + 1), the
            // reinjector's rebuilt-frame cap, and the native ABI capture size must all agree.
            // Only the ABI constant should ever change; every component follows it from here.
            var maximumFrameSize = NdisApiAbi.MaximumEthernetFrame;
            var udpTargets = new UdpAdapterTargetSource();
            var udpCoordinator = new UdpProxyCoordinator(
                new Socks5UdpTransportFactory(selfTraffic, maximumFrameSize),
                new UdpResponseReinjector(reinjector, udpTargets, maximumFrameSize: maximumFrameSize, logger: logger),
                logger: logger,
                maximumFrameSize: maximumFrameSize);
            try
            {
                var executor = new NdisPacketActionExecutor(reinjector, logger, tcpCoordinator, udpCoordinator);
                var dispatcher = new FlowDispatcher(
                    configuration, selfTraffic, executor, new WindowsProcessAttributor(),
                    reverseHandler: tcpCoordinator,
                    fragmentHandler: tcpCoordinator.HandleFragmentAsync,
                    logger: logger);
                var idleExpirySweeper = new IdleExpirySweeper(dispatcher, tcpCoordinator, udpCoordinator, logger: logger);
                idleExpirySweeper.Start();
                return new DurableCaptureBundle(dispatcher, executor, udpTargets, idleExpirySweeper, udpCoordinator, tcpCoordinator, logger);
            }
            catch
            {
                await udpCoordinator.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            await tcpCoordinator.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The capture runner's scope-installed callback: swaps the UDP reinjection-target snapshot to
    /// the installed scope (design §3.4/R-5). The scope's first adapter becomes the host-flow
    /// fallback; adapters reporting an all-zero MAC are skipped with a warn (forwarded responses
    /// toward them drop fail-closed, host responses use the fallback), and a zero-MAC host
    /// fallback keeps today's zero-placeholder semantics. An empty scope clears the snapshot —
    /// interception is paused, so every response resolution drops fail-closed.
    /// </summary>
    internal void UpdateUdpTargets(IReadOnlyList<AdapterEnumerationItem> scope)
    {
        if (scope.Count == 0)
        {
            UdpTargets.Update(null, new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase));
            return;
        }

        var host = scope[0];
        var hostMac = host.Mac;
        if (IsZeroMac(hostMac))
        {
            _logger.Warn("UDP response reinjection will use a zero MAC because the adapter MAC is unavailable; verify on the target host.");
            hostMac = new byte[NdisApiAbi.EthernetAddressLength];
        }

        var adapterTargets = new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in scope)
        {
            if (IsZeroMac(adapter.Mac))
            {
                _logger.Warn($"UDP response reinjection has no MAC for adapter '{adapter.Adapter.FriendlyName}' ({adapter.StableId}); forwarded responses are dropped fail-closed and host responses use the fallback adapter.");
                continue;
            }
            adapterTargets[adapter.StableId] = new UdpAdapterTarget(adapter.Adapter.RuntimeHandle, adapter.Mac);
        }

        UdpTargets.Update(new UdpAdapterTarget(host.Adapter.RuntimeHandle, hostMac), adapterTargets);
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
                await _tcp.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool IsZeroMac(byte[] mac) => mac.All(static octet => octet == 0);
}
