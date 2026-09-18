using WinForward.Configuration;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The periodic <c>runner.heartbeat</c> summary (task 09-17 R2.3): one info event per tick with
/// uptime, usage counts, interception-health state, and per-counter deltas since the previous
/// heartbeat; zero-valued fields are omitted to keep the line compact, and disposal stops the
/// loop. Timing follows the periodic-refresh test pattern (real short intervals plus polling),
/// and per-tick deltas are staged between observed ticks so no assertion races the timer.
/// </summary>
public sealed class RuntimeHeartbeatTests
{
    private static readonly TimeSpan s_tick = TimeSpan.FromMilliseconds(40);

    private static RuntimeHeartbeatUsage Usage(int flows = 0, int tcp = 0, int udp = 0, int pumpsRunning = 0, int pumpsDegraded = 0) =>
        new(flows, 1000, tcp, 4096, udp, 16_384, pumpsRunning, pumpsDegraded);

    private static List<(RuntimeLogLevel Level, RuntimeLogField[] Fields)> Heartbeats(RecordingRuntimeLogger logger) =>
        logger.Events.Where(entry => string.Equals(entry.Name, "runner.heartbeat", StringComparison.Ordinal))
            .Select(entry => (entry.Level, entry.Fields))
            .ToList();

    private static object? Field(RuntimeLogField[] fields, string key) =>
        fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal)).Value;

    [Fact]
    public async Task TickEmitsInfoHeartbeatWithUsageAndHealthFields()
    {
        var logger = new RecordingRuntimeLogger();
        var health = new InterceptionHealthMonitor(logger, thresholds: new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [RuntimeCounters.RelaySetupFailed] = 1,
        });
        health.ReportFailure(RuntimeCounters.RelaySetupFailed);
        var usage = Usage(flows: 12, tcp: 3, udp: 1, pumpsRunning: 2, pumpsDegraded: 1);
        await using var heartbeat = new RuntimeHeartbeat(logger, usage: () => usage, counters: new RuntimeCounters(), health: health, interval: s_tick);
        heartbeat.Start();

        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);

        var beat = Heartbeats(logger)[0];
        Assert.Equal(RuntimeLogLevel.Info, beat.Level);
        var fields = beat.Fields;
        Assert.True((long)Field(fields, "uptimeSeconds")! >= 0);
        Assert.Equal(12, Field(fields, "flows"));
        Assert.Equal(1000, Field(fields, "flowCapacity"));
        Assert.Equal(3, Field(fields, "tcpSessions"));
        Assert.Equal(4096, Field(fields, "tcpCapacity"));
        Assert.Equal(1, Field(fields, "udpSessions"));
        Assert.Equal(16_384, Field(fields, "udpCapacity"));
        Assert.Equal(2, Field(fields, "pumpsRunning"));
        Assert.Equal(1, Field(fields, "pumpsDegraded"));
        Assert.Equal(1, Field(fields, "consecutiveForced"));
        Assert.True((long)Field(fields, "cooldownSeconds")! > 0);
        Assert.Null(Field(fields, "degraded"));
    }

    [Fact]
    public async Task CounterFieldsAreDeltasSinceThePreviousHeartbeat()
    {
        var logger = new RecordingRuntimeLogger();
        var counters = new RuntimeCounters();
        await using var heartbeat = new RuntimeHeartbeat(logger, counters: counters, interval: s_tick);
        heartbeat.Start();

        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);
        counters.Increment(RuntimeCounters.RelaySetupFailed);
        counters.Increment(RuntimeCounters.RelaySetupFailed);
        counters.Increment(RuntimeCounters.RelaySetupFailed);
        counters.Increment(RuntimeCounters.PassReinjectFailed);
        // Waiting for a beat count is not enough under scheduler load: a tick may snapshot
        // before the increments above complete. Wait for THE beat that provably observed them
        // (recorded by index, so a subsequent tick cannot invalidate the assertion target).
        var observed = -1;
        await AsyncTestExtensions.WaitForAsync(() =>
        {
            var beats = Heartbeats(logger);
            for (var i = 1; i < beats.Count; i++)
            {
                if (3L.Equals(Field(beats[i].Fields, RuntimeCounters.RelaySetupFailed))
                    && 1L.Equals(Field(beats[i].Fields, RuntimeCounters.PassReinjectFailed)))
                {
                    observed = i;
                    return true;
                }
            }
            return false;
        }).ConfigureAwait(false);

        var beats = Heartbeats(logger);
        Assert.Null(Field(beats[0].Fields, RuntimeCounters.RelaySetupFailed));
        Assert.Equal(3L, Field(beats[observed].Fields, RuntimeCounters.RelaySetupFailed));
        Assert.Equal(1L, Field(beats[observed].Fields, RuntimeCounters.PassReinjectFailed));
        // A counter with no new hits in the interval stays absent rather than reporting a zero delta.
        counters.Increment(RuntimeCounters.RelaySetupFailed);
        var thirdIndex = -1;
        await AsyncTestExtensions.WaitForAsync(() =>
        {
            var list = Heartbeats(logger);
            for (var i = observed + 1; i < list.Count; i++)
            {
                if (1L.Equals(Field(list[i].Fields, RuntimeCounters.RelaySetupFailed))
                    && Field(list[i].Fields, RuntimeCounters.PassReinjectFailed) is null)
                {
                    thirdIndex = i;
                    return true;
                }
            }
            return false;
        }).ConfigureAwait(false);
        var third = Heartbeats(logger)[thirdIndex].Fields;
        Assert.Equal(1L, Field(third, RuntimeCounters.RelaySetupFailed));
        Assert.Null(Field(third, RuntimeCounters.PassReinjectFailed));
    }

    [Fact]
    public async Task IdleHeartbeatOmitsEveryZeroValuedField()
    {
        var logger = new RecordingRuntimeLogger();
        var gc = new MutableGcSnapshotSource();
        await using var heartbeat = new RuntimeHeartbeat(
            logger, counters: new RuntimeCounters(), interval: s_tick, gcSnapshotProvider: gc.Read);
        heartbeat.Start();

        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);

        var fields = Heartbeats(logger)[0].Fields;
        Assert.NotNull(Field(fields, "uptimeSeconds"));
        Assert.Equal("uptimeSeconds", fields.Single().Key);
    }

    [Fact]
    public async Task FaultyUsageProviderWarnsAndTheLoopSurvives()
    {
        var logger = new RecordingRuntimeLogger();
        var calls = 0;
        var heartbeat = new RuntimeHeartbeat(
            logger,
            usage: () =>
            {
                calls++;
                return calls == 1 ? throw new InvalidOperationException("usage source unavailable") : Usage(flows: 5);
            },
            counters: new RuntimeCounters(),
            interval: s_tick);
        await using (heartbeat)
        {
            heartbeat.Start();

            await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);
        }

        Assert.Contains(logger.Lines, line => line.Level == RuntimeLogLevel.Warn && line.Message.Contains("usage source unavailable", StringComparison.Ordinal));
        Assert.Equal(5, Field(Heartbeats(logger)[0].Fields, "flows"));
    }

    [Fact]
    public async Task DisposeStopsFurtherTicks()
    {
        var logger = new RecordingRuntimeLogger();
        var heartbeat = new RuntimeHeartbeat(logger, counters: new RuntimeCounters(), interval: s_tick);
        heartbeat.Start();
        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);

        await heartbeat.DisposeAsync().ConfigureAwait(false);
        var countAtDispose = Heartbeats(logger).Count;
        await Task.Delay(150).ConfigureAwait(false);

        Assert.Equal(countAtDispose, Heartbeats(logger).Count);
    }

    [Fact]
    public async Task DefaultHeartbeatIsSilentUntilStarted()
    {
        var logger = new RecordingRuntimeLogger();
        var heartbeat = new RuntimeHeartbeat(logger, counters: new RuntimeCounters(), interval: TimeSpan.FromMilliseconds(20));

        await Task.Delay(80).ConfigureAwait(false);
        Assert.Empty(Heartbeats(logger));

        await heartbeat.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public void NonPositiveIntervalIsRejected()
    {
        var fault = Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeHeartbeat(new RecordingRuntimeLogger(), interval: TimeSpan.Zero));
        Assert.Equal("interval", fault.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeHeartbeat(new RecordingRuntimeLogger(), interval: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task SecondStartIsRejected()
    {
        var heartbeat = new RuntimeHeartbeat(new RecordingRuntimeLogger(), counters: new RuntimeCounters(), interval: TimeSpan.FromSeconds(10));
        heartbeat.Start();

        Assert.Throws<InvalidOperationException>(heartbeat.Start);

        await heartbeat.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task HeartbeatReportsGcDeltasSinceTheStartupMarkAndWarnsOnNewCollections()
    {
        var logger = new RecordingRuntimeLogger();
        var gc = new MutableGcSnapshotSource { Current = new(Gen0Collections: 10, Gen1Collections: 2, Gen2Collections: 1, AllocatedBytes: 1_000_000) };
        await using var heartbeat = new RuntimeHeartbeat(
            logger, counters: new RuntimeCounters(), interval: s_tick, gcSnapshotProvider: gc.Read);
        heartbeat.Start();
        gc.Current = new(Gen0Collections: 12, Gen1Collections: 2, Gen2Collections: 1, AllocatedBytes: 1_500_000);

        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);

        var fields = Heartbeats(logger)[0].Fields;
        Assert.Equal(2, Field(fields, "gcCollections"));
        Assert.Equal(2, Field(fields, "gcGen0"));
        Assert.Null(Field(fields, "gcGen1"));
        Assert.Null(Field(fields, "gcGen2"));
        Assert.Equal(500_000L, Field(fields, "gcAllocatedBytes"));
        var warn = logger.Events.Single(entry => string.Equals(entry.Name, "gc.collected", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Warn, warn.Level);
        Assert.Equal(2, Field(warn.Fields, "gen0"));
        Assert.Null(Field(warn.Fields, "gen1"));
        Assert.Null(Field(warn.Fields, "gen2"));
        Assert.Equal(2, Field(warn.Fields, "sinceStart"));
    }

    [Fact]
    public async Task GcCollectionWarnFiresOnlyOnTheTickThatObservesNewCollections()
    {
        var logger = new RecordingRuntimeLogger();
        var gc = new MutableGcSnapshotSource { Current = new(Gen0Collections: 5, Gen1Collections: 0, Gen2Collections: 0, AllocatedBytes: 0) };
        await using var heartbeat = new RuntimeHeartbeat(
            logger, counters: new RuntimeCounters(), interval: s_tick, gcSnapshotProvider: gc.Read);
        heartbeat.Start();

        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);
        gc.Current = new(Gen0Collections: 7, Gen1Collections: 0, Gen2Collections: 0, AllocatedBytes: 0);
        // Beat indexes cannot be derived from beat counts under scheduler load: a tick may read
        // the sample before the mutation above. Find the beat that observed the collections.
        var warnedIndex = -1;
        await AsyncTestExtensions.WaitForAsync(() =>
        {
            var beats = Heartbeats(logger);
            for (var i = 1; i < beats.Count; i++)
            {
                if (2.Equals(Field(beats[i].Fields, "gcCollections")))
                {
                    warnedIndex = i;
                    return true;
                }
            }
            return false;
        }).ConfigureAwait(false);
        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= warnedIndex + 2).ConfigureAwait(false);

        // Tick 1 is clean; the observing tick warns exactly once; the next tick still reports the
        // cumulative delta against the startup mark but does not re-warn.
        var beats = Heartbeats(logger);
        Assert.Null(Field(beats[0].Fields, "gcCollections"));
        Assert.Equal(2, Field(beats[warnedIndex].Fields, "gcCollections"));
        Assert.Equal(2, Field(beats[warnedIndex + 1].Fields, "gcCollections"));
        var warn = logger.Events.Single(entry => string.Equals(entry.Name, "gc.collected", StringComparison.Ordinal));
        Assert.Equal(2, Field(warn.Fields, "gen0"));
        Assert.Equal(2, Field(warn.Fields, "sinceStart"));
    }

    [Fact]
    public async Task HeartbeatReportsAggregatePoolOccupancyFromRegisteredPools()
    {
        var logger = new RecordingRuntimeLogger();
        var counters = new RuntimeCounters();
        counters.RegisterPool("frame");
        counters.RecordPoolRent("frame");
        counters.RecordPoolRent("frame");
        counters.RecordPoolRent("frame");
        var gc = new MutableGcSnapshotSource();
        await using var heartbeat = new RuntimeHeartbeat(
            logger, counters: counters, interval: s_tick, gcSnapshotProvider: gc.Read);
        heartbeat.Start();

        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);
        var first = Heartbeats(logger)[0].Fields;
        Assert.Equal(1, Field(first, "pools"));
        Assert.Equal(3L, Field(first, "poolOccupancy"));
        // The rents predate the startup counter snapshot, so tick 1 reports no rent delta.
        Assert.Null(Field(first, RuntimeCounters.PoolRentedKey("frame")));

        counters.RecordPoolReturn("frame");
        // A later tick may snapshot before the return lands under scheduler load; find the beat
        // that provably observed it instead of assuming it is the second beat.
        var returnIndex = -1;
        await AsyncTestExtensions.WaitForAsync(() =>
        {
            var beats = Heartbeats(logger);
            for (var i = 1; i < beats.Count; i++)
            {
                if (1L.Equals(Field(beats[i].Fields, RuntimeCounters.PoolReturnedKey("frame"))))
                {
                    returnIndex = i;
                    return true;
                }
            }
            return false;
        }).ConfigureAwait(false);
        var second = Heartbeats(logger)[returnIndex].Fields;
        Assert.Equal(1, Field(second, "pools"));
        Assert.Equal(2L, Field(second, "poolOccupancy"));
        Assert.Equal(1L, Field(second, RuntimeCounters.PoolReturnedKey("frame")));
    }

    /// <summary>
    /// A mutable GC sample source: the startup mark and every tick read <see cref="Current"/>, so
    /// tests stage collection and allocation drift deterministically. The real GC counters are
    /// process-global (a parallel test host collects gen0 constantly), so the zero-drift default
    /// keeps field-omission assertions flake-free.
    /// </summary>
    private sealed class MutableGcSnapshotSource
    {
        public RuntimeGcSnapshot Current;
        public RuntimeGcSnapshot Read() => Current;
    }
}
