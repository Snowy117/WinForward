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

    /// <summary>
    /// The floor for the derived UDP sweep cadence: half of the shortest accepted idle timeout
    /// (5 s) would sweep every 2.5 s, which is faster than retention accuracy requires.
    /// </summary>
    private static readonly TimeSpan s_minimumUdpSweepInterval = TimeSpan.FromSeconds(5);

    private readonly FlowDispatcher _dispatcher;
    private readonly TcpProxyCoordinator? _tcp;
    private readonly UdpProxyCoordinator? _udp;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _udpSweepInterval;
    private readonly TimeSpan _flowIdleTimeout;
    private readonly TimeSpan _redirectIdleTimeout;
    private readonly TimeSpan _relayIdleTimeout;
    private readonly QuiescenceScope _scope = new();

    // Cached per-tick hold predicate: an instance-method-group conversion in the tick would build a new
    // Func<FlowKey,bool> on every main-leg sweep.
    private readonly Func<FlowKey, bool>? _holdsFlow;
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
        TimeProvider? timeProvider = null,
        TimeSpan? udpSweepInterval = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _tcp = tcp;
        _udp = udp;
        _interval = interval ?? TimeSpan.FromMinutes(1);
        _flowIdleTimeout = flowIdleTimeout ?? TimeSpan.FromMinutes(5);
        _redirectIdleTimeout = redirectIdleTimeout ?? TimeSpan.FromMinutes(5);
        _relayIdleTimeout = relayIdleTimeout ?? TimeSpan.FromMinutes(2);
        _udpSweepInterval = DeriveUdpSweepInterval(_interval, _relayIdleTimeout, udpSweepInterval);
        _holdsFlow = tcp is null ? null : tcp.HoldsFlow;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The tick period that serves both leg cadences: the UDP leg needs an idle timeout half-life
    /// (at least <see cref="s_minimumUdpSweepInterval"/>, so retention follows the active flow set
    /// instead of a minutes-long tail), while the expensive TCP-session and flow-table legs keep
    /// <paramref name="mainInterval"/> and are gated on the injected clock inside the tick. The
    /// override is the test seam for driving the UDP cadence without waiting for a real timeout.
    /// </summary>
    internal static TimeSpan DeriveUdpSweepInterval(TimeSpan mainInterval, TimeSpan relayIdleTimeout, TimeSpan? udpSweepInterval)
    {
        if (mainInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(mainInterval), mainInterval, "The main sweep interval must be positive.");
        var requested = udpSweepInterval ?? TimeSpan.FromTicks(Math.Max(s_minimumUdpSweepInterval.Ticks, relayIdleTimeout.Ticks / 2));
        if (requested <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(udpSweepInterval), requested, "The UDP sweep interval must be positive.");
        return requested < mainInterval ? requested : mainInterval;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The idle-expiry sweeper is already started.");
        ObjectDisposedException.ThrowIf(!_scope.Run(RunAsync, "idle-expiry.loop"), this);
    }

    private async Task RunAsync(CancellationToken token)
    {
        // The first main-leg sweep happens one full main interval after start, exactly as the
        // historical PeriodicTimer(_interval) cadence did; only the UDP leg rides every tick.
        var lastMainSweepUtc = _timeProvider.GetUtcNow();
        using var timer = new PeriodicTimer(_udpSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var now = _timeProvider.GetUtcNow();
                var tcpCount = 0;
                var flowCount = 0;
                var udpCount = 0;
                // The main-leg group (TCP redirects, then the flow table) and the UDP leg each own a
                // try/catch: a failure in one group is surfaced (rate-limited) without skipping the
                // other group's tick. Both groups report through the same failure logger.
                try
                {
                    if (now - lastMainSweepUtc >= _interval)
                    {
                        // Stamped before the legs run: a failing main sweep must not shorten their
                        // cadence (the UDP leg's failures are independent of this gate).
                        lastMainSweepUtc = now;
                        (tcpCount, flowCount) = await SweepMainLegsAsync(now).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // A sweep failure must not stop the capture loop; the next tick retries. It is
                    // still surfaced (rate-limited) so a persistently failing leg is diagnosable (S6d).
                    LogSweepFailureRateLimited(exception);
                }

                try
                {
                    udpCount = await SweepUdpLegAsync(now).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    LogSweepFailureRateLimited(exception);
                }

                LogExpired(tcpCount, flowCount, udpCount);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
    }

    /// <summary>
    /// The gated main-leg group: TCP redirects sweep before the flow table (expiring a half-open
    /// session releases its hold before the flow sweep runs, so a dead session's flow decision
    /// expires on its own idle instead of being held a round longer by a session that no longer
    /// exists), and the capacity summary rides the TCP leg so it keeps its historical cadence
    /// rather than following the faster UDP tick. Flows held by a live relaying session or a grace
    /// tombstone are skipped by the predicate and keep their original idle point.
    /// </summary>
    private async Task<(int TcpCount, int FlowCount)> SweepMainLegsAsync(DateTimeOffset now)
    {
        var tcpCount = _tcp is null ? 0 : await _tcp.RemoveExpiredAsync(now, _redirectIdleTimeout).ConfigureAwait(false);
        var flowCount = _dispatcher.RemoveExpiredFlows(now, _flowIdleTimeout, _holdsFlow);
        _tcp?.LogCapacitySummary();
        return (tcpCount, flowCount);
    }

    /// <summary>The UDP leg: it rides every tick on the fast cadence, independent of the main-leg gate.</summary>
    private async Task<int> SweepUdpLegAsync(DateTimeOffset now) =>
        _udp is null ? 0 : await _udp.RemoveExpiredAsync(now, _relayIdleTimeout).ConfigureAwait(false);

    /// <summary>The per-tick <c>runtime.expired</c> aggregate, emitted only when a leg expired something.</summary>
    private void LogExpired(int tcpCount, int flowCount, int udpCount)
    {
        if (!_logger.IsEnabled(Configuration.RuntimeLogLevel.Debug) || (flowCount == 0 && tcpCount == 0 && udpCount == 0)) return;
        _logger.Event(Configuration.RuntimeLogLevel.Debug, "runtime.expired",
            new("flows", flowCount), new("tcpRedirects", tcpCount), new("udpSessions", udpCount));
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
