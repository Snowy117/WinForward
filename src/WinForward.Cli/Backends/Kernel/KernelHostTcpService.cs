using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using WinForward.Userland.Device;
using WinForward.Userland.Forwarded;
using WinForward.Userland.Policy;
using WinForward.Userland.Relay;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// The Kernel host-TCP data path: the device publication that turns redirected connections into this
/// process's listener, the listener and block sink that receive them, and the accept loops that hand
/// each connection to a <see cref="KernelRelaySession"/>.
/// </summary>
/// <remarks>
/// <para>
/// The device calls follow the Kernel controller's order. Startup clears first, because a previous
/// run may have left the driver redirecting to a listener that is not up yet; it then binds,
/// publishes and verifies every readback before the driver is enabled, and only then starts
/// accepting. Shutdown reverses the visibility: the host redirect is disabled before the listener or
/// the block sink is released, so the driver can never aim a connection at a port this process has
/// given up.
/// </para>
/// <para>
/// The service borrows the device, the relay factory and the relay pool; <see cref="KernelBackend"/>
/// owns and releases them. Every accepted socket has exactly one owner: the session, until the relay
/// takes it over.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class KernelHostTcpService : IAsyncDisposable
{
    private const string AcceptedCounter = "kernel.tcp.accepted";
    private const string RefusedCounter = "kernel.tcp.refused";
    private const string CapacityBlockedCounter = "kernel.tcp.capacityBlocked";
    private const string ActiveCounter = "kernel.tcp.active";
    private const string EstablishedCounter = "kernel.relay.established";
    private const string SetupFailedCounter = "kernel.relay.setupFailed";
    private const string StalledCounter = "kernel.relay.stalled";
    private const string FaultedCounter = "kernel.relay.faulted";

    /// <summary>A short bounded back-off between retries of a transient accept error, never a busy loop.</summary>
    private static readonly TimeSpan s_acceptRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>The empty forwarded table this milestone publishes to clear a previous run's forwarded state.</summary>
    private static readonly byte[] s_emptyForwardedBlob =
        ForwardedBlobCompiler.Compile(new ForwardedPolicyTable([], RuleAction.Permit));

    private readonly WfpDeviceClient _device;
    private readonly ValidatedConfiguration _configuration;
    private readonly TcpProxyRelayFactory _relayFactory;
    private readonly ILogger _logger;
    private readonly QuiescenceScope _scope = new();
    private readonly SemaphoreSlim _setupSlots;
    private readonly RuntimeCounters _counters = RuntimeCounters.Shared;
    private ProxyTarget?[] _targetsByRuleIndex = [];
    private ListenerSet? _listeners;
    private BlockSink? _blockSink;
    private int _activeSessions;
    private int _disposeStarted;
    private int _shutdownFaulted;

    internal KernelHostTcpService(
        WfpDeviceClient device,
        ValidatedConfiguration configuration,
        TcpProxyRelayFactory relayFactory,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(relayFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _device = device;
        _configuration = configuration;
        _relayFactory = relayFactory;
        _logger = logger;

        // SetupWorkerCount keeps 0 = auto, the bound the packet path's SetupExecutor resolves the same
        // way; the dial queues behind this gate instead of parking a thread per establishment.
        SetupWorkers = configuration.SetupWorkerCount > 0
                           ? configuration.SetupWorkerCount
                           : Math.Max(2 * Environment.ProcessorCount, 16);
        _setupSlots = new SemaphoreSlim(SetupWorkers, SetupWorkers);
    }

    /// <summary>The bound on relay establishments in flight at once.</summary>
    private int SetupWorkers { get; }

    /// <summary>The configured active-relay budget (<c>TcpFlowCapacity</c>).</summary>
    internal int FlowCapacity => _configuration.TcpFlowCapacity;

    /// <summary>Whether the shutdown could not disable the driver's host redirect.</summary>
    internal bool ShutdownFaulted => Volatile.Read(ref _shutdownFaulted) != 0;

    /// <summary>
    /// Publishes the policy and brings the data path up, in the one order that keeps the driver from
    /// ever redirecting to a port that is not listening. Returns <see langword="false"/> with the
    /// compiler's diagnostics when the host policy does not compile.
    /// </summary>
    internal bool TryStart(out IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        VerifyControlAbi();
        DisableStaleState();

        if (!KernelPolicyCompiler.TryCompile(_configuration, WindowsProcessPathNormalizer.ForWindows(), out var compilation, out diagnostics))
        {
            return false;
        }

        _targetsByRuleIndex = compilation!.TargetsByRuleIndex;
        var listeners = ListenerSet.Bind();
        _listeners = listeners;
        var tcpListeners = listeners.Listeners;
        _blockSink = compilation.ContainsBlock ? BlockSink.Bind(listeners.Port) : null;
        KernelBackendLog.ListenerBound(_logger, listeners.Port, tcpListeners.Count, _blockSink?.Port ?? 0, compilation.ContainsBlock);

        _device.SetRuleBlob(compilation.HostBlob);
        var info = VerifyRuleReadback(compilation);
        KernelBackendLog.RulesInstalled(_logger, info.Generation, compilation.RuleCount, compilation.FallbackAction, compilation.HostBlob.Length, compilation.ContainsBlock);

        var processId = (uint)Environment.ProcessId;
        var blockSinkPort = _blockSink?.Port ?? 0;
        _device.SetConfig(ProbeConfig.EnabledFor(processId, (ushort)listeners.Port, [processId], (uint)blockSinkPort));
        VerifyConfigReadback(processId, listeners.Port, blockSinkPort);

        StartAcceptLoops(tcpListeners);
        KernelBackendLog.Ready(_logger, ProbeIoctl.AbiVersion, listeners.Port, blockSinkPort, compilation.RuleCount, FlowCapacity, SetupWorkers);
        return true;
    }

    /// <summary>
    /// Resolves the target a redirect context's <c>rule_index</c> names. Every failure is fail-closed:
    /// the blob never produced the index (the fallback sentinel reads as -1), the rule is not a proxy
    /// rule, or it resolves to a local target, which needs the host UDP relay this backend does not
    /// serve. A default target would silently proxy traffic the policy passed or blocked.
    /// </summary>
    internal bool TryResolveTarget(int ruleIndex, out ProxyTarget target, out string error)
    {
        target = default;
        if (ruleIndex < 0 || ruleIndex >= _targetsByRuleIndex.Length)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"rule index {ruleIndex} is outside the {_targetsByRuleIndex.Length} compiled host rule(s), so the blob and the target table disagree");
            return false;
        }

        if (_targetsByRuleIndex[ruleIndex] is not { } resolved)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"rule {ruleIndex} is not a proxy rule");
            return false;
        }

        if (resolved.Socks5 is null)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"rule {ruleIndex} resolves to local target '{resolved.Name}', whose host UDP relay this provider does not serve");
            return false;
        }

        target = resolved;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Claims one active-session slot. A full budget refuses the connection instead of queueing it:
    /// the client would otherwise wait for a relay that may never come, and the budget is what
    /// protects the ephemeral-port pool the relay's own upstream sockets come from.
    /// </summary>
    internal bool TryAdmitSession()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activeSessions);
            if (current >= _configuration.TcpFlowCapacity) return false;
            if (Interlocked.CompareExchange(ref _activeSessions, current + 1, current) != current) continue;
            _ = _counters.Increment(ActiveCounter);
            return true;
        }
    }

    /// <summary>Releases the slot claimed by <see cref="TryAdmitSession"/>; exactly one release per admit.</summary>
    internal void ReleaseSession()
    {
        _ = Interlocked.Decrement(ref _activeSessions);
        _counters.Add(ActiveCounter, -1);
    }

    /// <summary>Waits for one relay-setup slot, so a dial cannot starve the accept loop.</summary>
    internal Task EnterSetupSlotAsync(CancellationToken cancellationToken) => _setupSlots.WaitAsync(cancellationToken);

    /// <summary>Releases the slot claimed by <see cref="EnterSetupSlotAsync"/>.</summary>
    internal void ExitSetupSlot() => _setupSlots.Release();

    /// <summary>
    /// Establishes the relay for a connection this service accepted. Ownership of the socket moves to
    /// the relay on success; a failure leaves it with the caller, which closes it abortively.
    /// </summary>
    internal ValueTask<ITcpRelay> EstablishRelayAsync(RedirectDestination destination, Socket socket, Socks5Server server, CancellationToken cancellationToken) =>
        _relayFactory.EstablishAsync(Endpoint.From(destination.EndPoint.Address, checked((ushort)destination.EndPoint.Port)), socket, server, cancellationToken);

    internal void CountRefused()
    {
        _ = _counters.Increment(RefusedCounter);
    }

    internal void CountCapacityBlocked()
    {
        _ = _counters.Increment(CapacityBlockedCounter);
    }

    internal void CountRelayEstablished()
    {
        _ = _counters.Increment(EstablishedCounter);
    }

    internal void CountRelaySetupFailed()
    {
        _ = _counters.Increment(SetupFailedCounter);
    }

    /// <summary>Counts a relay end the client can see; a clean end is the expected outcome and is not counted.</summary>
    internal void CountRelayEnd(RelayEndKind endKind)
    {
        _ = endKind switch
        {
            RelayEndKind.Stalled => _counters.Increment(StalledCounter),
            RelayEndKind.Faulted => _counters.Increment(FaultedCounter),
            _ => 0,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            // A second caller joins the same drain instead of running the teardown again.
            await _scope.DrainAsync().ConfigureAwait(false);
            return;
        }

        KernelBackendLog.ShuttingDown(_logger);

        // Visibility first: with the host redirect disabled the driver cannot aim a connection at a
        // listener or a block sink this method is about to release. A refused call is recorded and
        // the release continues — a torn-down data path beats a released port the driver still uses.
        TryDeviceCall("disable the host redirect", () => _device.SetConfig(ProbeConfig.Disabled()));

        _scope.Cancel();
        _listeners?.Dispose();
        _listeners = null;
        // The sink may only be released after the driver is disabled: while it is enabled a block
        // decision is still steered at the sink port.
        _blockSink?.Dispose();
        _blockSink = null;

        // Cancels the sessions — a relay parked on its own completion is terminated by its disposal —
        // and joins the accept loops and every session task.
        await _scope.DrainAsync().ConfigureAwait(false);
        _setupSlots.Dispose();

        if (_scope.Fault is { } fault)
        {
            KernelBackendLog.SessionFailed(_logger, _scope.FaultSite ?? "kernel.session", fault);
        }

        TryDeviceCall("clear the forwarded state", ClearForwardedState);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var accepted = _counters.Get(AcceptedCounter);
            var refused = _counters.Get(RefusedCounter);
            var capacityBlocked = _counters.Get(CapacityBlockedCounter);
            var established = _counters.Get(EstablishedCounter);
            var stalled = _counters.Get(StalledCounter);
            var faulted = _counters.Get(FaultedCounter);
            var active = _counters.Get(ActiveCounter);
            KernelBackendLog.Stopped(_logger, accepted, refused, capacityBlocked, established, stalled, faulted, active);
        }
    }

    /// <summary>
    /// Enforces the driver's control ABI before any device state is touched. Only the stats carry
    /// the driver's own <c>abi_version</c> — the config readback echoes the caller's — and the
    /// driver's own rejection of a mismatched config arrives as a bare Win32 status.
    /// </summary>
    private void VerifyControlAbi()
    {
        var stats = _device.GetStats();
        if (stats.AbiVersion != ProbeIoctl.AbiVersion)
        {
            throw new InvalidOperationException(
                $"The WinForward.Kernel driver reports control ABI {stats.AbiVersion} but this backend implements ABI {ProbeIoctl.AbiVersion}; upgrade the driver and the backend together.");
        }
    }

    /// <summary>
    /// Clears whatever a previous run may have left behind, before anything else happens: no
    /// connection can be redirected while the host config is disabled, and the forwarded side is
    /// taken down with an empty permit table (this backend serves no forwarded traffic).
    /// </summary>
    private void DisableStaleState()
    {
        _device.SetConfig(ProbeConfig.Disabled());
        ClearForwardedState();
        KernelBackendLog.StaleStateCleared(_logger);
    }

    private void ClearForwardedState()
    {
        _device.SetForwardedConfig(ProbeForwardedConfig.Disabled());
        _device.SetForwardedBlob(s_emptyForwardedBlob);
    }

    /// <summary>
    /// Verifies the driver's readback of the published blob field by field: a driver that rejected or
    /// truncated the table must fail startup instead of redirecting under a policy nobody published.
    /// </summary>
    private ProbeRuleInfo VerifyRuleReadback(KernelPolicyCompilation compilation)
    {
        var info = _device.GetRuleInfo();
        if (info.Installed == 1 &&
            info.RuleCount == (uint)compilation.RuleCount &&
            info.FallbackAction == (uint)compilation.FallbackAction &&
            info.TotalSize == (uint)compilation.HostBlob.Length)
        {
            return info;
        }

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"The driver reports installed={info.Installed}, {info.RuleCount} rule(s), fallback {info.FallbackAction} and {info.TotalSize} byte(s) after the Kernel host rule blob was published; the backend published {compilation.RuleCount} rule(s), fallback {(uint)compilation.FallbackAction} and {compilation.HostBlob.Length} byte(s)."));
    }

    private void VerifyConfigReadback(uint processId, int listenerPort, int blockSinkPort)
    {
        var config = _device.GetConfig();
        // ReSharper disable once MergeIntoPattern // The readback check compares the driver's seven reported fields one by one; the pattern form can only carry the three constant comparisons (the parameter and inline-array-element comparisons are rejected by the pattern syntax), and a partial merge would split one verification into two shapes.
        if (config.AbiVersion == ProbeIoctl.AbiVersion &&
            config.Enabled == 1 &&
            config.ListenerPort == listenerPort &&
            config.ListenerProcessId == processId &&
            config.SelfProcessCount == 1 &&
            config.SelfProcessIds[0] == processId &&
            config.BlockSinkPort == (uint)blockSinkPort)
        {
            return;
        }

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"The driver reports enabled={config.Enabled}, listener port {config.ListenerPort} for process {config.ListenerProcessId}, {config.SelfProcessCount} self process(es) and block sink port {config.BlockSinkPort} after the Kernel config was published; the backend published ABI {ProbeIoctl.AbiVersion}, listener port {listenerPort} for process {processId}, one self process and block sink port {blockSinkPort}."));
    }

    private void StartAcceptLoops(IReadOnlyList<TcpListener> tcpListeners)
    {
        foreach (var listener in tcpListeners)
        {
            // One loop per listener; the scope joins each loop and every session it admits, so the
            // shutdown drain is the data path's quiescence point.
            StartAcceptLoop(listener);
        }
    }

    private void StartAcceptLoop(TcpListener listener)
    {
        if (_scope.Run(token => AcceptLoopAsync(listener, token), "kernel.accept")) return;

        throw new InvalidOperationException("The Kernel accept scope is sealed; the service cannot start once its shutdown has begun.");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await listener.AcceptSocketAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // Shutdown released the listener while this loop was waiting for a connection.
                return;
            }
            catch (Exception exception)
            {
                KernelBackendLog.AcceptFailed(_logger, exception.GetType().Name);
                await BoundedRetryDelayAsync(token).ConfigureAwait(false);
                continue;
            }

            _ = _counters.Increment(AcceptedCounter);
            var session = new KernelRelaySession(accepted, this, _logger);
            if (_scope.Run(session.RunAsync, "kernel.session")) continue;

            // The scope was sealed between the accept and the admission, so the session body never
            // runs and the socket would have no owner: close it here instead.
            KernelBackendLog.SessionRefused(_logger, "shutdown", accepted.RemoteEndPoint);
            session.CloseAcceptedConnection();
        }
    }

    private static async Task BoundedRetryDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(s_acceptRetryDelay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown during the back-off: the loop's next iteration observes the token and returns.
        }
    }

    private void TryDeviceCall(string operation, Action call)
    {
        try
        {
            call();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // The teardown must reach the listener and session release even when the driver refuses a
            // call, so the fault is recorded rather than thrown; the run reports exit code 1 from it.
            Volatile.Write(ref _shutdownFaulted, 1);
            KernelBackendLog.DeviceFailed(_logger, operation, exception.GetType().Name, exception.Message);
        }
    }
}
