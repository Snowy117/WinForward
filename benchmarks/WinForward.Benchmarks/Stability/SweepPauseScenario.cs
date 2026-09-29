using System.Diagnostics;
using System.Globalization;
using WinForward.Core;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The flow table's stop-the-world sweep, measured as the pause it imposes on packet processing
/// (research F3). A sweeper thread repeatedly fills the table with already-expired states and sweeps
/// them with the production call — <see cref="FlowTable.RemoveExpired"/> plus the same "is this flow
/// still held?" predicate the coordinator passes — while observer threads resolve live keys as fast as
/// they can and record how long each resolve took.
/// <para>
/// The metric the research names is the <em>maximum</em> time a resolve blocks during a sweep tick, so
/// the observer side keeps an exact maximum and counts of pauses over 0.1 / 0.5 / 1 / 5 ms rather than
/// sampling percentiles: a sampled distribution can miss the one pause that matters, and this probe
/// exists to catch exactly that one.
/// </para>
/// <para>
/// The live observer keys are TCP and the swept population is UDP, which is what lets the hold
/// predicate be a static protocol test: no allocation, no lookup, and the observer set survives every
/// sweep. The verdict row is report-only with the research's 0.5 ms line recorded as the target, and it
/// carries the sweep's own allocation (which must stay zero — the xunit gate asserts that exactly).
/// </para>
/// </summary>
internal static class SweepPauseScenario
{
    /// <summary>The research's seeded cardinality; <c>--flows</c> can raise it but not lower it, because a small table cannot exhibit the scan this probe measures.</summary>
    private const int MinimumFlows = 65_536;

    /// <summary>Live keys the observers resolve; large enough that the table cannot hold them all in one cache line.</summary>
    private const int ObserverFlows = 4_096;

    /// <summary>Observer key ports, kept clear of the identifiers the swept population uses.</summary>
    private const int ObserverPortFloor = 40_000;

    /// <summary>Claims in the claim-cost loop (the row R2 could not express in BenchmarkDotNet).</summary>
    private const int ClaimFlows = 4_096;

    /// <summary>Sweeps whose allocation is sampled: the first tick also pays the table's own population growth, so one sample would not be the sweep.</summary>
    private const int AllocationSampleTarget = 8;

    private const int WarmupResolves = 100_000;

    private static readonly TimeSpan s_idleTimeout = TimeSpan.FromSeconds(30);

    public static Task RunAsync(StabilityContext context, SoakOptions options)
    {
        var flows = Math.Max(options.Flows, MinimumFlows);
        var table = new FlowTable(capacity: flows + ObserverFlows + 1_024);
        var observerKeys = BuildObserverKeys();
        foreach (var key in observerKeys) Claim(table, key);

        MeasureClaimCost(context, flows);
        MeasureSweepPause(context, table, BuildSweptKeys(flows), observerKeys, options);
        return Task.CompletedTask;
    }

    /// <summary>
    /// One claim loop on a fresh table: the insert path (two indexes plus a state rent) at the live
    /// cardinality the sweeps maintain. Owned here rather than in a BenchmarkDotNet row because the
    /// row cannot reset the table between invocations and would silently measure resolves instead.
    /// </summary>
    private static void MeasureClaimCost(StabilityContext context, int cardinality)
    {
        var warm = new FlowTable(capacity: ClaimFlows + 1_024);
        for (var index = 0; index < ClaimFlows / 8; index++) Claim(warm, BenchmarkShared.CreateFlowKey(2_000_000 + index));

        var table = new FlowTable(capacity: ClaimFlows + 1_024);
        var keys = new FlowKey[ClaimFlows];
        for (var index = 0; index < ClaimFlows; index++) keys[index] = BenchmarkShared.CreateFlowKey(1_000_000 + index);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var claimed = 0;
        foreach (var key in keys)
        {
            if (table.TryClaimResolved(key, static () => FlowDecision.Fallback(FlowAction.Pass), out _)) claimed++;
        }

        var elapsed = Stopwatch.GetTimestamp() - started;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        context.WriteResult(
            "flowTable.claim",
            new { claims = ClaimFlows, cardinality },
            new
            {
                claimed,
                nanosecondsPerClaim = Round(StabilityShared.TicksToSeconds(elapsed) * 1_000_000_000 / ClaimFlows, 1),
                allocatedBytesPerClaim = Round((double)allocated / ClaimFlows, 2),
            });
    }

    /// <summary>
    /// The pause probe: one sweeper thread filling and sweeping, one observer thread per
    /// <c>--tcp-concurrency</c>, all released from a common gate so they share one observation window.
    /// Reports the sweep's own wall time and allocation plus the observer-side maximum and thresholds.
    /// </summary>
    private static void MeasureSweepPause(StabilityContext context, FlowTable table, FlowKey[] sweptKeys, FlowKey[] observerKeys, SoakOptions options)
    {
        var observers = Math.Max(1, options.TcpConcurrency);
        var observation = new ObserverResult[observers];
        var sweeps = new SweepStats[1];
        var signal = new StopSignal();
        var gate = new ManualResetEventSlim(initialState: false);
        var threads = new Thread[observers + 1];
        for (var index = 0; index < observers; index++)
        {
            var observer = index;
            // ReSharper disable once AccessToDisposedClosure // every thread is joined before the gate is disposed below.
            threads[observer] = new Thread(() => observation[observer] = Observe(table, observerKeys, gate, signal))
            {
                IsBackground = true,
                Name = string.Create(CultureInfo.InvariantCulture, $"sweep-observer-{observer}"),
            };
        }

        // ReSharper disable once AccessToDisposedClosure // the sweeper is joined with the observers before the gate is disposed.
        threads[observers] = new Thread(() => sweeps[0] = SweepLoop(table, sweptKeys, gate, signal, options.DurationSeconds))
        {
            IsBackground = true,
            Name = "sweep-sweeper",
        };

        foreach (var thread in threads) thread.Start();
        gate.Set();
        foreach (var thread in threads) thread.Join();
        gate.Dispose();

        var sweep = sweeps[0];
        var results = new
        {
            sweep.Sweeps,
            removedPerSweep = sweptKeys.Length,
            sweepMeanMs = Round(StabilityShared.TicksToMilliseconds(sweep.TotalTicks) / Math.Max(1, sweep.Sweeps), 3),
            sweepMaxMs = Round(StabilityShared.TicksToMilliseconds(sweep.MaxTicks), 3),
            sweepAllocatedBytesPerSweep = sweep.AllocatedBytes / Math.Max(1, sweep.AllocationSamples),
            observers,
            resolves = observation.Sum(result => result.Resolves),
            maxPauseMs = Round(StabilityShared.TicksToMilliseconds(observation.Max(result => result.MaxTicks)), 4),
            pausesOver100us = observation.Sum(result => result.Over100Microseconds),
            pausesOver500us = observation.Sum(result => result.Over500Microseconds),
            pausesOver1ms = observation.Sum(result => result.Over1Millisecond),
            pausesOver5ms = observation.Sum(result => result.Over5Milliseconds),
            targetMaxPauseMs = 0.5,
            gated = false,
            note = "Report-only pause probe (design §3): the research's 0.5 ms line is recorded as a target, not enforced, because a loaded host can inflate a single pause. The exact gate is the sweep's zero allocation, asserted in SweepAllocationGateTests.",
        };
        context.WriteResult("flowTable.sweepPause", new { flows = sweptKeys.Length, observerFlows = ObserverFlows, observers, durationSeconds = options.DurationSeconds }, results);
        context.WriteResult("flowTable.sweepPause.verdict", new { flows = sweptKeys.Length, observers, durationSeconds = options.DurationSeconds }, results);
    }

    /// <summary>
    /// The sweeper: refill with states already past the idle timeout, sweep them, repeat until the
    /// window closes. The refill is deliberately outside the measured region — the pause under test is
    /// the scan and removal, not the population churn that precedes it — and a sweep that fails to
    /// remove every expired flow aborts the run rather than reporting a pause for less work.
    /// </summary>
    private static SweepStats SweepLoop(FlowTable table, FlowKey[] sweptKeys, ManualResetEventSlim gate, StopSignal signal, int durationSeconds)
    {
        gate.Wait();
        var stats = new SweepStats();
        var watch = Stopwatch.StartNew();
        while (!signal.Stopped)
        {
            foreach (var key in sweptKeys) Claim(table, key);
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();

            // The claim stamps the state with the wall clock (FlowState.Reset), so "expired" is
            // expressed by moving the sweep's own notion of now past the idle timeout rather than by
            // faking a clock the table does not consult for activity.
            var removed = table.RemoveExpired(DateTimeOffset.UtcNow.Add(s_idleTimeout).AddSeconds(1), s_idleTimeout, static key => key.Protocol == TransportProtocol.Tcp);
            var elapsed = Stopwatch.GetTimestamp() - started;
            if (removed != sweptKeys.Length) throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The sweep removed {removed} of {sweptKeys.Length} expired flows."));
            stats.Record(elapsed, GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
            if (watch.Elapsed.TotalSeconds >= durationSeconds) signal.Stop();
        }

        signal.Stop();
        return stats;
    }

    /// <summary>
    /// One observer loop. Every resolve is timestamped; the loop keeps the exact maximum and counts how
    /// often a resolve crossed each pause threshold, so a single stop-the-world tick cannot be averaged
    /// away. A resolve that misses would mean the hold predicate let the observer set expire — fatal to
    /// the measurement, so it aborts the run instead of reporting a plausible number.
    /// </summary>
    private static ObserverResult Observe(FlowTable table, FlowKey[] keys, ManualResetEventSlim gate, StopSignal signal)
    {
        gate.Wait();

        // Warm the JIT before any pause is attributed to the sweep: a cold first resolve would
        // otherwise be reported as the maximum pause.
        for (var warm = 0; warm < WarmupResolves; warm++) _ = table.TryResolve(keys[warm % keys.Length], out _);

        var hundred = Stopwatch.Frequency / 10_000;
        var fiveHundred = Stopwatch.Frequency / 2_000;
        var oneMs = Stopwatch.Frequency / 1_000;
        var fiveMs = Stopwatch.Frequency / 200;
        var resolves = 0L;
        var maxTicks = 0L;
        var over100 = 0L;
        var over500 = 0L;
        var over1ms = 0L;
        var over5ms = 0L;
        var cursor = 0;
        while (!signal.Stopped)
        {
            var key = keys[cursor];
            cursor++;
            if (cursor == keys.Length) cursor = 0;
            var started = Stopwatch.GetTimestamp();
            if (!table.TryResolve(key, out _)) throw new InvalidOperationException("The observer's live key expired: the sweep's hold predicate is not protecting the observer set.");
            var elapsed = Stopwatch.GetTimestamp() - started;
            resolves++;
            if (elapsed > maxTicks) maxTicks = elapsed;
            if (elapsed > hundred) over100++;
            if (elapsed > fiveHundred) over500++;
            if (elapsed > oneMs) over1ms++;
            if (elapsed > fiveMs) over5ms++;
        }

        return new ObserverResult(resolves, maxTicks, over100, over500, over1ms, over5ms);
    }

    /// <summary>Artifact rounding: the analyzer requires an explicit midpoint mode, and ToEven is the runtime's own default.</summary>
    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.ToEven);

    private static void Claim(FlowTable table, FlowKey key)
    {
        if (!table.TryClaimResolved(key, static () => FlowDecision.Fallback(FlowAction.Pass), out _))
        {
            throw new InvalidOperationException("The sweep probe could not claim a key it expected to be absent.");
        }
    }

    private static FlowKey[] BuildSweptKeys(int flows)
    {
        var keys = new FlowKey[flows];
        for (var index = 0; index < flows; index++) keys[index] = BenchmarkShared.CreateFlowKey(index);
        return keys;
    }

    private static FlowKey[] BuildObserverKeys()
    {
        var keys = new FlowKey[ObserverFlows];
        for (var index = 0; index < ObserverFlows; index++) keys[index] = BenchmarkShared.CreateTcpFlowKey(checked((ushort)(ObserverPortFloor + index)));
        return keys;
    }

    private sealed class SweepStats
    {
        public int Sweeps { get; private set; }

        public long TotalTicks { get; private set; }

        public long MaxTicks { get; private set; }

        public long AllocatedBytes { get; private set; }

        public int AllocationSamples { get; private set; }

        public void Record(long ticks, long allocated)
        {
            Sweeps++;
            TotalTicks += ticks;
            MaxTicks = Math.Max(MaxTicks, ticks);

            // The first tick also pays the table's own population growth, which is not the sweep.
            if (Sweeps > 1 && AllocationSamples < AllocationSampleTarget)
            {
                AllocatedBytes += allocated;
                AllocationSamples++;
            }
        }
    }

    private sealed record ObserverResult(long Resolves, long MaxTicks, long Over100Microseconds, long Over500Microseconds, long Over1Millisecond, long Over5Milliseconds);

    /// <summary>Shared stop flag: a captured local cannot be read through <see cref="Volatile"/> by reference, and a plain bool field would race.</summary>
    private sealed class StopSignal
    {
        private int _stopped;

        public bool Stopped => Volatile.Read(ref _stopped) != 0;

        public void Stop() => Volatile.Write(ref _stopped, 1);
    }
}
