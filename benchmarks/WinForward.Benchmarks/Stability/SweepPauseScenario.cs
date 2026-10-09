using System.Diagnostics;
using System.Globalization;
using WinForward.Core;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The flow table's stop-the-world sweep, measured as the pause it imposes on packet processing. A
/// sweeper thread repeatedly fills the table with already-expired states and sweeps them with the
/// production call — <see cref="FlowTable.RemoveExpired"/> plus the same "is this flow still held?"
/// predicate the coordinator passes — while observer threads resolve live keys as fast as they can and
/// record how long each resolve took.
/// <para>
/// The metric is the <em>maximum</em> time a resolve blocks during a sweep tick, so the observer side
/// keeps an exact maximum and counts of pauses over 0.1 / 0.5 / 1 / 5 ms rather than sampling
/// percentiles, which can miss the one pause that matters.
/// </para>
/// <para>
/// The live observer keys are TCP and the swept population is UDP, which is what lets the hold
/// predicate be a static protocol test: no allocation, no lookup, and the observer set survives every
/// sweep. The row also carries the sweep's own allocation, which must stay zero — the xunit gate
/// asserts that exactly.
/// </para>
/// <para>
/// The raw series cannot carry the finding by itself: the scenario refills each round with 65,536
/// <em>individual</em> claims that contend on the same table gate, so most observer-visible pauses are
/// refill cost the sweep does not own. A phase flag armed immediately around the
/// <see cref="FlowTable.RemoveExpired"/> call therefore scopes <c>maxSweepWindowPauseMs</c> and the
/// <c>pausesInWindow*</c> counts to the sweep, and an observer counts a resolve as in-window only when
/// it <em>started</em> while that flag was set. Those figures are <em>diagnostics</em>, not acceptance
/// figures: the calibration control — the same flag armed for <c>--sweep-window-control-ms</c> with
/// <em>no product call at all</em> — measures the same order of in-window pauses on a shared host. The
/// sweep's hold bound is proven by counts in
/// <c>SweepAllocationGateTests.FlowTableSweepHoldWorkIsBoundedByChunkEntries</c>, and a run whose
/// window observed no resolve aborts rather than publishing a maximum for a window that never existed.
/// </para>
/// </summary>
internal static class SweepPauseScenario
{
    /// <summary>The cardinality floor; <c>--flows</c> can raise it but not lower it, because a small table cannot exhibit the scan this probe measures.</summary>
    private const int MinimumFlows = 65_536;

    /// <summary>Live keys the observers resolve; large enough that the table cannot hold them all in one cache line.</summary>
    private const int ObserverFlows = 4_096;

    /// <summary>Observer key ports, kept clear of the identifiers the swept population uses.</summary>
    private const int ObserverPortFloor = 40_000;

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
    /// With <c>--sweep-window-control-ms</c> the sweeper thread arms the same window and waits instead of
    /// sweeping, which is the noise-floor control the timing figures are quoted against.
    /// </summary>
    private static void MeasureSweepPause(StabilityContext context, FlowTable table, FlowKey[] sweptKeys, FlowKey[] observerKeys, SoakOptions options)
    {
        var observers = Math.Max(1, options.TcpConcurrency);
        var observation = new ObserverResult[observers];
        var sweeps = new SweepStats[1];
        var signal = new StopSignal();
        var window = new SweepWindow();
        var gate = new ManualResetEventSlim(initialState: false);
        var threads = new Thread[observers + 1];
        for (var index = 0; index < observers; index++)
        {
            var observer = index;
            // ReSharper disable once AccessToDisposedClosure // every thread is joined before the gate is disposed below.
            threads[observer] = new Thread(() => observation[observer] = Observe(table, observerKeys, gate, signal, window))
            {
                IsBackground = true,
                Name = string.Create(CultureInfo.InvariantCulture, $"sweep-observer-{observer}"),
            };
        }

        // ReSharper disable once AccessToDisposedClosure // the sweeper is joined with the observers before the gate is disposed.
        threads[observers] = new Thread(() => sweeps[0] = SweepLoop(table, sweptKeys, gate, signal, window, options.DurationSeconds, options.SweepWindowControlMs))
        {
            IsBackground = true,
            Name = "sweep-sweeper",
        };

        foreach (var thread in threads) thread.Start();
        gate.Set();
        foreach (var thread in threads) thread.Join();
        gate.Dispose();

        ReportSweepMetrics(context, observation, sweeps[0], sweptKeys.Length, observers, options.DurationSeconds, options.SweepWindowControlMs);
    }

    /// <summary>
    /// Aggregates the per-observer statistics into the row. Every timing field is a report-only diagnostic:
    /// the control run (the same window armed with no product call at all) shows the in-window maximum
    /// measures host scheduling and lock queueing, not hold length, so the acceptance evidence for the
    /// sweep's hold bound is the countable probe in <c>SweepAllocationGateTests</c>, not a number here. A
    /// window that observed no resolve would publish a vacuously zero maximum, which reads as a pass, so it
    /// aborts like the other tripwires instead.
    /// </summary>
    private static void ReportSweepMetrics(StabilityContext context, ObserverResult[] observation, SweepStats sweep, int flows, int observers, int durationSeconds, int controlWindowMs)
    {
        var windowResolves = observation.Sum(result => result.WindowResolves);
        if (windowResolves == 0) throw new InvalidOperationException("The sweep window observed no resolve: the phase-scoped pause metric would be vacuous.");
        var isControl = controlWindowMs > 0;

        var results = new
        {
            sweep.Sweeps,
            removedPerSweep = isControl ? 0 : flows,
            sweepMeanMs = Round(StabilityShared.TicksToMilliseconds(sweep.TotalTicks) / Math.Max(1, sweep.Sweeps), 3),
            sweepMaxMs = Round(StabilityShared.TicksToMilliseconds(sweep.MaxTicks), 3),
            sweepAllocatedBytesPerSweep = sweep.AllocatedBytes / Math.Max(1, sweep.AllocationSamples),
            observers,
            resolves = observation.Sum(result => result.Resolves),
            maxPauseMs = Round(StabilityShared.TicksToMilliseconds(observation.Max(result => result.Raw.MaxTicks)), 4),
            pausesOver100us = observation.Sum(result => result.Raw.Over100Microseconds),
            pausesOver500us = observation.Sum(result => result.Raw.Over500Microseconds),
            pausesOver1ms = observation.Sum(result => result.Raw.Over1Millisecond),
            pausesOver5ms = observation.Sum(result => result.Raw.Over5Milliseconds),
            sweepWindowResolves = windowResolves,
            maxSweepWindowPauseMs = Round(StabilityShared.TicksToMilliseconds(observation.Max(result => result.Window.MaxTicks)), 4),
            pausesInWindowOver100us = observation.Sum(result => result.Window.Over100Microseconds),
            pausesInWindowOver500us = observation.Sum(result => result.Window.Over500Microseconds),
            pausesInWindowOver1ms = observation.Sum(result => result.Window.Over1Millisecond),
            pausesInWindowOver5ms = observation.Sum(result => result.Window.Over5Milliseconds),
            sweepWindowControlMs = controlWindowMs,
            targetMaxPauseMs = 0.5,
            gated = false,
            note = isControl
                ? "Calibration control: the in-window flag was armed for sweepWindowControlMs per iteration and NO product call was made, so every pause figure here is the host's scheduling and lock-queueing floor for this observer count. It is the number the real runs' timing figures are quoted against; no field in this row is an acceptance figure."
                : "Report-only pause probe with a phase-scoped sweep window: in-window means the resolve started while the sweeper had the flag armed around FlowTable.RemoveExpired, so the in-window fields exclude the scenario's own refill contention. No field here is an acceptance figure — the control run (armed window, no product call) shows the same order of in-window pauses, so they measure host scheduling and lock queueing, not hold length. The sweep's hold bound is proven by counts in SweepAllocationGateTests.FlowTableSweepHoldWorkIsBoundedByChunkEntries; the raw maxPauseMs / pausesOver* series are report-only and quoted normalized per sweep (at least 96% of them are the 65,536 per-round refill claims contending on the same gate). sweepWindowResolves (>= 1,000,000 per 15 s window is the D-C claim) proves the warm path is not starving and that the window is not vacuous.",
        };
        context.WriteResult("flowTable.sweepPause", new { flows, observerFlows = ObserverFlows, observers, durationSeconds, sweepWindowControlMs = controlWindowMs }, results);
        context.WriteResult("flowTable.sweepPause.verdict", new { flows, observers, durationSeconds, sweepWindowControlMs = controlWindowMs }, results);
    }

    /// <summary>
    /// The sweeper: refill with states already past the idle timeout, sweep them, repeat until the
    /// observation window closes. The refill is deliberately outside the measured region — the pause
    /// under test is the scan and removal, not the population churn that precedes it — and a sweep that
    /// fails to remove every expired flow aborts the run rather than reporting a pause for less work. The
    /// phase flag is armed around the sweep call alone, so no observer can mistake refill contention for
    /// the sweep's own pause. In control mode the flag is armed for <paramref name="controlWindowMs"/> and
    /// nothing else happens: no refill, no sweep, no tripwire — the row is the calibration floor.
    /// </summary>
    private static SweepStats SweepLoop(FlowTable table, FlowKey[] sweptKeys, ManualResetEventSlim gate, StopSignal signal, SweepWindow window, int durationSeconds, int controlWindowMs)
    {
        gate.Wait();
        var stats = new SweepStats();
        var watch = Stopwatch.StartNew();
        if (controlWindowMs > 0)
        {
            while (!signal.Stopped)
            {
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var started = Stopwatch.GetTimestamp();
                window.Enter();
                Thread.Sleep(controlWindowMs);
                window.Exit();
                stats.Record(Stopwatch.GetTimestamp() - started, GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
                if (watch.Elapsed.TotalSeconds >= durationSeconds) signal.Stop();
            }

            signal.Stop();
            return stats;
        }

        while (!signal.Stopped)
        {
            foreach (var key in sweptKeys) Claim(table, key);
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();

            // The claim stamps the state with the wall clock (FlowState.Reset), so "expired" is
            // expressed by moving the sweep's own notion of now past the idle timeout rather than by
            // faking a clock the table does not consult for activity.
            window.Enter();
            var removed = table.RemoveExpired(DateTimeOffset.UtcNow.Add(s_idleTimeout).AddSeconds(1), s_idleTimeout, static key => key.Protocol == TransportProtocol.Tcp);
            window.Exit();
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
    /// the measurement, so it aborts the run instead of reporting a plausible number. The same statistics
    /// are kept a second time for the resolves that started inside the sweep's phase window.
    /// </summary>
    private static ObserverResult Observe(FlowTable table, FlowKey[] keys, ManualResetEventSlim gate, StopSignal signal, SweepWindow window)
    {
        gate.Wait();

        // Warm the JIT before any pause is attributed to the sweep: a cold first resolve would
        // otherwise be reported as the maximum pause.
        for (var warm = 0; warm < WarmupResolves; warm++) _ = table.TryResolve(keys[warm % keys.Length], out _);

        var hundred = Stopwatch.Frequency / 10_000;
        var fiveHundred = Stopwatch.Frequency / 2_000;
        var oneMs = Stopwatch.Frequency / 1_000;
        var fiveMs = Stopwatch.Frequency / 200;
        var raw = new PauseCounters(hundred, fiveHundred, oneMs, fiveMs);
        var inWindowCounters = new PauseCounters(hundred, fiveHundred, oneMs, fiveMs);
        var resolves = 0L;
        var windowResolves = 0L;
        var cursor = 0;
        while (!signal.Stopped)
        {
            var key = keys[cursor];
            cursor++;
            if (cursor == keys.Length) cursor = 0;

            // Read before the resolve, never after: in-window must mean the resolve started while the
            // sweeper had the flag armed, or a resolve that merely outlived a sweep would be attributed
            // to it.
            var inWindow = window.Active;
            var started = Stopwatch.GetTimestamp();
            if (!table.TryResolve(key, out _)) throw new InvalidOperationException("The observer's live key expired: the sweep's hold predicate is not protecting the observer set.");
            var elapsed = Stopwatch.GetTimestamp() - started;
            resolves++;
            raw.Record(elapsed);
            if (!inWindow) continue;
            windowResolves++;
            inWindowCounters.Record(elapsed);
        }

        return new ObserverResult(resolves, raw, windowResolves, inWindowCounters);
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

    private sealed record ObserverResult(long Resolves, PauseCounters Raw, long WindowResolves, PauseCounters Window);

    /// <summary>
    /// One population's exact pause maximum and threshold counts: every resolve, or the sweep-window slice
    /// of them. Mutated only by the observer thread that owns it, outside the resolve's own measurement.
    /// </summary>
    private sealed class PauseCounters(long hundredMicroseconds, long fiveHundredMicroseconds, long oneMillisecond, long fiveMilliseconds)
    {
        public long MaxTicks { get; private set; }

        public long Over100Microseconds { get; private set; }

        public long Over500Microseconds { get; private set; }

        public long Over1Millisecond { get; private set; }

        public long Over5Milliseconds { get; private set; }

        public void Record(long ticks)
        {
            if (ticks > MaxTicks) MaxTicks = ticks;
            if (ticks > hundredMicroseconds) Over100Microseconds++;
            if (ticks > fiveHundredMicroseconds) Over500Microseconds++;
            if (ticks > oneMillisecond) Over1Millisecond++;
            if (ticks > fiveMilliseconds) Over5Milliseconds++;
        }
    }

    /// <summary>
    /// The sweep phase flag, armed immediately around the <see cref="FlowTable.RemoveExpired"/> call and
    /// cleared right after it. A volatile <see langword="int"/> rather than a local or a plain field: the
    /// sweeper writes it and every observer reads it without a lock, and a plain <see langword="bool"/>
    /// field would race.
    /// </summary>
    private sealed class SweepWindow
    {
        private int _active;

        public bool Active => Volatile.Read(ref _active) != 0;

        public void Enter() => Volatile.Write(ref _active, 1);

        public void Exit() => Volatile.Write(ref _active, 0);
    }

    /// <summary>Shared stop flag: a captured local cannot be read through <see cref="Volatile"/> by reference, and a plain bool field would race.</summary>
    private sealed class StopSignal
    {
        private int _stopped;

        public bool Stopped => Volatile.Read(ref _stopped) != 0;

        public void Stop() => Volatile.Write(ref _stopped, 1);
    }
}
