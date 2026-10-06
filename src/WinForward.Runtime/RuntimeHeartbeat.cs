using System.Globalization;
using Microsoft.Extensions.Logging;
using WinForward.Runtime.Logging;

namespace WinForward.Runtime;

/// <summary>
/// The component usage counts one <c>runner.heartbeat</c> line summarizes (task 09-17 R2.3):
/// live flow decisions, TCP redirect sessions, UDP sessions (each against its capacity), and
/// the current capture generation's pump counts. Zero-valued members are omitted from the
/// emitted line, so an idle runtime keeps the heartbeat to its uptime alone.
/// </summary>
/// <param name="UdpRelayBufferBytes">
/// The estimated aggregate relay receive-buffer bytes: live UDP sessions multiplied by the
/// configured per-session buffer. An estimate only — each relay socket requests that size and the
/// OS may cap or double it, and a session still in setup holds no relay socket yet.
/// </param>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly record struct RuntimeHeartbeatUsage(
    int FlowsActive,
    int FlowCapacity,
    int TcpSessions,
    int TcpCapacity,
    int UdpSessions,
    int UdpCapacity,
    int PumpsRunning,
    int PumpsDegraded,
    long UdpRelayBufferBytes = 0);

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
    private readonly ILogger _logger;
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
        ILogger logger,
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
        ILogger logger,
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
                    var error = exception.GetType().Name;
                    RuntimeLog.HeartbeatSummaryFailed(_logger, error, exception.Message);
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
        var gc = ReadGcSnapshot();
        var intervalGen0 = gc.Gen0Collections - _lastGcSnapshot.Gen0Collections;
        var intervalGen1 = gc.Gen1Collections - _lastGcSnapshot.Gen1Collections;
        var intervalGen2 = gc.Gen2Collections - _lastGcSnapshot.Gen2Collections;
        // The heartbeat's argument list is the one place in the runtime where a tick does real work
        // the level may discard: it calls the usage delegate and builds the pool and counter-delta
        // blocks. The tick is timer-driven rather than event-driven, so unlike every other call site
        // that work would happen even on a tick that logs nothing. The guard covers only the
        // heartbeat; the warn-level GC alarm below is independent of it.
        if (_logger.IsEnabled(LogLevel.Information)) EmitHeartbeat(gc);
        if (intervalGen0 > 0 || intervalGen1 > 0 || intervalGen2 > 0)
        {
            WarnGcCollected(gc, intervalGen0, intervalGen1, intervalGen2);
        }
        _lastGcSnapshot = gc;
    }

    private void EmitHeartbeat(RuntimeGcSnapshot gc)
    {
        var current = _counters.Snapshot();
        var now = _time.GetUtcNow();
        var usage = _usage?.Invoke() ?? default;
        var cooldown = _health?.CooldownRemaining ?? TimeSpan.Zero;
        var gcGen0 = gc.Gen0Collections - _gcStartupMark.Gen0Collections;
        var gcGen1 = gc.Gen1Collections - _gcStartupMark.Gen1Collections;
        var gcGen2 = gc.Gen2Collections - _gcStartupMark.Gen2Collections;
        var gcCollections = gcGen0 + gcGen1 + gcGen2;
        var gcAllocatedBytes = gc.AllocatedBytes - _gcStartupMark.AllocatedBytes;
        var pools = _counters.GetRegisteredPools();
        var poolOccupancy = _counters.GetTotalPoolOccupancy();
        var deltas = DescribeDeltas(current);
        RuntimeLog.RunnerHeartbeat(
            _logger,
            (long)(now - _startedUtc).TotalSeconds,
            usage.FlowsActive > 0 ? usage.FlowsActive : null,
            usage.FlowCapacity > 0 ? usage.FlowCapacity : null,
            usage.TcpSessions > 0 ? usage.TcpSessions : null,
            usage.TcpCapacity > 0 ? usage.TcpCapacity : null,
            usage.UdpSessions > 0 ? usage.UdpSessions : null,
            usage.UdpCapacity > 0 ? usage.UdpCapacity : null,
            usage.UdpRelayBufferBytes >= 1024 * 1024 ? (int)(usage.UdpRelayBufferBytes / (1024 * 1024)) : null,
            usage.PumpsRunning > 0 ? usage.PumpsRunning : null,
            usage.PumpsDegraded > 0 ? usage.PumpsDegraded : null,
            _health is { IsDegraded: true } ? "true" : null,
            _health is { ConsecutiveForcedTriggers: > 0 and var forced } ? forced : null,
            cooldown > TimeSpan.Zero ? (long)cooldown.TotalSeconds : null,
            gcCollections > 0 ? gcCollections : null,
            gcGen0 > 0 ? gcGen0 : null,
            gcGen1 > 0 ? gcGen1 : null,
            gcGen2 > 0 ? gcGen2 : null,
            gcAllocatedBytes > 0 ? gcAllocatedBytes : null,
            pools.Count > 0 ? pools.Count : null,
            poolOccupancy != 0 ? poolOccupancy : null,
            deltas);
        _lastCounters = current;
    }

    /// <summary>
    /// The heartbeat's counter-delta block: one <c>key=delta</c> token per counter that moved since
    /// the previous heartbeat, in ordinal key order, and null when nothing moved.
    /// </summary>
    private string? DescribeDeltas(IReadOnlyDictionary<string, long> current)
    {
        var deltas = new List<string>();
        foreach (var pair in current.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var delta = pair.Value - _lastCounters.GetValueOrDefault(pair.Key);
            if (delta != 0) deltas.Add(string.Create(CultureInfo.InvariantCulture, $"{pair.Key}={delta}"));
        }

        return deltas.Count == 0 ? null : string.Join(' ', deltas);
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
        var sinceStart = (gc.Gen0Collections - _gcStartupMark.Gen0Collections)
            + (gc.Gen1Collections - _gcStartupMark.Gen1Collections)
            + (gc.Gen2Collections - _gcStartupMark.Gen2Collections);
        RuntimeLog.GcCollected(
            _logger,
            intervalGen0 > 0 ? intervalGen0 : null,
            intervalGen1 > 0 ? intervalGen1 : null,
            intervalGen2 > 0 ? intervalGen2 : null,
            sinceStart);
    }

    // The drain is the whole teardown for this owner — seal, cancel (unwinding the timer wait),
    // join the in-flight tick, release the owned CTS last — and is itself single-flight, so a
    // second DisposeAsync joins it instead of re-running teardown.
    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
