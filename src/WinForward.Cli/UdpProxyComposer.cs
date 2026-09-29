using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Cli;

/// <summary>
/// The bundle-created collaborators the durable UDP coordinator is wired from (P3): the
/// refreshable reinjection-target snapshot, the pinned frame cap, the shared native pools and
/// setup executor, the shared SOCKS5 address cache, and the per-server association pool. Creation,
/// registration, rollback, and disposal stay in <see cref="DurableCaptureBundle"/>; the coordinator
/// borrows the pools, the executor, and the association pool and never disposes them.
/// </summary>
internal sealed record UdpProxyComposition(
    UdpAdapterTargetSource Targets,
    int MaximumFrameSize,
    NativeBufferPool SetupQueuePool,
    NativeBufferPool ReceiveWindowPool,
    SetupExecutor SetupExecutor,
    Socks5AddressCache AddressCache,
    UdpAssociationPool Associations,
    int SessionCapacity,
    int RelayReceiveBufferBytes);

/// <summary>
/// Composes the durable <see cref="UdpProxyCoordinator"/> from its
/// <see cref="UdpProxyComposition"/> and pre-resolves the shared SOCKS5 endpoints; the bundle
/// keeps teardown ordering and only the wiring moves here.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UdpProxyComposer
{
    internal static async Task PrimeSocks5AddressCacheAsync(ValidatedConfiguration configuration, Socks5AddressCache cache, IRuntimeLogger logger)
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

    /// <summary>
    /// Creates the per-server association pool from the validated placement bounds and reuse mode.
    /// The bounds multiply into the shared head, which is why both are passed as distinct arguments
    /// here rather than read from the pool's defaults. The caller owns the pool and disposes it
    /// after the coordinator has released every lease.
    /// </summary>
    internal static UdpAssociationPool CreateAssociationPool(
        ValidatedConfiguration configuration,
        SelfTrafficRegistry selfTraffic,
        Socks5AddressCache addressCache,
        IRuntimeLogger logger)
        => new(
            selfTraffic,
            configuration.UdpAssociationReuse,
            addressCache,
            logger: logger,
            maxAssociationsPerServer: configuration.UdpAssociationMaxPerServer,
            flowsPerAssociation: configuration.UdpAssociationFlowsPerAssociation);

    internal static UdpProxyCoordinator Create(
        IPacketReinjector reinjector,
        SelfTrafficRegistry selfTraffic,
        IRuntimeLogger logger,
        IInterceptionHealthSignal? healthSignal,
        UdpProxyComposition composition)
        => new(
            new Socks5UdpTransportFactory(composition.Associations, selfTraffic, composition.MaximumFrameSize, composition.RelayReceiveBufferBytes),
            new UdpResponseReinjector(reinjector, composition.Targets, maximumFrameSize: composition.MaximumFrameSize, logger: logger, healthSignal: healthSignal),
            composition.SetupQueuePool,
            composition.ReceiveWindowPool,
            composition.SetupExecutor,
            new UdpProxyOptions
            {
                Logger = logger,
                MaximumFrameSize = composition.MaximumFrameSize,
                Capacity = composition.SessionCapacity,
                RelayReceiveBufferBytes = composition.RelayReceiveBufferBytes,
            });
}
