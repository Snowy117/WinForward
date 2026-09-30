using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinForward.Core;
using WinForward.Runtime;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// Contention and scaling probe for the global lock chain (research F2). One shared flow table plus
/// one self-traffic registry, driven by 1/2/4 worker threads that resolve mostly disjoint keys through
/// the operations a warm packet pays. The claim under test is the scaling ratio
/// <c>throughput(N) / (N × throughput(1))</c>: every process-wide lock on the warm path pushes the
/// ratio below 1.
/// <para>
/// Three arms: <c>fake</c> (<see cref="NeverOwnedGuard"/>, the guard shape the dispatcher benchmarks
/// use, plus the real gated resolve), <c>real</c> (a populated <see cref="SelfTrafficRegistry"/> and the
/// full <see cref="ISelfTrafficGuard.IsOwned"/>), and <c>warm</c> — the post-reorder production shape:
/// the same populated registry, the retained lock-free wildcard half
/// (<see cref="ISelfTrafficGuard.IsWildcardOwned"/>) and the lock-free cache probe
/// (<see cref="FlowTable.TryResolveWarm"/>) in place of the per-lookup exact-tuple check and the gated
/// resolve. The warm arm counts the probe's hits and misses, so its ratio is attributable to the
/// measured cache miss rate; the arm charges a miss only the failed probe (the production fallback is
/// the dispatcher's slow path, which is not part of this resolve-shaped unit). The fake arm
/// cannot show the reorder (it answers without touching a lock); the real arm is the pre-change
/// comparator. Only <c>warm</c> carries the acceptance figure, and its verdict row records both the
/// self-normalised ratio and the ratio against the recorded one-thread baseline, because the
/// self-normalised denominator is this run's own one-thread arm and therefore rises with the fix.
/// </para>
/// </summary>
internal static class ScalingContentionScenario
{
    /// <summary>Configurations the default sweep runs; <c>--threads N</c> pins one instead.</summary>
    private static readonly int[] s_threadSweep = [1, 2, 4];

    /// <summary>Self-traffic tuples in the real arm — the desktop shape (dozens, not thousands).</summary>
    private const int SelfTrafficTuples = 32;

    /// <summary>Lookups per batch; the batch keeps the clock check out of the inner loop.</summary>
    private const int BatchSize = 64;

    /// <summary>The recorded one-thread baseline of the pre-change <c>real</c> arm (resolutions/s).</summary>
    private const double RecordedOneThreadBaseline = 3_331_758.3;

    private static long s_sink;

    public static Task RunAsync(StabilityContext context, SoakOptions options)
    {
        var threads = options.Threads > 0 ? [options.Threads] : s_threadSweep;
        var sharedKeyPercent = Math.Clamp(options.SharedKeyPercent, 0, 50);
        var sharedPeriod = sharedKeyPercent == 0 ? int.MaxValue : Math.Max(1, 100 / sharedKeyPercent);

        var keys = BuildKeys(options.Flows);
        var contexts = new FlowContext[keys.Length];
        for (var index = 0; index < keys.Length; index++) contexts[index] = BenchmarkShared.CreateContext(keys[index]);
        var table = BuildTable(keys);
        var sharedCount = sharedKeyPercent == 0 ? 0 : Math.Clamp(options.Flows * sharedKeyPercent / 100, 1, keys.Length);

        // The registry's tuples are disjoint from every resolved key, so the measured shape is the
        // common warm case: the check probes and then answers "not ours".
        var registered = new SelfTrafficRegistry();
        for (var index = 0; index < SelfTrafficTuples; index++)
        {
            registered.Register(SelfTrafficRegistry.SelfTrafficKey.From(BenchmarkShared.CreateContext(BenchmarkShared.CreateFlowKey(options.Flows + 1_000 + index))));
        }

        var arms = new GuardArm[]
        {
            new(GuardArmKind.Fake, new NeverOwnedGuard(), WildcardOnly: false),
            new(GuardArmKind.Real, registered, WildcardOnly: false),
            new(GuardArmKind.Warm, registered, WildcardOnly: true),
        };
        // The window divisor counts every arm actually run: a two-arm constant silently shortens each
        // window as soon as an arm is added, which breaks comparability with the recorded series.
        var windowSeconds = Math.Max(1, options.DurationSeconds / (arms.Length * threads.Length));
        var results = new List<ArmResult>(arms.Length * threads.Length);
        foreach (var arm in arms)
        {
            foreach (var threadCount in threads)
            {
                var result = RunConfiguration(arm, table, keys, contexts, threadCount, sharedCount, sharedPeriod, windowSeconds);
                results.Add(result);
                context.WriteResult(
                    "scaling.contention",
                    new
                    {
                        selfTrafficGuard = Name(arm.Kind),
                        threads = threadCount,
                        flows = options.Flows,
                        sharedKeyCount = sharedCount,
                        sharedKeyPercent,
                        windowSeconds,
                    },
                    result.BuildMetrics());
            }
        }

        context.WriteResult(
            "scaling.contention.verdict",
            new { flows = options.Flows, sharedKeyPercent, threadSweep = threads, windowSeconds },
            BuildVerdict(results, threads));
        return Task.CompletedTask;
    }

    /// <summary>
    /// One configuration: <paramref name="arm"/>'s guard, <paramref name="threadCount"/> dedicated
    /// threads released from a common gate (so the window excludes thread-start skew), each walking its
    /// own key partition with every <paramref name="sharedPeriod"/>-th lookup aimed at the shared pool.
    /// </summary>
    private static ArmResult RunConfiguration(GuardArm arm, FlowTable table, FlowKey[] keys, FlowContext[] contexts, int threadCount, int sharedCount, int sharedPeriod, int windowSeconds)
    {
        var partitions = new int[threadCount][];
        for (var worker = 0; worker < threadCount; worker++) partitions[worker] = BuildPartition(keys.Length, threadCount, worker);

        var counts = new WorkerCounts[threadCount];
        var gate = new ManualResetEventSlim(initialState: false);
        var workers = new Thread[threadCount];
        for (var index = 0; index < threadCount; index++)
        {
            var worker = index;
            // ReSharper disable once AccessToDisposedClosure // every worker is joined before the gate is disposed below.
            workers[worker] = new Thread(() => counts[worker] = RunWorker(arm, table, keys, contexts, partitions[worker], sharedCount, sharedPeriod, windowSeconds, gate))
            {
                IsBackground = true,
                Name = string.Create(CultureInfo.InvariantCulture, $"scaling-contention-{worker}"),
            };
            workers[worker].Start();
        }

        var wall = Stopwatch.StartNew();
        gate.Set();
        foreach (var worker in workers) worker.Join();
        wall.Stop();
        gate.Dispose();
        Volatile.Write(ref s_sink, counts.Sum(static c => c.Resolutions));
        return new ArmResult(
            arm.Kind,
            threadCount,
            [.. counts.Select(static c => c.Resolutions)],
            [.. counts.Select(static c => c.CacheHits)],
            [.. counts.Select(static c => c.CacheMisses)],
            wall.Elapsed.TotalSeconds,
            windowSeconds);
    }

    private static WorkerCounts RunWorker(GuardArm arm, FlowTable table, FlowKey[] keys, FlowContext[] contexts, int[] partition, int sharedCount, int sharedPeriod, int windowSeconds, ManualResetEventSlim gate)
    {
        gate.Wait();
        long count = 0;
        long hits = 0;
        long misses = 0;
        var partitionCursor = 0;
        var sharedCursor = 0;
        var sinceShared = 0;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < windowSeconds)
        {
            for (var step = 0; step < BatchSize; step++)
            {
                // Every lookup is one unit of work (the recorded before-series counted the gated resolve
                // the same way), and the warm arm additionally splits that unit into cache hits/misses.
                LookupOutcome outcome;
                if (sharedCount > 0 && sinceShared == 0)
                {
                    outcome = Resolve(arm, table, keys[sharedCursor], contexts[sharedCursor]);
                    sharedCursor++;
                    if (sharedCursor == sharedCount) sharedCursor = 0;
                }
                else
                {
                    var key = partition[partitionCursor];
                    outcome = Resolve(arm, table, keys[key], contexts[key]);
                    partitionCursor++;
                    if (partitionCursor == partition.Length) partitionCursor = 0;
                }

                count++;
                if (outcome.WarmProbe)
                {
                    if (outcome.CacheHit) hits++;
                    else misses++;
                }

                if (sinceShared >= sharedPeriod) sinceShared = 0;
                else sinceShared++;
            }
        }

        return new WorkerCounts(count, hits, misses);
    }

    /// <summary>
    /// One lookup: the self-traffic check then the flow-table resolve — the serialized lock
    /// acquisitions and dictionary probes a warm packet pays (research F2's cost model). Inlined so the
    /// measurement is the collaborators, not the loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LookupOutcome Resolve(GuardArm arm, FlowTable table, in FlowKey key, in FlowContext context)
    {
        var owned = arm.WildcardOnly ? arm.Guard.IsWildcardOwned(context) : arm.Guard.IsOwned(context);
        if (owned) return default;
        if (!arm.WildcardOnly) return new LookupOutcome(WarmProbe: false, CacheHit: table.TryResolve(key, out _));
        var hit = table.TryResolveWarm(key, out _);
        return new LookupOutcome(WarmProbe: true, CacheHit: hit);
    }

    private static FlowKey[] BuildKeys(int flows)
    {
        var keys = new FlowKey[flows];
        for (var index = 0; index < flows; index++) keys[index] = BenchmarkShared.CreateFlowKey(index);
        return keys;
    }

    /// <summary>
    /// Worker <c>w</c>'s key partition: the indices congruent to <c>w</c> modulo the thread count, so no
    /// two workers walk the same key (the measurement is the table's contention, not one hot key). A
    /// partition always has at least one entry, and the cursor wraps on its length.
    /// </summary>
    private static int[] BuildPartition(int flows, int threadCount, int worker)
    {
        var indices = new List<int>(Math.Max(1, flows / threadCount));
        for (var index = worker; index < flows; index += threadCount) indices.Add(index);
        if (indices.Count == 0) indices.Add(worker % flows);
        return [.. indices];
    }

    /// <summary>
    /// Seeds one flow table with every key as a live state: the measured resolve is the hit path, and
    /// the table's size (hence its dictionary shape) matches the live population.
    /// </summary>
    private static FlowTable BuildTable(FlowKey[] keys)
    {
        var table = new FlowTable(capacity: keys.Length + 1_024);
        foreach (var key in keys)
        {
            if (!table.TryClaimResolved(key, static () => FlowDecision.Fallback(FlowAction.Pass), out _))
            {
                throw new InvalidOperationException("Unable to seed the scaling-contention flow table.");
            }
        }

        return table;
    }

    /// <summary>Artifact rounding: the analyzer requires an explicit midpoint mode, and ToEven is the runtime's own default.</summary>
    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.ToEven);

    private static string Name(GuardArmKind kind) => kind switch
    {
        GuardArmKind.Fake => "fake",
        GuardArmKind.Real => "real",
        _ => "warm",
    };

    private static object BuildVerdict(List<ArmResult> results, int[] threads)
    {
        return new
        {
            gated = false,
            note = "Report-only contention curve. Only the warm arm (populated registry, lock-free wildcard half, lock-free cache probe, no per-lookup exact IsOwned and no gated resolve) carries the acceptance figure; the fake arm answers without touching a lock and cannot show the reorder, and the real arm is the pre-change comparator. warmResolve records both the self-normalised ratio (denominator = this run's own one-thread arm, which rises with the fix) and ratioVersusRecordedBaseline against the recorded 3,331,758.3/s one-thread baseline, plus the measured cache miss rate so the ratio is attributable. A warm miss is charged only the failed probe; the production fallback is the dispatcher's slow path and is outside this resolve-shaped unit.",
            baselineThreads = threads[0],
            fakeGuard = Describe(GuardArmKind.Fake),
            realGuard = Describe(GuardArmKind.Real),
            warmResolve = DescribeWarm(),
            recordedOneThreadBaseline = RecordedOneThreadBaseline,
        };

        object Describe(GuardArmKind kind)
        {
            var arm = results.Where(result => result.Kind == kind).ToArray();
            return new
            {
                resolutionsPerSecond = arm.Select(result => Round(result.ResolutionsPerSecond, 1)).ToArray(),
                scalingRatio = arm.Select(result => Round(result.ResolutionsPerSecond / (result.Threads * arm[0].ResolutionsPerSecond), 3)).ToArray(),
                workerSpread = arm.Select(result => Round(result.WorkerSpread, 2)).ToArray(),
            };
        }

        object DescribeWarm()
        {
            var arm = results.Where(result => result.Kind == GuardArmKind.Warm).ToArray();
            return new
            {
                resolutionsPerSecond = arm.Select(result => Round(result.ResolutionsPerSecond, 1)).ToArray(),
                scalingRatio = arm.Select(result => Round(result.ResolutionsPerSecond / (result.Threads * arm[0].ResolutionsPerSecond), 3)).ToArray(),
                ratioVersusRecordedBaseline = arm.Select(result => Round(result.ResolutionsPerSecond / (result.Threads * RecordedOneThreadBaseline), 3)).ToArray(),
                lookups = arm.Select(static result => result.Lookups).ToArray(),
                cacheHits = arm.Select(static result => result.Hits.Sum()).ToArray(),
                cacheMisses = arm.Select(static result => result.Misses.Sum()).ToArray(),
                cacheMissRate = arm.Select(result => Round(result.MissRate, 5)).ToArray(),
                workerSpread = arm.Select(result => Round(result.WorkerSpread, 2)).ToArray(),
            };
        }
    }

    private enum GuardArmKind
    {
        Fake,
        Real,
        Warm,
    }

    private readonly record struct GuardArm(GuardArmKind Kind, ISelfTrafficGuard Guard, bool WildcardOnly);

    /// <summary>One worker's totals: lookups performed, and the warm arm's cache hit/miss split.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct WorkerCounts(long Resolutions, long CacheHits, long CacheMisses);

    /// <summary>One lookup's outcome: whether it was the warm cache probe, and whether the cache served it.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct LookupOutcome(bool WarmProbe, bool CacheHit);

    private sealed record ArmResult(GuardArmKind Kind, int Threads, long[] Counts, long[] Hits, long[] Misses, double Seconds, int WindowSeconds)
    {
        public double ResolutionsPerSecond => Counts.Sum() / Seconds;

        public long Lookups => Counts.Sum();

        public double MissRate
        {
            get
            {
                var probes = Hits.Sum() + Misses.Sum();
                return probes == 0 ? 0 : (double)Misses.Sum() / probes;
            }
        }

        public double WorkerSpread
        {
            get
            {
                var min = Counts.Min();
                return min == 0 ? 0 : (double)Counts.Max() / min;
            }
        }

        public object BuildMetrics()
        {
            var rates = Counts.Select(count => Round(count / Seconds, 1)).ToArray();
            return new
            {
                resolutions = Counts.Sum(),
                seconds = Round(Seconds, 2),
                resolutionsPerSecond = Round(ResolutionsPerSecond, 1),
                workerResolutionsPerSecondMin = rates.Min(),
                workerResolutionsPerSecondMax = rates.Max(),
                workerSpread = Round(WorkerSpread, 2),
                windowSeconds = WindowSeconds,
                cacheHits = Hits.Sum(),
                cacheMisses = Misses.Sum(),
                cacheMissRate = Round(MissRate, 5),
            };
        }
    }
}
