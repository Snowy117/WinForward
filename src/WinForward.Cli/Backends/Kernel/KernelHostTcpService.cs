using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using WinForward.Userland.Relay;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// The Kernel host-TCP data path: the accept loops over the orchestrator's bound listeners and the
/// per-connection handing to a <see cref="KernelRelaySession"/>.
/// </summary>
/// <remarks>
/// <para>
/// The service borrows the listeners, the device-owning orchestrator's shutdown token, the relay
/// factory and the relay pool; <see cref="KernelHostService"/> binds and publishes the driver config
/// and <see cref="KernelBackend"/> owns the factory and the pool. Every accepted socket has exactly one
/// owner: the session, until the relay takes it over.
/// </para>
/// <para>
/// The accept loops start only after the orchestrator has published and verified the host rule blob
/// and the enabled driver config, so the driver never redirects to a port that is not accepting yet.
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

    private readonly IReadOnlyList<TcpListener> _tcpListeners;
    private readonly ProxyTarget?[] _targetsByRuleIndex;
    private readonly ValidatedConfiguration _configuration;
    private readonly TcpProxyRelayFactory _relayFactory;
    private readonly ILogger _logger;
    private readonly QuiescenceScope _scope;
    private readonly SemaphoreSlim _setupSlots;
    private readonly RuntimeCounters _counters = RuntimeCounters.Shared;
    private int _activeSessions;
    private int _disposeStarted;

    internal KernelHostTcpService(
        IReadOnlyList<TcpListener> tcpListeners,
        ProxyTarget?[] targetsByRuleIndex,
        ValidatedConfiguration configuration,
        TcpProxyRelayFactory relayFactory,
        ILogger logger,
        CancellationToken hostToken)
    {
        ArgumentNullException.ThrowIfNull(tcpListeners);
        ArgumentNullException.ThrowIfNull(targetsByRuleIndex);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(relayFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _tcpListeners = tcpListeners;
        _targetsByRuleIndex = targetsByRuleIndex;
        _configuration = configuration;
        _relayFactory = relayFactory;

        // The orchestrator owns the shutdown decision; this scope nests under it so its own drain also
        // cancels the accept loops when this service is disposed on its own.
        _scope = new QuiescenceScope(hostToken);
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

    /// <summary>This service's TCP counters, for the backend's stop line.</summary>
    internal KernelHostTcpSnapshot Snapshot() => new(
        _counters.Get(AcceptedCounter),
        _counters.Get(RefusedCounter),
        _counters.Get(CapacityBlockedCounter),
        _counters.Get(EstablishedCounter),
        _counters.Get(StalledCounter),
        _counters.Get(FaultedCounter),
        _counters.Get(ActiveCounter));

    /// <summary>Starts one accept loop per bound listener; the scope joins each loop and every session it admits.</summary>
    internal void StartAcceptLoops()
    {
        foreach (var listener in _tcpListeners)
        {
            if (_scope.Run(token => AcceptLoopAsync(listener, token), "kernel.accept")) continue;

            throw new InvalidOperationException("The Kernel accept scope is sealed; the service cannot start once its shutdown has begun.");
        }
    }

    /// <summary>
    /// Resolves the target a redirect context's <c>rule_index</c> names through the compiled table. See
    /// <see cref="KernelHostTargetResolver"/> for why every failure is fail-closed.
    /// </summary>
    internal bool TryResolveTarget(int ruleIndex, out ProxyTarget target, out string error)
        => KernelHostTargetResolver.TryResolve(_targetsByRuleIndex, ruleIndex, out target, out error);

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

    /// <summary>
    /// Joins the accept loops and every active session. The orchestrator has already cancelled the host
    /// token and stopped the listeners, so each loop's pending accept has returned; the sessions drain
    /// as their relays are disposed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            // A second caller joins the same drain instead of running the teardown again.
            await _scope.DrainAsync().ConfigureAwait(false);
            return;
        }

        await _scope.DrainAsync().ConfigureAwait(false);
        _setupSlots.Dispose();

        if (_scope.Fault is { } fault)
        {
            KernelBackendLog.SessionFailed(_logger, _scope.FaultSite ?? "kernel.session", fault);
        }
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
}

/// <summary>The host TCP path's counters, for the backend's stop line.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct KernelHostTcpSnapshot(
    long Accepted,
    long Refused,
    long CapacityBlocked,
    long Established,
    long Stalled,
    long Faulted,
    long Active);
