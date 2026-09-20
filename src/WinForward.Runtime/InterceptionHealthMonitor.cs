using WinForward.Configuration;

namespace WinForward.Runtime;

/// <summary>
/// Reports one interception-path failure observation (reinject/forward failures the capture
/// runner may treat as an adapter-view staleness signal). <c>counter</c> is a
/// <see cref="RuntimeCounters"/> key constant; the receiving monitor owns thresholding,
/// cooldown, and forced-refresh pacing. Reporting is observational: it must never change packet
/// disposition, and call sites keep their existing fail-closed behavior unchanged.
/// </summary>
public interface IInterceptionHealthSignal
{
    /// <summary>Records one failure observation for the named counter.</summary>
    void ReportFailure(string counter);
}

/// <summary>
/// Thresholds interception-path failure signals and paces the forced refreshes they request
/// (task 09-17 R1-B, design §3.1): a per-counter 30 s sliding-window count crossing its
/// threshold fires the attached trigger once, then a 60 s cooldown shared by every counter
/// suppresses further triggers while reports keep counting. Three consecutive triggers with no
/// successful refresh in between (<see cref="NoteRefreshCompleted"/>) degrade the monitor: one
/// error-level <c>runner.forcedRefresh.degraded</c> event is emitted and the trigger cadence
/// drops to one per 5 minutes — the forced-rebuild counterpart of the runner's startup-recovery
/// budget, so a persistent failure source cannot drive a rebuild loop at cooldown rate.
/// All state sits behind one gate; a report costs a locked ring append with no allocation
/// (the reinjector sites fire per datagram under failure, so the report path stays lean).
/// Window counts saturate at the threshold — the trigger decision needs "at least threshold
/// within the window", while <see cref="RuntimeCounters"/> keeps the true aggregates.
/// </summary>
public sealed class InterceptionHealthMonitor(
    IRuntimeLogger? logger = null,
    Action<string>? onTrigger = null,
    TimeProvider? timeProvider = null,
    IReadOnlyDictionary<string, int>? thresholds = null) : IInterceptionHealthSignal
{
    /// <summary>The shared no-op every injection point defaults to, so no call site needs a null guard.</summary>
    public static IInterceptionHealthSignal Noop { get; } = new NoopSignal();

    private static readonly TimeSpan s_defaultWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_defaultTriggerCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_degradedTriggerSpacing = TimeSpan.FromMinutes(5);
    private const int DegradedAfterConsecutiveTriggers = 3;

    /// <summary>Default per-counter trigger thresholds, keyed by <see cref="RuntimeCounters"/> constants.</summary>
    private static IReadOnlyDictionary<string, int> DefaultThresholds { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [RuntimeCounters.RelaySetupFailed] = 3,
        [RuntimeCounters.PassReinjectFailed] = 3,
        [RuntimeCounters.UdpOriginUnresolved] = 8,
        [RuntimeCounters.UdpFailClosedDrop] = 8,
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, CounterWindow> _windows = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, int> _thresholds = thresholds ?? DefaultThresholds;
    private readonly IRuntimeLogger _logger = logger ?? NullRuntimeLogger.Instance;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private Action<string>? _onTrigger = onTrigger;
    private DateTimeOffset _nextTriggerUtc;
    private bool _degraded;
    private int _consecutiveForced;

    /// <summary>Consecutive triggers fired since the last <see cref="NoteRefreshCompleted"/> (diagnostics/heartbeat).</summary>
    public int ConsecutiveForcedTriggers
    {
        get { lock (_gate) return _consecutiveForced; }
    }

    /// <summary>Whether the degrade budget was exhausted (one trigger per 5 minutes until the next refresh completes).</summary>
    public bool IsDegraded
    {
        get { lock (_gate) return _degraded; }
    }

    /// <summary>How much longer triggers are suppressed (cooldown or degraded spacing, whichever is active).</summary>
    public TimeSpan CooldownRemaining
    {
        get
        {
            lock (_gate)
            {
                var remaining = _nextTriggerUtc - _time.GetUtcNow();
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// A copy of every tracked counter's current window count (saturated at its threshold) —
    /// the <c>runner.forcedRefresh</c> context and the heartbeat's health summary source.
    /// </summary>
    public IReadOnlyDictionary<string, int> WindowSnapshot()
    {
        lock (_gate)
        {
            var snapshot = new Dictionary<string, int>(_windows.Count, StringComparer.Ordinal);
            foreach (var pair in _windows) snapshot[pair.Key] = pair.Value.Count;
            return snapshot;
        }
    }

    /// <summary>
    /// Attaches the capture runner's forced-refresh handler. Two-phase on purpose: the durable
    /// bundle (whose components report into the signal) is composed before the runner exists, so
    /// <c>Program</c> creates the monitor, hands it to both sides, and the runner attaches here.
    /// Throws when a handler is already attached.
    /// </summary>
    public void AttachTrigger(Action<string> onTrigger)
    {
        ArgumentNullException.ThrowIfNull(onTrigger);
        lock (_gate)
        {
            if (_onTrigger is not null) throw new InvalidOperationException("A trigger handler is already attached.");
            _onTrigger = onTrigger;
        }
    }

    public void ReportFailure(string counter)
    {
        string fired;
        Action<string>? handler;
        var degradedNow = false;
        int consecutive;
        lock (_gate)
        {
            if (!_thresholds.TryGetValue(counter, out var threshold)) return;
            var now = _time.GetUtcNow();
            var window = GetOrAddWindow(counter, threshold);
            window.Record(now.Ticks, s_defaultWindow.Ticks);
            if (now < _nextTriggerUtc || window.Count < threshold) return;
            _consecutiveForced++;
            consecutive = _consecutiveForced;
            if (!_degraded && _consecutiveForced >= DegradedAfterConsecutiveTriggers)
            {
                _degraded = true;
                degradedNow = true;
            }
            _nextTriggerUtc = now + (_degraded ? s_degradedTriggerSpacing : s_defaultTriggerCooldown);
            fired = counter;
            handler = _onTrigger;
        }
        // Outside the gate: the transition to degraded happened exactly once under it, and the
        // handler may itself consult the snapshot state.
        if (degradedNow) LogDegraded(consecutive);
        handler?.Invoke(fired);
    }

    /// <summary>
    /// The runner's "a refresh demand ended with a live generation" hook: resets the consecutive
    /// and degraded state and clears every window — the failure cluster was presumably addressed
    /// by the rebuilt adapter view. The active cooldown deliberately survives (a persistent
    /// failure stream cannot re-trigger faster than the cadence it already earned).
    /// </summary>
    public void NoteRefreshCompleted()
    {
        lock (_gate)
        {
            _consecutiveForced = 0;
            _degraded = false;
            foreach (var window in _windows.Values) window.Clear();
        }
    }

    private void LogDegraded(int consecutive)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Error)) return;
        _logger.Event(RuntimeLogLevel.Error, "runner.forcedRefresh.degraded",
            new("consecutive", consecutive),
            new("spacingSeconds", (long)s_degradedTriggerSpacing.TotalSeconds));
    }

    private CounterWindow GetOrAddWindow(string counter, int threshold)
    {
        if (!_windows.TryGetValue(counter, out var window))
        {
            window = new CounterWindow(threshold);
            _windows[counter] = window;
        }
        return window;
    }

    /// <summary>
    /// Fixed-capacity ring of report timestamps for one counter (capacity = its threshold). The
    /// count saturates at capacity; once saturated, the ring keeps the NEWEST stamps so age-based
    /// pruning at a later report still reflects reality — reports that arrived during a cooldown
    /// count toward the next trigger after the cooldown expires.
    /// </summary>
    private sealed class CounterWindow(int capacity)
    {
        private readonly long[] _ticks = new long[capacity];
        private int _head;
        public int Count { get; private set; }

        public void Record(long nowTicks, long windowTicks)
        {
            while (Count > 0 && nowTicks - _ticks[_head] >= windowTicks)
            {
                _head = (_head + 1) % _ticks.Length;
                Count--;
            }
            _ticks[(_head + Count) % _ticks.Length] = nowTicks;
            if (Count < _ticks.Length) Count++;
            else _head = (_head + 1) % _ticks.Length;
        }

        public void Clear()
        {
            _head = 0;
            Count = 0;
        }
    }

    private sealed class NoopSignal : IInterceptionHealthSignal
    {
        public void ReportFailure(string counter)
        {
            // Deliberately inert: the default injection for tests and non-production compositions.
        }
    }
}
