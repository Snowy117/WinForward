using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinForward.Core;
using WinForward.Runtime.Logging;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Runtime;

/// <summary>
/// Runs the periodic idle-expiry sweep for the bounded flow/association tables. The tables have
/// <c>RemoveExpired</c> implementations but nothing else invokes them; this component is the single
/// wiring point so stale one-shot flows and idle redirect/relay sessions are released instead of
/// accumulating to the bounded capacities. Sweep failures are isolated so a transient teardown error
/// cannot stop the capture loop.
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
    private readonly Func<DateTimeOffset, int>? _attributionSweep;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _udpSweepInterval;
    private readonly TimeSpan _flowIdleTimeout;
    private readonly TimeSpan _redirectIdleTimeout;
    private readonly TimeSpan _relayIdleTimeout;
    private readonly TimeSpan? _udpOneShotIdleTimeout;
    private readonly QuiescenceScope _scope = new();

    // Cached per-tick hold predicate: an instance-method-group conversion in the tick would build a new
    // Func<FlowKey,bool> on every main-leg sweep.
    private readonly Func<FlowKey, bool>? _holdsFlow;
    private readonly ILogger _logger;
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
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? udpSweepInterval = null,
        Func<DateTimeOffset, int>? attributionSweep = null,
        TimeSpan? udpOneShotIdleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _tcp = tcp;
        _udp = udp;
        _attributionSweep = attributionSweep;
        _interval = interval ?? TimeSpan.FromMinutes(1);
        _flowIdleTimeout = flowIdleTimeout ?? TimeSpan.FromMinutes(5);
        _redirectIdleTimeout = redirectIdleTimeout ?? TimeSpan.FromMinutes(5);
        _relayIdleTimeout = relayIdleTimeout ?? TimeSpan.FromMinutes(2);
        _udpOneShotIdleTimeout = udpOneShotIdleTimeout;
        _udpSweepInterval = DeriveUdpSweepInterval(_interval, EffectiveUdpRetentionFloor(_relayIdleTimeout, udpOneShotIdleTimeout), udpSweepInterval);
        _holdsFlow = tcp is null ? null : tcp.HoldsFlow;
        _logger = logger ?? NullLogger.Instance;
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

    /// <summary>
    /// The retention the UDP tick is derived from: the shorter of the configured retention and the
    /// one-shot class, so a short class is swept on its own scale instead of on the long class's
    /// cadence. A null one-shot timeout — or one at or above the configured retention — keeps the
    /// configured retention and therefore the previous cadence exactly.
    /// </summary>
    internal static TimeSpan EffectiveUdpRetentionFloor(TimeSpan relayIdleTimeout, TimeSpan? oneShotIdleTimeout) =>
        oneShotIdleTimeout is { } oneShot && oneShot < relayIdleTimeout ? oneShot : relayIdleTimeout;

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
                var (tcpCount, flowCount, sweptAt) = await SweepMainLegSafelyAsync(now, lastMainSweepUtc, token).ConfigureAwait(false);
                lastMainSweepUtc = sweptAt;
                var udpCount = await SweepUdpLegSafelyAsync(now, token).ConfigureAwait(false);
                var attributionCount = SweepAttributionLegSafely(now);
                LogExpired(tcpCount, flowCount, udpCount, attributionCount);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
    }

    /// <summary>
    /// The gated main-leg group behind its own failure containment. The cadence stamp is returned
    /// rather than mutated so a failing sweep still advances it: the main legs are stamped before
    /// they run, so a failure cannot shorten their cadence.
    /// </summary>
    private async Task<(int TcpCount, int FlowCount, DateTimeOffset SweptAt)> SweepMainLegSafelyAsync(DateTimeOffset now, DateTimeOffset lastMainSweepUtc, CancellationToken token)
    {
        if (now - lastMainSweepUtc < _interval) return (0, 0, lastMainSweepUtc);
        try
        {
            var (tcpCount, flowCount) = await SweepMainLegsAsync(now).ConfigureAwait(false);
            return (tcpCount, flowCount, now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // A sweep failure must not stop the capture loop; the next tick retries. It is still
            // surfaced (rate-limited) so a persistently failing leg is diagnosable.
            LogSweepFailureRateLimited(exception);
            return (0, 0, now);
        }
    }

    /// <summary>The UDP leg behind its own failure containment, independent of the main-leg gate.</summary>
    private async Task<int> SweepUdpLegSafelyAsync(DateTimeOffset now, CancellationToken token)
    {
        try
        {
            return await SweepUdpLegAsync(now).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            LogSweepFailureRateLimited(exception);
            return 0;
        }
    }

    /// <summary>
    /// The pending-attribution TTL leg. It is synchronous and allocation-free when nothing expires,
    /// so it rides every tick without a cadence of its own.
    /// </summary>
    private int SweepAttributionLegSafely(DateTimeOffset now)
    {
        if (_attributionSweep is null) return 0;
        try
        {
            return _attributionSweep(now);
        }
        catch (Exception exception)
        {
            LogSweepFailureRateLimited(exception);
            return 0;
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
        _udp is null ? 0 : await _udp.RemoveExpiredAsync(now, _relayIdleTimeout, _udpOneShotIdleTimeout ?? _relayIdleTimeout).ConfigureAwait(false);

    /// <summary>The per-tick <c>runtime.expired</c> aggregate, emitted only when a leg expired something.</summary>
    private void LogExpired(int tcpCount, int flowCount, int udpCount, int attributionCount)
    {
        if (flowCount == 0 && tcpCount == 0 && udpCount == 0 && attributionCount == 0) return;
        RuntimeLog.RuntimeExpired(_logger, flowCount, tcpCount, udpCount, attributionCount);
    }

    private void LogSweepFailureRateLimited(Exception exception)
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastSweepFailureLogTicks);
        if (now - last < s_sweepFailureLogInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastSweepFailureLogTicks, now, last) != last) return;
        var error = exception.GetType().Name;
        RuntimeLog.IdleExpirySweepFailed(_logger, error, exception.Message);
    }

    // The drain is the whole teardown for this owner — seal, cancel (unwinding the timer wait),
    // join the in-flight tick, release the owned CTS last — and is itself single-flight, so a
    // second DisposeAsync joins it instead of re-running teardown.
    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
