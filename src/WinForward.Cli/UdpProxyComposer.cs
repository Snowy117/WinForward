using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinForward.Cli.Logging;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Cli;

/// <summary>
/// The bundle-created collaborators the durable UDP coordinator is wired from: the refreshable
/// reinjection-target snapshot, the pinned frame cap, the shared native pools and setup executor,
/// and the shared SOCKS5 address cache. Creation, registration, rollback, and disposal stay in
/// <see cref="DurableCaptureBundle"/>; the coordinator borrows the pools, the executor, and the
/// address cache and never disposes them. Every UDP flow's association is the flow transport's
/// own, so the composition carries no association owner.
/// </summary>
internal sealed record UdpProxyComposition(
    UdpAdapterTargetSource Targets,
    AdapterSlotTable Slots,
    int MaximumFrameSize,
    NativeBufferPool SetupQueuePool,
    NativeBufferPool ReceiveWindowPool,
    SetupExecutor SetupExecutor,
    Socks5AddressCache AddressCache,
    int SessionCapacity,
    int RelayReceiveBufferBytes,
    ActivityBucketClock? ActivityClock = null);

/// <summary>
/// Composes the durable <see cref="UdpProxyCoordinator"/> from its
/// <see cref="UdpProxyComposition"/> and pre-resolves the shared SOCKS5 endpoints; the bundle
/// keeps teardown ordering and only the wiring moves here.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UdpProxyComposer
{
    internal static async Task PrimeSocks5AddressCacheAsync(ValidatedConfiguration configuration, Socks5AddressCache cache, ILogger logger)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var target in configuration.Targets.Values)
        {
            // A local target is already an IP literal, so only SOCKS5 servers have an address to resolve.
            if (target.Socks5 is not { } server) continue;
            try
            {
                await cache.ResolveAsync(server.Host, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var detail = $"{exception.GetType().Name}: {exception.Message}";
                StartupLog.Socks5AddressResolutionFailed(logger, server.Name, detail);
            }
        }
    }

    /// <summary>
    /// Creates the UDP coordinator over the composition's collaborators. The SOCKS5 transport
    /// factory dials, ASSOCIATEs, and binds one association per flow, so the coordinator owns no
    /// association lifecycle of its own.
    /// </summary>
    internal static UdpProxyCoordinator Create(
        IPacketReinjector reinjector,
        SelfTrafficRegistry selfTraffic,
        ILogger logger,
        IInterceptionHealthSignal? healthSignal,
        UdpProxyComposition composition)
        => new(
            new UdpTransportFactory(
                new Socks5UdpTransportFactory(selfTraffic, composition.MaximumFrameSize, composition.RelayReceiveBufferBytes, composition.AddressCache, logger),
                new LocalUdpTransportFactory(selfTraffic, composition.MaximumFrameSize, composition.RelayReceiveBufferBytes)),
            new UdpResponseReinjector(reinjector, composition.Targets, composition.Slots, maximumFrameSize: composition.MaximumFrameSize, logger: logger, healthSignal: healthSignal),
            composition.SetupQueuePool,
            composition.ReceiveWindowPool,
            composition.SetupExecutor,
            new UdpProxyOptions
            {
                Logger = logger,
                MaximumFrameSize = composition.MaximumFrameSize,
                Capacity = composition.SessionCapacity,
                RelayReceiveBufferBytes = composition.RelayReceiveBufferBytes,
                ActivityClock = composition.ActivityClock,
            });
}
