using WinForward.Core;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Runtime;

/// <summary>
/// Runs the periodic idle-expiry sweep for the bounded flow/association tables (design §7/§8).
/// The added tables have <c>RemoveExpired</c> implementations but nothing invoked them; this
/// component is the single wiring point so stale one-shot flows and idle redirect/relay sessions
/// are released instead of accumulating to the bounded capacities. Sweep failures are isolated so
/// a transient teardown error cannot stop the capture loop.
/// </summary>
public sealed class IdleExpirySweeper : IAsyncDisposable
{
    private static readonly TimeSpan s_sweepFailureLogInterval = TimeSpan.FromSeconds(5);

    private readonly FlowDispatcher _dispatcher;
    private readonly TcpProxyCoordinator? _tcp;
    private readonly UdpProxyCoordinator? _udp;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _flowIdleTimeout;
    private readonly TimeSpan _redirectIdleTimeout;
    private readonly TimeSpan _relayIdleTimeout;
    private readonly QuiescenceScope _scope = new();
    private readonly IRuntimeLogger _logger;
    private readonly TimeProvider _timeProvider;
    private long _lastSweepFailureLogTicks;
    private int _started;

    public IdleExpirySweeper(
        FlowDispatcher dispatcher,
        TcpProxyCoordinator? tcp,
        UdpProxyCoordinator? udp,
        TimeSpan? interval = null,
        TimeSpan? flowIdleTimeout = null,
        TimeSpan? redirectIdleTimeout = null,
        TimeSpan? relayIdleTimeout = null,
        IRuntimeLogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _tcp = tcp;
        _udp = udp;
        _interval = interval ?? TimeSpan.FromMinutes(1);
        _flowIdleTimeout = flowIdleTimeout ?? TimeSpan.FromMinutes(5);
        _redirectIdleTimeout = redirectIdleTimeout ?? TimeSpan.FromMinutes(5);
        _relayIdleTimeout = relayIdleTimeout ?? TimeSpan.FromMinutes(2);
        _logger = logger ?? NullRuntimeLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The idle-expiry sweeper is already started.");
        ObjectDisposedException.ThrowIf(!_scope.Run(RunAsync, "idle-expiry.loop"), this);
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var now = _timeProvider.GetUtcNow();
                try
                {
                    // TCP sweeps before flows: expiring a half-open session here releases its hold
                    // (session first, then its grace tombstone) before the flow sweep runs, so a
                    // dead session's flow decision expires on its own idle instead of being held a
                    // round longer by a session that no longer exists. Flows held by a live
                    // relaying session or a grace tombstone are skipped by the predicate and keep
                    // their original idle point.
                    var tcpCount = _tcp is null ? 0 : await _tcp.RemoveExpiredAsync(now, _redirectIdleTimeout).ConfigureAwait(false);
                    Func<FlowKey, bool>? isHeld = _tcp is null ? null : _tcp.HoldsFlow;
                    var flowCount = _dispatcher.RemoveExpiredFlows(now, _flowIdleTimeout, isHeld);
                    var udpCount = _udp is null ? 0 : await _udp.RemoveExpiredAsync(now, _relayIdleTimeout).ConfigureAwait(false);
                    // Rides the existing sweep tick so the capacity summary needs no dedicated timer.
                    _tcp?.LogCapacitySummary();
                    if (_logger.IsEnabled(Configuration.RuntimeLogLevel.Debug) && (flowCount != 0 || tcpCount != 0 || udpCount != 0))
                    {
                        _logger.Event(Configuration.RuntimeLogLevel.Debug, "runtime.expired",
                            new("flows", flowCount), new("tcpRedirects", tcpCount), new("udpSessions", udpCount));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // A sweep failure must not stop the capture loop; the next tick retries. It is
                    // still surfaced (rate-limited) so a persistently failing sweep is diagnosable (S6d).
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
        var now = _timeProvider.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastSweepFailureLogTicks);
        if (now - last < s_sweepFailureLogInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastSweepFailureLogTicks, now, last) != last) return;
        _logger.Warn($"Idle-expiry sweep failed and will retry on the next tick: {exception.GetType().Name}: {exception.Message}");
    }

    // The drain is the whole teardown for this owner — seal, cancel (unwinding the timer wait),
    // join the in-flight tick, release the owned CTS last — and is itself single-flight, so a
    // second DisposeAsync joins it instead of re-running teardown.
    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
