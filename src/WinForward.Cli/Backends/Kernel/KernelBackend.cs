using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.TcpRedirect;
using WinForward.Userland.Device;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// The WinForward.Kernel backend: it owns the WFP device handle, the relay factory, the relay pool and
/// the host-TCP service, and it concludes the exit code. All of them live for the whole
/// <see cref="RunAsync"/> call and are released in one teardown inside it; nothing outlives it.
/// </summary>
/// <remarks>
/// A device or ABI fault is caught here and reported as a Kernel backend error, because escaping to
/// the CLI would dress a WFP device failure up as an NDISAPI one: the CLI's catch set exists for the
/// packet backend's native library and says so.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class KernelBackend(ValidatedConfiguration configuration, ILoggerFactory loggerFactory, ILogger logger) : IInterceptionBackend
{
    private const string RelayPoolName = "kernel.tcp.relay";

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
        // The pool is owned here and released after the service has drained its sessions; the service
        // and its relays only borrow it.
        var relayPool = new NativeBufferPool(TcpProxyRelayFactory.PumpBufferSize);
        RegisterRelayPool(relayPool);
        try
        {
            var relayFactory = new TcpProxyRelayFactory(
                new SelfTrafficRegistry(),
                loggerFactory.CreateLogger<TcpProxyRelayFactory>(),
                relayPool,
                // The driver's self-PID gate is what keeps the relay's own upstream traffic out of the
                // redirect; the factory's registry only answers for the control tuple it dials.
                new Socks5AddressCache());
            await using var service = new KernelHostTcpService(device, configuration, relayFactory, loggerFactory.CreateLogger<KernelHostTcpService>());
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
                // ReSharper disable once DisposeOnUsingVariable // The explicit call is the shutdown whose result the exit code is concluded from and whose faults the catch set below maps to the Kernel error surface; the await using declaration only backstops the paths where a catch has already returned, and KernelHostTcpService.DisposeAsync is single-flight so the second call joins the completed drain.
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
        finally
        {
            relayPool.Dispose();
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
    /// Registers the relay pool with the runtime counters so the heartbeat reports its occupancy; the
    /// sink allocates nothing per rent/return and never touches packet disposition.
    /// </summary>
    private static void RegisterRelayPool(NativeBufferPool pool)
    {
        var counters = RuntimeCounters.Shared;
        counters.RegisterPool(RelayPoolName);
        var rentKey = RuntimeCounters.PoolRentedKey(RelayPoolName);
        var returnKey = RuntimeCounters.PoolReturnedKey(RelayPoolName);
        pool.AccountingSink = rented => counters.Increment(rented ? rentKey : returnKey);
    }
}
