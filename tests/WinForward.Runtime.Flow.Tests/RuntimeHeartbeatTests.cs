using System.Globalization;
using Microsoft.Extensions.Logging;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Runtime.Flow.Tests;

/// <summary>
/// The periodic <c>runner.heartbeat</c> summary (task 09-17 R2.3): one info event per tick with
/// uptime, usage counts, interception-health state, and the counter movement since the previous
/// heartbeat; a zero-valued optional field is passed as null rather than omitted, which renders as
/// an empty slot, and disposal stops the loop. Timing follows the periodic-refresh test pattern (real short intervals plus polling),
/// and per-tick deltas are staged between observed ticks so no assertion races the timer.
/// </summary>
public sealed class RuntimeHeartbeatTests
{
    private static readonly TimeSpan s_tick = TimeSpan.FromMilliseconds(40);

    private static RuntimeHeartbeatUsage Usage(int flows = 0, int tcp = 0, int udp = 0, int pumpsRunning = 0, int pumpsDegraded = 0, long relayBufferBytes = 0) =>
        new(flows, 1000, tcp, 4096, udp, 16_384, pumpsRunning, pumpsDegraded, relayBufferBytes);

    private static List<RecordedEvent> Heartbeats(RecordingLogger logger) =>
        [.. logger.Events.Where(entry => string.Equals(entry.Name, "runner.heartbeat", StringComparison.Ordinal))];

    private static object? Field(IReadOnlyList<KeyValuePair<string, object?>> fields, string key) =>
        fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal)).Value;

    /// <summary>The per-key delta tokens of one heartbeat, parsed back into the counter values the old per-key fields carried.</summary>
    private static Dictionary<string, long> Deltas(RecordedEvent heartbeat)
    {
        var deltas = new Dictionary<string, long>(StringComparer.Ordinal);
        if (heartbeat.Field("Deltas") is not string text) return deltas;
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.LastIndexOf('=');
            deltas[token[..separator]] = long.Parse(token[(separator + 1)..], CultureInfo.InvariantCulture);
        }

        return deltas;
    }

    [Fact]
    public async Task TickEmitsInfoHeartbeatWithUsageAndHealthFields()
    {
        var logger = new RecordingLogger();
        var health = new InterceptionHealthMonitor(logger, thresholds: new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [RuntimeCounters.RelaySetupFailed] = 1,
        });
        health.ReportFailure(RuntimeCounters.RelaySetupFailed);
        var usage = Usage(flows: 12, tcp: 3, udp: 1, pumpsRunning: 2, pumpsDegraded: 1);
        await using var heartbeat = new RuntimeHeartbeat(logger, usage: () => usage, counters: new RuntimeCounters(), health: health, interval: s_tick);
        heartbeat.Start();

        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);

        var (level, fields) = Heartbeats(logger)[0];
        Assert.Equal(LogLevel.Information, level);
        Assert.True((long)Field(fields, "UptimeSeconds")! >= 0);
        Assert.Equal(12, Field(fields, "Flows"));
        Assert.Equal(1000, Field(fields, "FlowCapacity"));
        Assert.Equal(3, Field(fields, "TcpSessions"));
        Assert.Equal(4096, Field(fields, "TcpCapacity"));
        Assert.Equal(1, Field(fields, "UdpSessions"));
        Assert.Equal(16_384, Field(fields, "UdpCapacity"));
        Assert.Equal(2, Field(fields, "PumpsRunning"));
        Assert.Equal(1, Field(fields, "PumpsDegraded"));
        Assert.Equal(1, Field(fields, "ConsecutiveForced"));
        Assert.True((long)Field(fields, "CooldownSeconds")! > 0);
        Assert.Null(Field(fields, "Degraded"));
        // No relay receive-buffer estimate is supplied, so the estimate field stays omitted.
        Assert.Null(Field(fields, "UdpRelayBufferMB"));
    }

    [Fact]
    public async Task TickReportsTheEstimatedUdpRelayBufferInMegabytes()
    {
        var logger = new RecordingLogger();
        // 100 sessions x 128 KiB = 12.5 MiB, reported truncated as an estimate.
        var usage = Usage(udp: 100, relayBufferBytes: 100L * 128 * 1024);
        await using var heartbeat = new RuntimeHeartbeat(logger, usage: () => usage, counters: new RuntimeCounters(), interval: s_tick);
        heartbeat.Start();

        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);

        var (_, fields) = Heartbeats(logger)[0];
        Assert.Equal(12, Field(fields, "UdpRelayBufferMB"));
    }

    [Fact]
    public async Task CounterFieldsAreDeltasSinceThePreviousHeartbeat()
    {
        var logger = new RecordingLogger();
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
                var deltas = Deltas(beats[i]);
                if (3L.Equals(deltas.GetValueOrDefault(RuntimeCounters.RelaySetupFailed))
                    && 1L.Equals(deltas.GetValueOrDefault(RuntimeCounters.PassReinjectFailed)))
                {
                    observed = i;
                    return true;
                }
            }
            return false;
        }).ConfigureAwait(false);

        var beats = Heartbeats(logger);
        Assert.Empty(Deltas(beats[0]));
        Assert.Equal(3L, Deltas(beats[observed])[RuntimeCounters.RelaySetupFailed]);
        Assert.Equal(1L, Deltas(beats[observed])[RuntimeCounters.PassReinjectFailed]);
        // The block is rendered in ordinal key order.
        Assert.Equal(
            [RuntimeCounters.PassReinjectFailed, RuntimeCounters.RelaySetupFailed],
            Deltas(beats[observed]).Keys);
        // A counter with no new hits in the interval stays absent rather than reporting a zero delta.
        counters.Increment(RuntimeCounters.RelaySetupFailed);
        var thirdIndex = -1;
        await AsyncTestExtensions.WaitForAsync(() =>
        {
            var list = Heartbeats(logger);
            for (var i = observed + 1; i < list.Count; i++)
            {
                var deltas = Deltas(list[i]);
                if (1L.Equals(deltas.GetValueOrDefault(RuntimeCounters.RelaySetupFailed))
                    && !deltas.ContainsKey(RuntimeCounters.PassReinjectFailed))
                {
                    thirdIndex = i;
                    return true;
                }
            }
            return false;
        }).ConfigureAwait(false);
        var third = Deltas(Heartbeats(logger)[thirdIndex]);
        Assert.Equal(1L, third[RuntimeCounters.RelaySetupFailed]);
        Assert.DoesNotContain(RuntimeCounters.PassReinjectFailed, third.Keys);
    }

    [Fact]
    public async Task IdleHeartbeatCarriesNoZeroValuedField()
    {
        var logger = new RecordingLogger();
        var gc = new MutableGcSnapshotSource();
        await using var heartbeat = new RuntimeHeartbeat(
            logger, gcSnapshotProvider: gc.Read, counters: new RuntimeCounters(), interval: s_tick);
        heartbeat.Start();

        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);

        var idle = Heartbeats(logger)[0];
        Assert.NotNull(Field(idle.Fields, "UptimeSeconds"));
        Assert.All(idle.Fields, field => Assert.True(
            string.Equals(field.Key, "UptimeSeconds", StringComparison.Ordinal) || field.Value is null,
            $"An idle heartbeat reported {field.Key}={field.Value}."));
        Assert.Empty(Deltas(idle));
    }

    [Fact]
    public async Task FaultyUsageProviderWarnsAndTheLoopSurvives()
    {
        var logger = new RecordingLogger();
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

        Assert.Contains(logger.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("usage source unavailable", StringComparison.Ordinal));
        Assert.Equal(5, Field(Heartbeats(logger)[0].Fields, "Flows"));
    }

    [Fact]
    public async Task DisposeStopsFurtherTicks()
    {
        var logger = new RecordingLogger();
        var heartbeat = new RuntimeHeartbeat(logger, counters: new RuntimeCounters(), interval: s_tick);
        heartbeat.Start();
        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);

        await heartbeat.DisposeAsync().ConfigureAwait(false);
        var countAtDispose = Heartbeats(logger).Count;
        await Task.Delay(150).ConfigureAwait(false);

        Assert.Equal(countAtDispose, Heartbeats(logger).Count);
    }

    [Fact]
    public async Task SecondDisposeAsyncJoinsInsteadOfThrowing()
    {
        var logger = new RecordingLogger();
        var heartbeat = new RuntimeHeartbeat(logger, counters: new RuntimeCounters(), interval: s_tick);
        heartbeat.Start();
        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);

        await heartbeat.DisposeAsync().ConfigureAwait(false);
        // Regression: the loop is now a scope child and the scope owns the CTS. Before the migration
        // a second dispose called CancelAsync on the already-disposed source and threw
        // ObjectDisposedException; it must now join the same drain.
        await heartbeat.DisposeAsync().ConfigureAwait(false);

        var countAtDispose = Heartbeats(logger).Count;
        await Task.Delay(150).ConfigureAwait(false);
        Assert.Equal(countAtDispose, Heartbeats(logger).Count);
    }

    [Fact]
    public async Task DisposeAsyncJoinsAnInFlightTick()
    {
        var logger = new RecordingLogger();
        var tickEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = new RuntimeHeartbeat(
            logger,
            usage: () =>
            {
                tickEntered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return Usage(flows: 1);
            },
            counters: new RuntimeCounters(),
            interval: s_tick);
        heartbeat.Start();
        await tickEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        var dispose = Task.Run(async () => await heartbeat.DisposeAsync());
        await Task.Delay(50).ConfigureAwait(false);

        // Regression: if disposal did not join its Run child, it would return while the tick is still
        // inside the usage provider.
        Assert.False(dispose.IsCompleted);

        release.TrySetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    [Fact]
    public async Task DefaultHeartbeatIsSilentUntilStarted()
    {
        var logger = new RecordingLogger();
        var heartbeat = new RuntimeHeartbeat(logger, counters: new RuntimeCounters(), interval: TimeSpan.FromMilliseconds(20));

        await Task.Delay(80).ConfigureAwait(false);
        Assert.Empty(Heartbeats(logger));

        await heartbeat.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public void NonPositiveIntervalIsRejected()
    {
        var fault = Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeHeartbeat(new RecordingLogger(), interval: TimeSpan.Zero));
        Assert.Equal("interval", fault.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeHeartbeat(new RecordingLogger(), interval: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task SecondStartIsRejected()
    {
        var heartbeat = new RuntimeHeartbeat(new RecordingLogger(), counters: new RuntimeCounters(), interval: TimeSpan.FromSeconds(10));
        heartbeat.Start();

        Assert.Throws<InvalidOperationException>(heartbeat.Start);

        await heartbeat.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task HeartbeatReportsGcDeltasSinceTheStartupMarkAndWarnsOnNewCollections()
    {
        var logger = new RecordingLogger();
        var gc = new MutableGcSnapshotSource { Current = new(Gen0Collections: 10, Gen1Collections: 2, Gen2Collections: 1, AllocatedBytes: 1_000_000) };
        await using var heartbeat = new RuntimeHeartbeat(
            logger, gcSnapshotProvider: gc.Read, counters: new RuntimeCounters(), interval: s_tick);
        heartbeat.Start();
        gc.Current = new(Gen0Collections: 12, Gen1Collections: 2, Gen2Collections: 1, AllocatedBytes: 1_500_000);

        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);

        var fields = Heartbeats(logger)[0].Fields;
        Assert.Equal(2, Field(fields, "GcCollections"));
        Assert.Equal(2, Field(fields, "GcGen0"));
        Assert.Null(Field(fields, "GcGen1"));
        Assert.Null(Field(fields, "GcGen2"));
        Assert.Equal(500_000L, Field(fields, "GcAllocatedBytes"));
        var (level, _, gcFields) = logger.Events.Single(entry => string.Equals(entry.Name, "gc.collected", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal(2, Field(gcFields, "Gen0"));
        Assert.Null(Field(gcFields, "Gen1"));
        Assert.Null(Field(gcFields, "Gen2"));
        Assert.Equal(2, Field(gcFields, "SinceStart"));
    }

    [Fact]
    public async Task GcCollectionWarnFiresOnlyOnTheTickThatObservesNewCollections()
    {
        var logger = new RecordingLogger();
        var gc = new MutableGcSnapshotSource { Current = new(Gen0Collections: 5, Gen1Collections: 0, Gen2Collections: 0, AllocatedBytes: 0) };
        await using var heartbeat = new RuntimeHeartbeat(
            logger, gcSnapshotProvider: gc.Read, counters: new RuntimeCounters(), interval: s_tick);
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
                if (2.Equals(Field(beats[i].Fields, "GcCollections")))
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
        Assert.Null(Field(beats[0].Fields, "GcCollections"));
        Assert.Equal(2, Field(beats[warnedIndex].Fields, "GcCollections"));
        Assert.Equal(2, Field(beats[warnedIndex + 1].Fields, "GcCollections"));
        var (_, _, fields) = logger.Events.Single(entry => string.Equals(entry.Name, "gc.collected", StringComparison.Ordinal));
        Assert.Equal(2, Field(fields, "Gen0"));
        Assert.Equal(2, Field(fields, "SinceStart"));
    }

    [Fact]
    public async Task HeartbeatReportsAggregatePoolOccupancyFromRegisteredPools()
    {
        var logger = new RecordingLogger();
        var counters = new RuntimeCounters();
        counters.RegisterPool("frame");
        counters.RecordPoolRent("frame");
        counters.RecordPoolRent("frame");
        counters.RecordPoolRent("frame");
        var gc = new MutableGcSnapshotSource();
        await using var heartbeat = new RuntimeHeartbeat(
            logger, gcSnapshotProvider: gc.Read, counters: counters, interval: s_tick);
        heartbeat.Start();

        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 1).ConfigureAwait(false);
        var first = Heartbeats(logger)[0];
        Assert.Equal(1, Field(first.Fields, "Pools"));
        Assert.Equal(3L, Field(first.Fields, "PoolOccupancy"));
        // The rents predate the startup counter snapshot, so tick 1 reports no rent delta.
        Assert.DoesNotContain(RuntimeCounters.PoolRentedKey("frame"), Deltas(first).Keys);

        counters.RecordPoolReturn("frame");
        // A later tick may snapshot before the return lands under scheduler load; find the beat
        // that provably observed it instead of assuming it is the second beat.
        var returnIndex = -1;
        await AsyncTestExtensions.WaitForAsync(() =>
        {
            var beats = Heartbeats(logger);
            for (var i = 1; i < beats.Count; i++)
            {
                if (1L.Equals(Deltas(beats[i]).GetValueOrDefault(RuntimeCounters.PoolReturnedKey("frame"))))
                {
                    returnIndex = i;
                    return true;
                }
            }
            return false;
        }).ConfigureAwait(false);
        var second = Heartbeats(logger)[returnIndex];
        Assert.Equal(1, Field(second.Fields, "Pools"));
        Assert.Equal(2L, Field(second.Fields, "PoolOccupancy"));
        Assert.Equal(1L, Deltas(second)[RuntimeCounters.PoolReturnedKey("frame")]);
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
