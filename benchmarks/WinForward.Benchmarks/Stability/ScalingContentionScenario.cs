using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using WinForward.Core;
using WinForward.Runtime;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// Contention and scaling probe for the global lock chain (research F2). One shared flow table plus
/// one self-traffic registry, driven by 1/2/4 worker threads that resolve mostly disjoint keys through
/// the two operations a warm packet pays: <see cref="ISelfTrafficGuard.IsOwned"/> and
/// <see cref="FlowTable.TryResolve"/>. The claim under test is the scaling ratio
/// <c>throughput(N) / (N × throughput(1))</c>: both are single-instance locks, so every extra worker
/// should push the ratio below 1.
/// <para>
/// The run measures the same workload twice — once with <see cref="NeverOwnedGuard"/> (the guard shape
/// the existing dispatcher benchmarks use) and once with a real populated
/// <see cref="SelfTrafficRegistry"/> — because the self-traffic reorder (F2.1) can only move the second
/// arm: the fake guard answers without touching a lock, so a row built on it reports a constant zero
/// delta for work that changed exactly that collaborator. Report-only (design §3): the artifact is the
/// curve, not a pass line.
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

    private static long s_sink;

    public static Task RunAsync(StabilityContext context, SoakOptions options)
    {
        var threads = options.Threads > 0 ? [options.Threads] : s_threadSweep;
        var windowSeconds = Math.Max(1, options.DurationSeconds / (2 * threads.Length));
        var sharedKeyPercent = Math.Clamp(options.SharedKeyPercent, 0, 50);
        var sharedPeriod = sharedKeyPercent == 0 ? int.MaxValue : Math.Max(1, 100 / sharedKeyPercent);

        var keys = BuildKeys(options.Flows);
        var contexts = new FlowContext[keys.Length];
        for (var index = 0; index < keys.Length; index++) contexts[index] = BenchmarkShared.CreateContext(keys[index]);
        var table = BuildTable(keys);
        var sharedCount = sharedKeyPercent == 0 ? 0 : Math.Clamp(options.Flows * sharedKeyPercent / 100, 1, keys.Length);

        // The registry's tuples are disjoint from every resolved key, so the measured shape is the
        // common warm case: the check takes the lock and probes, then answers "not ours".
        var registered = new SelfTrafficRegistry();
        for (var index = 0; index < SelfTrafficTuples; index++)
        {
            registered.Register(SelfTrafficRegistry.SelfTrafficKey.From(BenchmarkShared.CreateContext(BenchmarkShared.CreateFlowKey(options.Flows + 1_000 + index))));
        }

        var arms = new GuardArm[] { new(GuardArmKind.Fake, new NeverOwnedGuard()), new(GuardArmKind.Real, registered) };
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
                        selfTrafficGuard = arm.Kind == GuardArmKind.Fake ? "fake" : "real",
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

        var counts = new long[threadCount];
        var gate = new ManualResetEventSlim(initialState: false);
        var workers = new Thread[threadCount];
        for (var index = 0; index < threadCount; index++)
        {
            var worker = index;
            // ReSharper disable once AccessToDisposedClosure // every worker is joined before the gate is disposed below.
            workers[worker] = new Thread(() => counts[worker] = RunWorker(arm.Guard, table, keys, contexts, partitions[worker], sharedCount, sharedPeriod, windowSeconds, gate))
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
        Volatile.Write(ref s_sink, counts.Sum());
        return new ArmResult(arm.Kind, threadCount, counts, wall.Elapsed.TotalSeconds, windowSeconds);
    }

    private static long RunWorker(ISelfTrafficGuard guard, FlowTable table, FlowKey[] keys, FlowContext[] contexts, int[] partition, int sharedCount, int sharedPeriod, int windowSeconds, ManualResetEventSlim gate)
    {
        gate.Wait();
        long count = 0;
        var partitionCursor = 0;
        var sharedCursor = 0;
        var sinceShared = 0;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < windowSeconds)
        {
            for (var step = 0; step < BatchSize; step++)
            {
                if (sharedCount > 0 && sinceShared == 0)
                {
                    count += Resolve(guard, table, keys[sharedCursor], contexts[sharedCursor]) ? 1 : 0;
                    sharedCursor++;
                    if (sharedCursor == sharedCount) sharedCursor = 0;
                }
                else
                {
                    var key = partition[partitionCursor];
                    count += Resolve(guard, table, keys[key], contexts[key]) ? 1 : 0;
                    partitionCursor++;
                    if (partitionCursor == partition.Length) partitionCursor = 0;
                }

                if (sinceShared >= sharedPeriod) sinceShared = 0;
                else sinceShared++;
            }
        }

        return count;
    }

    /// <summary>
    /// One lookup: the self-traffic check then the flow-table resolve — the serialized lock
    /// acquisitions and dictionary probes a warm packet pays (research F2's cost model). Inlined so the
    /// measurement is the collaborators, not the loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Resolve(ISelfTrafficGuard guard, FlowTable table, in FlowKey key, in FlowContext context) =>
        !guard.IsOwned(context) && table.TryResolve(key, out _);

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

    private static object BuildVerdict(List<ArmResult> results, int[] threads)
    {
        return new
        {
            gated = false,
            note = "Report-only contention curve (design §3). The fake arm is the shape the existing dispatcher benchmarks use; only the real arm can show the self-traffic reorder (F2.1).",
            baselineThreads = threads[0],
            fakeGuard = Describe(GuardArmKind.Fake),
            realGuard = Describe(GuardArmKind.Real),
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
    }

    private enum GuardArmKind
    {
        Fake,
        Real,
    }

    private readonly record struct GuardArm(GuardArmKind Kind, ISelfTrafficGuard Guard);

    private sealed record ArmResult(GuardArmKind Kind, int Threads, long[] Counts, double Seconds, int WindowSeconds)
    {
        public double ResolutionsPerSecond => Counts.Sum() / Seconds;

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
            };
        }
    }
}
