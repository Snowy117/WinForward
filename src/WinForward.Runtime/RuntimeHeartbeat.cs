using WinForward.Configuration;

namespace WinForward.Runtime;

/// <summary>
/// The component usage counts one <c>runner.heartbeat</c> line summarizes (task 09-17 R2.3):
/// live flow decisions, TCP redirect sessions, UDP sessions (each against its capacity), and
/// the current capture generation's pump counts. Zero-valued members are omitted from the
/// emitted line, so an idle runtime keeps the heartbeat to its uptime alone.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly record struct RuntimeHeartbeatUsage(
    int FlowsActive,
    int FlowCapacity,
    int TcpSessions,
    int TcpCapacity,
    int UdpSessions,
    int UdpCapacity,
    int PumpsRunning,
    int PumpsDegraded);

/// <summary>
/// Emits the periodic <c>runner.heartbeat</c> info summary (task 09-17 R2.3, default every 60 s):
/// uptime, the <see cref="RuntimeHeartbeatUsage"/> counts, the interception-health state, and the
/// per-counter deltas since the previous heartbeat computed from <see cref="RuntimeCounters"/>.
/// The heartbeat is purely observational — it must never influence packet disposition, refresh,
/// or shutdown decisions — so a fault while gathering usage is logged and retried on the next
/// tick instead of killing the loop. Counter fields are deltas since the previous heartbeat
/// (only non-zero deltas are emitted; the aggregates live in the counters themselves).
/// </summary>
public sealed class RuntimeHeartbeat : IAsyncDisposable
{
    /// <summary>The default heartbeat interval; <see cref="TimeSpan.Zero"/>-like values are rejected (use disposal to stop).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    private readonly IRuntimeLogger _logger;
    private readonly Func<RuntimeHeartbeatUsage>? _usage;
    private readonly RuntimeCounters _counters;
    private readonly InterceptionHealthMonitor? _health;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _shutdown = new();
    private DateTimeOffset _startedUtc;
    private IReadOnlyDictionary<string, long> _lastCounters = new Dictionary<string, long>(StringComparer.Ordinal);
    private Task? _loop;

    public RuntimeHeartbeat(
        IRuntimeLogger logger,
        Func<RuntimeHeartbeatUsage>? usage = null,
        RuntimeCounters? counters = null,
        InterceptionHealthMonitor? health = null,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (interval is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The heartbeat interval must be positive.");
        }
        _logger = logger;
        _usage = usage;
        _counters = counters ?? RuntimeCounters.Shared;
        _health = health;
        _interval = interval ?? DefaultInterval;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Starts the heartbeat loop on demand; a second start is a programming error. Not starting keeps the runtime silent.</summary>
    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("The runtime heartbeat is already started.");
        _startedUtc = _time.GetUtcNow();
        _lastCounters = _counters.Snapshot();
        _loop = RunAsync();
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(_interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    Emit();
                }
                catch (Exception exception)
                {
                    // Usage sources (coordinators, the runner) may be mid-teardown; the heartbeat
                    // retries on the next tick and never takes anything down with it.
                    _logger.Warn($"Runtime heartbeat summary failed: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
    }

    private void Emit()
    {
        var current = _counters.Snapshot();
        if (_logger.IsEnabled(RuntimeLogLevel.Info))
        {
            var now = _time.GetUtcNow();
            var usage = _usage is { } usageProvider ? usageProvider() : default;
            var fields = new List<RuntimeLogField>(12 + current.Count)
            {
                new("uptimeSeconds", (long)(now - _startedUtc).TotalSeconds),
            };
            AddPositive(fields, "flows", usage.FlowsActive);
            AddPositive(fields, "flowCapacity", usage.FlowCapacity);
            AddPositive(fields, "tcpSessions", usage.TcpSessions);
            AddPositive(fields, "tcpCapacity", usage.TcpCapacity);
            AddPositive(fields, "udpSessions", usage.UdpSessions);
            AddPositive(fields, "udpCapacity", usage.UdpCapacity);
            AddPositive(fields, "pumpsRunning", usage.PumpsRunning);
            AddPositive(fields, "pumpsDegraded", usage.PumpsDegraded);
            if (_health is { } health)
            {
                if (health.IsDegraded) fields.Add(new("degraded", "true"));
                AddPositive(fields, "consecutiveForced", health.ConsecutiveForcedTriggers);
                var cooldown = health.CooldownRemaining;
                if (cooldown > TimeSpan.Zero) fields.Add(new("cooldownSeconds", (long)cooldown.TotalSeconds));
            }
            foreach (var pair in current.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                var delta = pair.Value - (_lastCounters.TryGetValue(pair.Key, out var previous) ? previous : 0);
                if (delta != 0) fields.Add(new RuntimeLogField(pair.Key, delta));
            }
            _logger.Event(RuntimeLogLevel.Info, "runner.heartbeat", fields.ToArray());
        }
        _lastCounters = current;
    }

    private static void AddPositive(List<RuntimeLogField> fields, string key, int value)
    {
        if (value > 0) fields.Add(new RuntimeLogField(key, value));
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
