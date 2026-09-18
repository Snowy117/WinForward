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
        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 2).ConfigureAwait(false);

        var beats = Heartbeats(logger);
        Assert.Null(Field(beats[0].Fields, RuntimeCounters.RelaySetupFailed));
        Assert.Equal(3L, Field(beats[^1].Fields, RuntimeCounters.RelaySetupFailed));
        Assert.Equal(1L, Field(beats[^1].Fields, RuntimeCounters.PassReinjectFailed));
        // A counter with no new hits in the interval stays absent rather than reporting a zero delta.
        counters.Increment(RuntimeCounters.RelaySetupFailed);
        await AsyncTestExtensions.WaitForAsync(() => Heartbeats(logger).Count >= 3).ConfigureAwait(false);
        var third = Heartbeats(logger)[^1].Fields;
        Assert.Equal(1L, Field(third, RuntimeCounters.RelaySetupFailed));
        Assert.Null(Field(third, RuntimeCounters.PassReinjectFailed));
    }

    [Fact]
    public async Task IdleHeartbeatOmitsEveryZeroValuedField()
    {
        var logger = new RecordingRuntimeLogger();
        await using var heartbeat = new RuntimeHeartbeat(logger, counters: new RuntimeCounters(), interval: s_tick);
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
}
