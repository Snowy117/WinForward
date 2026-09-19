using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.TcpRedirect;
using WinForward.Windows;

namespace WinForward.Cli;

/// <summary>
/// The bundle-created collaborators the durable TCP coordinator is wired from (P3): the redirect
/// table, the shared native pools and setup executor, and the shared SOCKS5 address cache.
/// Creation, registration, rollback, and disposal stay in <see cref="DurableCaptureBundle"/>;
/// the coordinator owns the pools exactly as it always has.
/// </summary>
internal sealed record TcpRedirectComposition(
    TcpRedirectTable RedirectTable,
    NativeBufferPool SynCopyPool,
    NativeBufferPool RelayPool,
    SetupExecutor SetupExecutor,
    Socks5AddressCache AddressCache);

/// <summary>
/// Composes the durable <see cref="TcpProxyCoordinator"/> from its
/// <see cref="TcpRedirectComposition"/>: the bundle keeps teardown ordering and only the wiring
/// moves here.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TcpRedirectComposer
{
    internal static TcpProxyCoordinator Create(
        ValidatedConfiguration configuration,
        IPacketReinjector reinjector,
        SelfTrafficRegistry selfTraffic,
        IRuntimeLogger logger,
        IInterceptionHealthSignal? healthSignal,
        TcpRedirectComposition composition)
        => new(
            new TcpRedirectListenerFactory(),
            new TcpProxyRelayFactory(selfTraffic, logger, composition.RelayPool, composition.AddressCache),
            new TcpRedirectInjector(reinjector),
            composition.RedirectTable,
            selfTraffic,
            new WindowsAdapterLocalAddressProvider(),
            new TcpRedirectOptions
            {
                Logger = logger,
                Capacity = configuration.TcpFlowCapacity,
                HealthSignal = healthSignal,
                SynCopyPool = composition.SynCopyPool,
                SetupExecutor = composition.SetupExecutor,
            });
}
