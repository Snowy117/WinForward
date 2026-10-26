using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.UdpProxy;
using WinForward.Userland.Relay;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// The Kernel host UDP data path (scaffolding while the host UDP proxy is disabled): one receive leg
/// over the listener's dual-mode socket, the shared <see cref="UdpProxyCoordinator"/> carrying each
/// flow through its SOCKS5 association, and the activity/idle cadence the packet path would otherwise
/// supply from its capture pump.
/// </summary>
/// <remarks>
/// <para>
/// Not on the product path: the orchestrator constructs the service and its coordinator, but the
/// receive leg starts only once the host UDP proxy is enabled. The blocker is the reply source — a
/// reply sent from the receiving listener socket exposes the listener endpoint instead of the flow's
/// original destination — so the driver-side reply injection must land first. The entry path (cmsg
/// context validation, client-endpoint normalization, flow/target resolution and the coordinator
/// wiring) is the reusable part that leg restores. See the host UDP section of the Kernel WFP
/// architecture spec.
/// </para>
/// <para>
/// Every datagram is a full description of its flow: the client endpoint comes from the receive call,
/// and the original destination, rule index and client process id come from the WFP redirect context
/// the driver attached to the association. <see cref="KernelHostUdpDatagram"/> performs the validation
/// and builds the adapter-less host <see cref="FlowKey"/>; this service counts, admits and contains.
/// </para>
/// <para>
/// The datagram is contained per send. A false admission (capacity, setup ring, cooldown, cooldown
/// expiry) is a counted drop, and a genuine transport exception has already removed the flow's slot and
/// would otherwise propagate into the receive thread; neither may end the receive leg or tear down
/// another flow. The coordinator keeps its own capacity/setup counters, so this service counts only the
/// kernel-level datagram and pre-admission drop counters and never re-counts the shared engine's.
/// </para>
/// <para>
/// The coordinator's activity stamps and idle cutoffs are served from a shared
/// <see cref="ActivityBucketClock"/> that the packet path advances once per pump iteration. There is no
/// pump here, so a timer advances it at the bucket width and runs the two-tier idle sweep at the
/// interval <see cref="IdleExpirySweeper.DeriveUdpSweepInterval"/> derives for the configured retention.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class KernelHostUdpService : IAsyncDisposable
{
    /// <summary>Datagrams handed to the coordinator (one per admitted send).</summary>
    private const string AcceptedCounter = "kernel.udp.accepted";

    /// <summary>Datagrams dropped before or instead of a coordinator admission.</summary>
    private const string DroppedCounter = "kernel.udp.dropped";

    /// <summary>The packet path's activity-bucket cadence, reproduced here because there is no pump.</summary>
    private static readonly TimeSpan s_activityTickInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan s_sweepFailureLogInterval = TimeSpan.FromSeconds(5);

    private readonly ProxyTarget?[] _targetsByRuleIndex;
    private readonly ValidatedConfiguration _configuration;
    private readonly KernelHostUdpComposition _composition;
    private readonly UdpProxyCoordinator _coordinator;
    private readonly WfpUdpReceiver _receiver;
    private readonly ILogger _logger;
    private readonly QuiescenceScope _scope = new();
    private readonly RuntimeCounters _counters = RuntimeCounters.Shared;
    private readonly RuntimeLogThrottle _dropLog = new(s_sweepFailureLogInterval);
    private long _lastSweepFailureLogTicks;
    private int _started;
    private int _receiverStarted;
    private int _disposeStarted;

    /// <summary>
    /// Builds the receiver and the shared coordinator over the composition. The receiver resolves its
    /// function pointer here so a platform that cannot receive control messages fails startup instead
    /// of the first datagram.
    /// </summary>
    internal KernelHostUdpService(
        Socket udpSocket,
        ProxyTarget?[] targetsByRuleIndex,
        ValidatedConfiguration configuration,
        KernelHostUdpComposition composition,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(udpSocket);
        ArgumentNullException.ThrowIfNull(targetsByRuleIndex);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _targetsByRuleIndex = targetsByRuleIndex;
        _configuration = configuration;
        _composition = composition;
        _logger = logger;
        _receiver = new WfpUdpReceiver(udpSocket);
        try
        {
            _coordinator = UdpProxyComposer.Create(
                new HostUdpResponseSink(udpSocket, logger),
                composition.SelfTraffic,
                loggerFactory.CreateLogger<UdpProxyCoordinator>(),
                KernelHostUdpDatagram.MaximumDatagramBytes,
                composition.SetupQueuePool,
                composition.ReceiveWindowPool,
                composition.SetupExecutor,
                composition.AddressCache,
                configuration.UdpSessionCapacity,
                configuration.UdpRelayReceiveBufferBytes,
                composition.ActivityClock);
        }
        catch
        {
            _receiver.Dispose();
            throw;
        }
    }

    /// <summary>The coordinator's session budget, for the ready/stop lines.</summary>
    internal int SessionCapacity => _coordinator.Capacity;

    /// <summary>The live session count at the moment of the call.</summary>
    private int SessionCount => _coordinator.SessionCount;

    /// <summary>This service's own datagram counters and the live session count.</summary>
    internal KernelHostUdpSnapshot Snapshot()
        => new(_counters.Get(AcceptedCounter), _counters.Get(DroppedCounter), SessionCount);

    /// <summary>
    /// Starts the receive leg and the activity/sweep cadence. Both run while the driver is still
    /// disabled, so no association can be redirected to a listener that is not yet serving.
    /// </summary>
    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The Kernel host UDP service is already started.");
        _receiver.Start(OnDatagramAsync, _scope.Token);
        Volatile.Write(ref _receiverStarted, 1);
        if (!_scope.Run(SweepLoopAsync, "kernel.udp.sweep"))
        {
            // The scope is already sealed, so the receive thread would otherwise wait out its poll
            // interval with nothing driving the sweep; cancel it before reporting the failure.
            _scope.Cancel();
            throw new InvalidOperationException("The Kernel host UDP scope is sealed; the service cannot start once its shutdown has begun.");
        }
    }

    private ValueTask OnDatagramAsync(WfpUdpDatagram datagram)
    {
        var payload = datagram.Payload;
        if (!KernelHostUdpDatagram.TryPlan(datagram.Source, datagram.Control.Span, payload.Length, _targetsByRuleIndex, out var plan, out var reason, out var detail))
        {
            Drop(reason, datagram.Source, detail);
            return ValueTask.CompletedTask;
        }

        ValueTask<bool> pending;
        try
        {
            // The span is consumed synchronously by the shared engine (a ready session's transport
            // encodes before any await, and the setup window copies into its own queue), so it never
            // crosses an await here.
            pending = _coordinator.TrySendSpanAsync(plan.Flow, plan.Target, payload.Span, MacAddress.Invalid, _scope.Token);
        }
        catch (OperationCanceledException) when (_scope.Token.IsCancellationRequested)
        {
            return ValueTask.CompletedTask;
        }
        catch (Exception exception)
        {
            CountSendFailure(plan.Flow, exception);
            return ValueTask.CompletedTask;
        }

        if (pending.IsCompletedSuccessfully)
        {
#pragma warning disable VSTHRD103, MA0042 // The ValueTask is already complete (checked above), so reading it cannot block; MA0042's await guidance does not apply to the guarded-inline fast path.
            var admitted = pending.GetAwaiter().GetResult();
#pragma warning restore VSTHRD103, MA0042
            CountAdmission(admitted, plan.Flow);
            return ValueTask.CompletedTask;
        }

        return SendTailAsync(pending, plan.Flow);
    }

    private async ValueTask SendTailAsync(ValueTask<bool> pending, FlowKey flow)
    {
        try
        {
            if (!await pending.ConfigureAwait(false))
            {
                DropRefused(flow, "the shared engine refused the admission");
                return;
            }

            _ = _counters.Increment(AcceptedCounter);
        }
        catch (OperationCanceledException) when (_scope.Token.IsCancellationRequested)
        {
            // The shutdown cancelled the send that was in flight; nothing to report.
        }
        catch (Exception exception)
        {
            CountSendFailure(flow, exception);
        }
    }

    private void CountAdmission(bool admitted, FlowKey flow)
    {
        if (admitted)
        {
            _ = _counters.Increment(AcceptedCounter);
            return;
        }

        DropRefused(flow, "the shared engine refused the admission");
    }

    /// <summary>
    /// Counts a send the shared engine failed. The coordinator has already removed the flow's slot with
    /// its own teardown classification and counted that reason; this path moves only the kernel-level
    /// datagram drop and the shared fail-closed drop the coordinator does not record for a transport
    /// exception.
    /// </summary>
    private void CountSendFailure(FlowKey flow, Exception exception)
    {
        _ = _counters.Increment(DroppedCounter);
        _ = _counters.Increment(RuntimeCounters.UdpFailClosedDrop);
        var localText = flow.Local.ToString();
        var remoteText = flow.Remote.ToString();
        var errorText = exception.GetType().Name;
        if (_dropLog.ShouldEmit()) KernelBackendLog.UdpSendFailed(_logger, localText, remoteText, errorText);
    }

    /// <summary>
    /// Counts a datagram the shared engine refused admission for. The coordinator already counted the
    /// capacity, setup-ring, cooldown or session-unavailable reason it applied, so this path moves only
    /// the kernel-level datagram counter and never the shared engine's.
    /// </summary>
    private void DropRefused(FlowKey flow, string detail)
    {
        _ = _counters.Increment(DroppedCounter);
        var clientText = flow.Local.ToString();
        if (_dropLog.ShouldEmit()) KernelBackendLog.UdpDropped(_logger, nameof(UdpDatagramDropReason.AdmissionRefused), clientText, detail);
    }

    private void Drop(UdpDatagramDropReason reason, IPEndPoint source, string detail)
    {
        _ = _counters.Increment(DroppedCounter);
        _ = _counters.Increment(RuntimeCounters.UdpFailClosedDrop);
        var reasonText = reason.ToString();
        var clientText = source.ToString();
        if (_dropLog.ShouldEmit()) KernelBackendLog.UdpDropped(_logger, reasonText, clientText, detail);
    }

    /// <summary>
    /// The activity clock and the two-tier idle sweep: the clock advances every bucket so session
    /// stamps and the expiry cutoff stay current, and the coordinator sweeps at the derived cadence.
    /// A sweep failure is contained here — it must not end the timer that keeps every other flow's
    /// retention honest.
    /// </summary>
    private async Task SweepLoopAsync(CancellationToken token)
    {
        var idleTimeout = _configuration.UdpSessionIdleTimeout;
        var sweepInterval = IdleExpirySweeper.DeriveUdpSweepInterval(
            TimeSpan.FromMinutes(1),
            IdleExpirySweeper.EffectiveUdpRetentionFloor(idleTimeout, UdpProxyCoordinator.OneShotIdleTimeout),
            udpSweepInterval: null);
        using var timer = new PeriodicTimer(s_activityTickInterval);
        var lastSweepUtc = DateTimeOffset.UtcNow;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                _composition.ActivityClock.Tick();
                var now = DateTimeOffset.UtcNow;
                if (now - lastSweepUtc < sweepInterval) continue;
                lastSweepUtc = now;
                try
                {
                    _ = await _coordinator.RemoveExpiredAsync(now, idleTimeout, UdpProxyCoordinator.OneShotIdleTimeout).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    LogSweepFailureRateLimited(exception);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
    }

    private void LogSweepFailureRateLimited(Exception exception)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var last = Interlocked.Read(ref _lastSweepFailureLogTicks);
        if (now - last < s_sweepFailureLogInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastSweepFailureLogTicks, now, last) != last) return;
        var error = exception.GetType().Name;
        KernelBackendLog.UdpSweepFailed(_logger, error, exception.Message);
    }

    /// <summary>
    /// Stops the receive leg and disposes the coordinator. The listener set has already closed the UDP
    /// socket, which is what unblocks the parked receive; this method joins that thread, then the
    /// activity timer, and only then disposes the sessions and their transports.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _scope.DrainAsync().ConfigureAwait(false);
            return;
        }

        _scope.Cancel();
        // Only a started receiver has a completion to await; one whose thread was never created has no
        // loop to join.
        if (Volatile.Read(ref _receiverStarted) != 0) await _receiver.Completion.ConfigureAwait(false);
        _receiver.Dispose();

        await _scope.DrainAsync().ConfigureAwait(false);
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>The host UDP path's counters and live session count, for the backend's stop line.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct KernelHostUdpSnapshot(long Accepted, long Dropped, int Sessions);
