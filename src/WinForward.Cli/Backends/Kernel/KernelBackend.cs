using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;
using WinForward.Userland.Device;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// The WinForward.Kernel backend: it owns the WFP device handle, the shared self-traffic registry, the
/// SOCKS5 address cache, the setup executor, the UDP pools and clock, the relay factory, the relay pool
/// and the host service, and it concludes the exit code. All of them live for the whole
/// <see cref="RunAsync"/> call and are released in one teardown inside it; nothing outlives it.
/// </summary>
/// <remarks>
/// The host service borrows every one of those collaborators, so they are created here, disposed here
/// in reverse creation order, and never handed to anything that outlives this method. A device or ABI
/// fault is caught here and reported as a Kernel backend error, because escaping to the CLI would dress
/// a WFP device failure up as an NDISAPI one: the CLI's catch set exists for the packet backend's native
/// library and says so.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class KernelBackend(ValidatedConfiguration configuration, ILoggerFactory loggerFactory, ILogger logger) : IInterceptionBackend
{
    private const string RelayPoolName = "kernel.tcp.relay";
    private const string UdpDatagramPoolName = "kernel.udp.setupQueue";
    private const string UdpWindowPoolName = "kernel.udp.receiveWindow";

    public string ProviderName => BackendDriverConfiguration.KernelProviderName;

    public async ValueTask<int> RunAsync(CancellationToken cancellationToken)
    {
        WfpDeviceClient device;
        try
        {
            device = WfpDeviceClient.Open();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            KernelBackendLog.DeviceFailed(logger, "open the WFP device", exception.GetType().Name, exception.Message);
            return 1;
        }

        using (device)
        {
            return await RunComposedAsync(device, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// No-op, deliberately: <see cref="RunAsync"/> owns every composed resource for its own duration,
    /// so nothing survives the call to be released here.
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Composes the data path and concludes the exit code: 1 for a device, ABI or configuration fault,
    /// 3 for a runtime one, and 0 for a requested shutdown whose driver was left disabled.
    /// </summary>
    private async ValueTask<int> RunComposedAsync(WfpDeviceClient device, CancellationToken cancellationToken)
    {
        var counters = RuntimeCounters.Shared;

        // Every pool is owned here and released after the host service has drained, because the service
        // and its coordinator only borrow them.
        var relayPool = new NativeBufferPool(TcpProxyRelayFactory.PumpBufferSize);
        RegisterPool(counters, RelayPoolName, relayPool);
        var selfTraffic = new SelfTrafficRegistry();
        var addressCache = new Socks5AddressCache();
        using var setupExecutor = new SetupExecutor(configuration.SetupWorkerCount == 0 ? null : configuration.SetupWorkerCount);

        // The host datagram cap is one constant: the transport send buffer, the setup-queue lease, the
        // session receive window and the pre-admission length check all derive from it.
        // TODO: these UDP pools, the activity clock and the host composition feed a coordinator the
        // orchestrator constructs but does not start until the driver can inject the relay reply with
        // the original destination as its source. See the Host UDP section of
        // .trellis/spec/backend/kernel/wfp-architecture.md
        const int udpDatagramCap = KernelHostUdpDatagram.MaximumDatagramBytes;
        var udpSetupQueuePool = new NativeBufferPool(udpDatagramCap);
        RegisterPool(counters, UdpDatagramPoolName, udpSetupQueuePool);
        var udpWindowPool = new NativeBufferPool(
            UdpProxyCoordinator.ReceiveWindowSize(udpDatagramCap),
            UdpProxyCoordinator.ReceiveWindowPoolCapacity(configuration.UdpSessionCapacity));
        RegisterPool(counters, UdpWindowPoolName, udpWindowPool);
        var activityClock = new ActivityBucketClock();
        try
        {
            var relayFactory = new TcpProxyRelayFactory(
                selfTraffic,
                loggerFactory.CreateLogger<TcpProxyRelayFactory>(),
                relayPool,
                // The driver's self-PID gate is what keeps the relay's own upstream traffic out of the
                // redirect; the factory's registry only answers for the control tuple it dials.
                addressCache);

            // Resolve every SOCKS5 host once, before the first association exists: a UDP flow's first
            // datagram is the one place a dial cannot afford a synchronous DNS wait.
            await UdpProxyComposer.PrimeSocks5AddressCacheAsync(configuration, addressCache, loggerFactory.CreateLogger(typeof(Program).FullName!)).ConfigureAwait(false);

            var composition = new KernelHostUdpComposition(udpSetupQueuePool, udpWindowPool, setupExecutor, addressCache, selfTraffic, activityClock);
            return await RunHostAsync(device, relayFactory, composition, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Reverse creation order: the UDP pools first (the service's sessions are already gone), then
            // the relay pool, and the setup executor last because a drained coordinator released its
            // workers through it.
            udpWindowPool.Dispose();
            udpSetupQueuePool.Dispose();
            relayPool.Dispose();
        }
    }

    /// <summary>
    /// Runs the host service for one process lifetime and maps its outcome to the exit code: 1 for a
    /// policy or device fault, 3 for a runtime one, 0 for a requested shutdown whose driver was left
    /// disabled.
    /// </summary>
    private async ValueTask<int> RunHostAsync(
        WfpDeviceClient device,
        TcpProxyRelayFactory relayFactory,
        KernelHostUdpComposition composition,
        CancellationToken cancellationToken)
    {
        await using var service = new KernelHostService(device, configuration, relayFactory, composition, loggerFactory, logger);
        try
        {
            if (!service.TryStart(out var diagnostics))
            {
                foreach (var diagnostic in diagnostics)
                {
                    KernelBackendLog.PolicyCompilationFailed(logger, diagnostic.Path, diagnostic.Message);
                }

                return 1;
            }

            KernelBackendLog.Started(logger);
            await WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);

            // The shutdown order runs here, before the exit code is concluded, so a driver the
            // backend could not disable is reported instead of a clean exit.
            // ReSharper disable once DisposeOnUsingVariable // The explicit call is the shutdown whose result the exit code is concluded from and whose faults the catch set below maps to the Kernel error surface; the await using declaration only backstops the paths where a catch has already returned, and KernelHostService.DisposeAsync is single-flight so the second call joins the completed drain.
            await service.DisposeAsync().ConfigureAwait(false);
            return service.ShutdownFaulted ? 1 : 0;
        }
        catch (SocketException exception)
        {
            KernelBackendLog.RuntimeFailed(logger, exception);
            return 3;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            KernelBackendLog.DeviceFailed(logger, "start the Kernel backend", exception.GetType().Name, exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            KernelBackendLog.RuntimeFailed(logger, exception);
            return 3;
        }
    }

    private static async Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The shutdown request is the completion condition; the service's teardown concludes the run.
        }
    }

    /// <summary>
    /// Registers a native pool with the runtime counters so the heartbeat reports its occupancy; the
    /// sink allocates nothing per rent/return and never touches packet disposition.
    /// </summary>
    private static void RegisterPool(RuntimeCounters counters, string name, NativeBufferPool pool)
    {
        counters.RegisterPool(name);
        var rentKey = RuntimeCounters.PoolRentedKey(name);
        var returnKey = RuntimeCounters.PoolReturnedKey(name);
        pool.AccountingSink = rented => counters.Increment(rented ? rentKey : returnKey);
    }
}
