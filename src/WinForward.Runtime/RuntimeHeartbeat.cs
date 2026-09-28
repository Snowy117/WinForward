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
/// A GC observability sample (task 09-18 M0): per-generation collection counts plus the
/// process-wide allocated-bytes total. The heartbeat records a startup mark and reports
/// deltas against it; tests inject a fixed source so collection-delta coverage is deterministic.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly record struct RuntimeGcSnapshot(
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long AllocatedBytes);

/// <summary>
/// Emits the periodic <c>runner.heartbeat</c> info summary (task 09-17 R2.3, default every 60 s):
/// uptime, the <see cref="RuntimeHeartbeatUsage"/> counts, the interception-health state, and the
/// per-counter deltas since the previous heartbeat computed from <see cref="RuntimeCounters"/>.
/// The heartbeat is purely observational — it must never influence packet disposition, refresh,
/// or shutdown decisions — so a fault while gathering usage is logged and retried on the next
/// tick instead of killing the loop. Counter fields are deltas since the previous heartbeat
/// (only non-zero deltas are emitted; the aggregates live in the counters themselves). Since
/// task 09-18 M0 each tick also reports the GC posture (collection-count deltas and allocated
/// bytes since the startup mark) and the aggregate native-pool occupancy, and warns
/// <c>gc.collected</c> on any tick that observes new collections (the GC-off-posture alarm).
/// </summary>
public sealed class RuntimeHeartbeat : IAsyncDisposable
{
    /// <summary>The default heartbeat interval; <see cref="TimeSpan.Zero"/>-like values are rejected (use disposal to stop).</summary>
    private static readonly TimeSpan s_defaultInterval = TimeSpan.FromSeconds(60);
    private readonly IRuntimeLogger _logger;
    private readonly Func<RuntimeHeartbeatUsage>? _usage;
    private readonly RuntimeCounters _counters;
    private readonly InterceptionHealthMonitor? _health;
    private readonly Func<RuntimeGcSnapshot>? _gcSnapshotProvider;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly QuiescenceScope _scope = new();
    private DateTimeOffset _startedUtc;
    private IReadOnlyDictionary<string, long> _lastCounters = new Dictionary<string, long>(StringComparer.Ordinal);
    private RuntimeGcSnapshot _gcStartupMark;
    private RuntimeGcSnapshot _lastGcSnapshot;
    private int _started;

    public RuntimeHeartbeat(
        IRuntimeLogger logger,
        Func<RuntimeHeartbeatUsage>? usage = null,
        RuntimeCounters? counters = null,
        InterceptionHealthMonitor? health = null,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
        : this(logger, gcSnapshotProvider: null, usage, counters, health, timeProvider, interval)
    {
    }

    /// <summary>
    /// The injectable-snapshot form of the public constructor: tests supply a scripted
    /// <paramref name="gcSnapshotProvider"/> to drive the GC-growth alarm deterministically.
    /// Production reads the process GC counters through the default provider.
    /// </summary>
    internal RuntimeHeartbeat(
        IRuntimeLogger logger,
        Func<RuntimeGcSnapshot>? gcSnapshotProvider,
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
        _interval = interval ?? s_defaultInterval;
        _time = timeProvider ?? TimeProvider.System;
        _gcSnapshotProvider = gcSnapshotProvider;
    }

    /// <summary>Starts the heartbeat loop on demand; a second start is a programming error. Not starting keeps the runtime silent.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The runtime heartbeat is already started.");
        _startedUtc = _time.GetUtcNow();
        _gcStartupMark = ReadGcSnapshot();
        _lastGcSnapshot = _gcStartupMark;
        _lastCounters = _counters.Snapshot();
        ObjectDisposedException.ThrowIf(!_scope.Run(RunAsync, "runtime.heartbeat.loop"), this);
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
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
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
    }

    private void Emit()
    {
        var current = _counters.Snapshot();
        var gc = ReadGcSnapshot();
        var intervalGen0 = gc.Gen0Collections - _lastGcSnapshot.Gen0Collections;
        var intervalGen1 = gc.Gen1Collections - _lastGcSnapshot.Gen1Collections;
        var intervalGen2 = gc.Gen2Collections - _lastGcSnapshot.Gen2Collections;
        if (_logger.IsEnabled(RuntimeLogLevel.Info))
        {
            var now = _time.GetUtcNow();
            var usage = _usage?.Invoke() ?? default;
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
            if (_health is not null)
            {
                if (_health.IsDegraded) fields.Add(new("degraded", "true"));
                AddPositive(fields, "consecutiveForced", _health.ConsecutiveForcedTriggers);
                var cooldown = _health.CooldownRemaining;
                if (cooldown > TimeSpan.Zero) fields.Add(new("cooldownSeconds", (long)cooldown.TotalSeconds));
            }
            AddGcAndPoolFields(fields, gc);
            foreach (var pair in current.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                var delta = pair.Value - _lastCounters.GetValueOrDefault(pair.Key);
                if (delta != 0) fields.Add(new RuntimeLogField(pair.Key, delta));
            }
            _logger.Event(RuntimeLogLevel.Info, "runner.heartbeat", [.. fields]);
        }
        if (intervalGen0 > 0 || intervalGen1 > 0 || intervalGen2 > 0)
        {
            WarnGcCollected(gc, intervalGen0, intervalGen1, intervalGen2);
        }
        _lastGcSnapshot = gc;
        _lastCounters = current;
    }

    /// <summary>
    /// Appends the GC-posture fields (deltas against the startup mark; zero deltas are omitted,
    /// so an idle GC-off runtime stays compact) and the aggregate native-pool occupancy fields
    /// (omitted entirely while no pool is registered).
    /// </summary>
    private void AddGcAndPoolFields(List<RuntimeLogField> fields, RuntimeGcSnapshot gc)
    {
        var gcGen0 = gc.Gen0Collections - _gcStartupMark.Gen0Collections;
        var gcGen1 = gc.Gen1Collections - _gcStartupMark.Gen1Collections;
        var gcGen2 = gc.Gen2Collections - _gcStartupMark.Gen2Collections;
        var gcCollections = gcGen0 + gcGen1 + gcGen2;
        if (gcCollections > 0)
        {
            fields.Add(new("gcCollections", gcCollections));
            AddPositive(fields, "gcGen0", gcGen0);
            AddPositive(fields, "gcGen1", gcGen1);
            AddPositive(fields, "gcGen2", gcGen2);
        }
        var gcAllocatedBytes = gc.AllocatedBytes - _gcStartupMark.AllocatedBytes;
        if (gcAllocatedBytes > 0) fields.Add(new("gcAllocatedBytes", gcAllocatedBytes));
        var pools = _counters.GetRegisteredPools();
        if (pools.Count > 0)
        {
            fields.Add(new("pools", pools.Count));
            var poolOccupancy = _counters.GetTotalPoolOccupancy();
            if (poolOccupancy != 0) fields.Add(new("poolOccupancy", poolOccupancy));
        }
    }

    /// <summary>
    /// Reads the current GC sample: the injected source when tests provide one, otherwise the
    /// real process-wide GC counters (<c>precise: false</c> keeps the read cheap for the cold path).
    /// </summary>
    private RuntimeGcSnapshot ReadGcSnapshot() => _gcSnapshotProvider?.Invoke()
        ?? new RuntimeGcSnapshot(
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalAllocatedBytes(precise: false));

    /// <summary>
    /// The GC-off-posture alarm (task 09-18 R5): fires on every tick that observes NEW collections
    /// (deltas against the previous tick; the first tick compares against the startup mark), not
    /// on every tick after the first collection, so a single historical collection warns exactly
    /// once while the cumulative total stays visible in the heartbeat's <c>gcCollections</c> field.
    /// </summary>
    private void WarnGcCollected(RuntimeGcSnapshot gc, int intervalGen0, int intervalGen1, int intervalGen2)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Warn)) return;
        var fields = new List<RuntimeLogField>(4);
        if (intervalGen0 > 0) fields.Add(new("gen0", intervalGen0));
        if (intervalGen1 > 0) fields.Add(new("gen1", intervalGen1));
        if (intervalGen2 > 0) fields.Add(new("gen2", intervalGen2));
        var sinceStart = (gc.Gen0Collections - _gcStartupMark.Gen0Collections)
            + (gc.Gen1Collections - _gcStartupMark.Gen1Collections)
            + (gc.Gen2Collections - _gcStartupMark.Gen2Collections);
        fields.Add(new("sinceStart", sinceStart));
        _logger.Event(RuntimeLogLevel.Warn, "gc.collected", [.. fields]);
    }

    private static void AddPositive(List<RuntimeLogField> fields, string key, int value)
    {
        if (value > 0) fields.Add(new RuntimeLogField(key, value));
    }

    // The drain is the whole teardown for this owner — seal, cancel (unwinding the timer wait),
    // join the in-flight tick, release the owned CTS last — and is itself single-flight, so a
    // second DisposeAsync joins it instead of re-running teardown.
    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
