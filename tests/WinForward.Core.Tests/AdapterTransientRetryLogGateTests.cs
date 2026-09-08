using WinForward.Configuration;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The transient-retry warn gate's window semantics: the first retry logs a structured
/// <c>adapter.retry</c> event, retries inside the 5 s window are suppressed without extending the
/// window, and a retry at or past the window edge logs again. The fake ticks source makes the
/// clock advance deterministically — no real sleeps.
/// </summary>
public sealed class AdapterTransientRetryLogGateTests
{
    private static readonly long FiveSeconds = TimeSpan.FromSeconds(5).Ticks;

    private sealed class ScriptedClock
    {
        // Any start >= one window keeps the gate's cold-start semantics (first call logs) that
        // the real clock gets from DateTime.UtcNow.Ticks being far from zero.
        private long _ticks = FiveSeconds;
        public Func<long> Provider => () => _ticks;
        public void AdvanceTo(long ticks) => _ticks = ticks;
    }

    private static (AdapterTransientRetryLogGate Gate, ScriptedClock Clock, RecordingRuntimeLogger Logger) CreateGate()
    {
        var logger = new RecordingRuntimeLogger();
        var clock = new ScriptedClock();
        return (new AdapterTransientRetryLogGate(logger, clock.Provider), clock, logger);
    }

    [Fact]
    public void FirstRetryLogsTheStructuredAdapterEvent()
    {
        var (gate, _, logger) = CreateGate();

        gate.Log("eth0", "Ethernet", 21, 1);

        var logged = Assert.Single(logger.Events);
        Assert.Equal(RuntimeLogLevel.Warn, logged.Level);
        Assert.Equal("adapter.retry", logged.Name);
        Assert.Equal("eth0", logged.Fields.Single(field => string.Equals(field.Key, "adapter", StringComparison.Ordinal)).Value);
        Assert.Equal("Ethernet", logged.Fields.Single(field => string.Equals(field.Key, "name", StringComparison.Ordinal)).Value);
        Assert.Equal(21, logged.Fields.Single(field => string.Equals(field.Key, "nativeError", StringComparison.Ordinal)).Value);
        Assert.Equal(1, logged.Fields.Single(field => string.Equals(field.Key, "attempt", StringComparison.Ordinal)).Value);
    }

    [Fact]
    public void RetriesInsideTheWindowAreSuppressed()
    {
        var (gate, clock, logger) = CreateGate();

        clock.AdvanceTo(FiveSeconds);
        gate.Log("eth0", "Ethernet", 21, 1);
        clock.AdvanceTo(FiveSeconds + TimeSpan.FromSeconds(4).Ticks);
        gate.Log("eth0", "Ethernet", 21, 2);
        clock.AdvanceTo(2 * FiveSeconds - 1);
        gate.Log("eth1", "Wi-Fi", 21, 1);

        Assert.Single(logger.Events);
    }

    [Fact]
    public void RetryAtTheWindowEdgeLogsAgain()
    {
        var (gate, clock, logger) = CreateGate();

        clock.AdvanceTo(FiveSeconds);
        gate.Log("eth0", "Ethernet", 21, 1);
        // Exactly one full window later is outside the suppression window (< is strict).
        clock.AdvanceTo(2 * FiveSeconds);
        gate.Log("eth0", "Ethernet", 21, 2);

        Assert.Equal(2, logger.Events.Count);
        Assert.Equal(2, logger.Events[1].Fields.Single(field => string.Equals(field.Key, "attempt", StringComparison.Ordinal)).Value);
    }

    [Fact]
    public void SuppressedRetriesDoNotExtendTheWindow()
    {
        var (gate, clock, logger) = CreateGate();

        clock.AdvanceTo(FiveSeconds);
        gate.Log("eth0", "Ethernet", 21, 1);
        // A suppressed retry at +1 s must not become the new window anchor.
        clock.AdvanceTo(FiveSeconds + TimeSpan.FromSeconds(1).Ticks);
        gate.Log("eth0", "Ethernet", 21, 2);
        clock.AdvanceTo(2 * FiveSeconds);
        gate.Log("eth0", "Ethernet", 21, 3);

        Assert.Equal(2, logger.Events.Count);
    }
}
