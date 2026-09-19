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
/// setup executor, and the shared SOCKS5 address cache. Creation, registration, rollback, and
/// disposal stay in <see cref="DurableCaptureBundle"/>; the coordinator owns the pools exactly
/// as it always has.
/// </summary>
internal sealed record UdpProxyComposition(
    UdpAdapterTargetSource Targets,
    int MaximumFrameSize,
    NativeBufferPool SetupQueuePool,
    NativeBufferPool ReceiveWindowPool,
    SetupExecutor SetupExecutor,
    Socks5AddressCache AddressCache);

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

    internal static UdpProxyCoordinator Create(
        IPacketReinjector reinjector,
        SelfTrafficRegistry selfTraffic,
        IRuntimeLogger logger,
        IInterceptionHealthSignal? healthSignal,
        UdpProxyComposition composition)
        => new(
            new Socks5UdpTransportFactory(selfTraffic, composition.MaximumFrameSize, composition.AddressCache),
            new UdpResponseReinjector(reinjector, composition.Targets, maximumFrameSize: composition.MaximumFrameSize, logger: logger, healthSignal: healthSignal),
            new UdpProxyOptions
            {
                Logger = logger,
                MaximumFrameSize = composition.MaximumFrameSize,
                ReceiveWindowPool = composition.ReceiveWindowPool,
                SetupQueuePool = composition.SetupQueuePool,
                SetupExecutor = composition.SetupExecutor
            });
}
