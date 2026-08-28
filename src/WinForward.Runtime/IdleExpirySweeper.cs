using WinForward.Core;

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
    private readonly FlowDispatcher _dispatcher;
    private readonly TcpProxyCoordinator? _tcp;
    private readonly UdpProxyCoordinator? _udp;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _flowIdleTimeout;
    private readonly TimeSpan _redirectIdleTimeout;
    private readonly TimeSpan _relayIdleTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IRuntimeLogger _logger;
    private Task? _loop;

    public IdleExpirySweeper(
        FlowDispatcher dispatcher,
        TcpProxyCoordinator? tcp,
        UdpProxyCoordinator? udp,
        TimeSpan? interval = null,
        TimeSpan? flowIdleTimeout = null,
        TimeSpan? redirectIdleTimeout = null,
        TimeSpan? relayIdleTimeout = null,
        IRuntimeLogger? logger = null)
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
    }

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("The idle-expiry sweeper is already started.");
        _loop = RunAsync();
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                var now = DateTimeOffset.UtcNow;
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
                    if (_logger.IsEnabled(WinForward.Configuration.RuntimeLogLevel.Debug) && (flowCount != 0 || tcpCount != 0 || udpCount != 0))
                    {
                        _logger.Event(WinForward.Configuration.RuntimeLogLevel.Debug, "runtime.expired",
                            new("flows", flowCount), new("tcpRedirects", tcpCount), new("udpSessions", udpCount));
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable RCS1075 // A sweep failure must not stop the capture loop; the next tick retries.
                catch (Exception)
                {
                    // A sweep failure must not stop the capture loop; the next tick retries.
                }
#pragma warning restore RCS1075
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Cancellation is the expected shutdown path.
            }
        }
        _shutdown.Dispose();
    }
}
