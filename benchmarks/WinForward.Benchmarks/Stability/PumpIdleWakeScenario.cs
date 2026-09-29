#pragma warning disable CA1416 // The capture pump is Windows-attributed; this probe drives its managed-only loop through fake readers, so it runs on any OS (same rationale as Perf/CapturePumpBenchmarks).
using System.Diagnostics;
using System.Globalization;
using WinForward.NdisApi;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The capture pump's idle cost and its wake-to-dispatch latency (research F5). Two rows over the real
/// <see cref="NdisCapturePump"/> with a fake reader:
/// <list type="bullet">
/// <item>the idle row runs an always-empty reader for a capped window and reports process CPU seconds
/// per idle second, the poll count, and the window's managed allocation — which must be exactly 0 and
/// is the row's own gate, so a non-zero delta fails the run instead of being reported as a number;</item>
/// <item>the wake row runs a reader whose read blocks until the harness arms exactly one frame, which is
/// the F5.2 <c>SetPacketEvent</c> + wait shape, and reports the arrival-to-dispatch distribution over
/// <see cref="WakeCount"/> wakes.</item>
/// </list>
/// Both rows carry the seam-level read-call accounting of F5.1: one <c>TryReadPackets</c> call per poll
/// and one per packet. The driver's internal queue-query + batch-read pair sits below this seam
/// (<c>NdisApiDriver.TryReadPackets</c> queries then reads), so its IOCTL count is not visible from the
/// harness; what these rows establish is that the pump adds no read of its own beyond one per poll.
/// Report-only timing (design §3): the exact counts and bytes are the gates, the latencies are a series.
/// </summary>
internal static class PumpIdleWakeScenario
{
    /// <summary>The idle window's cap: a CPU-cost sample, not a soak — a longer window only adds wall time.</summary>
    private const int MaxIdleSeconds = 15;

    /// <summary>Iterations run through the test seam before the run, so the first-call JIT cost is outside the window.</summary>
    private const int IdleWarmupIterations = 64;

    /// <summary>Unrecorded wakes before the measured ones: the first semaphore round trips pay JIT/tiering and initial thread placement.</summary>
    private const int WakeWarmup = 500;

    /// <summary>Measured wakes. A fixed count (not a duration) keeps the distribution comparable across hosts.</summary>
    private const int WakeCount = 5_000;

    /// <summary>
    /// How long the harness waits after the reader announces its blocking wait, so the wake it measures
    /// is a real blocked→ready transition. It runs before the arrival timestamp and therefore adds
    /// nothing to any sample; it is what keeps a hot handoff from masquerading as a wake (a hot pass
    /// through the semaphore measures sub-microsecond and tells F5.2 nothing).
    /// </summary>
    private static readonly TimeSpan s_parkDelay = TimeSpan.FromMilliseconds(1);

    private static readonly TimeSpan s_idleRunWarmup = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_pollDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan s_entryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_wakeTimeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        var idleSeconds = Math.Clamp(options.DurationSeconds, 1, MaxIdleSeconds);
        var idle = await MeasureIdleAsync(context, idleSeconds).ConfigureAwait(false);
        var wake = await MeasureWakeAsync(context).ConfigureAwait(false);
        WriteVerdict(context, idle, wake);
    }

    /// <summary>
    /// The idle row. The window starts with the pump parked in a gated read — no iteration in flight —
    /// and is bracketed by <see cref="Process.TotalProcessorTime"/> and
    /// <see cref="GC.GetTotalAllocatedBytes"/> samples taken on this thread, so both deltas cover the
    /// pump's live window.
    /// </summary>
    private static async Task<IdleSample> MeasureIdleAsync(StabilityContext context, int windowSeconds)
    {
        var reads = new GatedEmptyCaptureReader();
        var iterations = new IterationCounter();
        await using var pump = new NdisCapturePump(
            reads,
            0x1D1E,
            static (_, _) => ValueTask.CompletedTask,
            new NdisCapturePumpOptions { PollDelay = s_pollDelay, OnBatchCompleted = iterations.Increment });

        // The seam warms the iteration body; the free-running run loop that follows warms the loop and
        // lets tiered compilation promote both before the first sample is taken.
        for (var warm = 0; warm < IdleWarmupIterations; warm++) pump.RunIterationForTests(CancellationToken.None);

        var run = pump.RunAsync(CancellationToken.None);
        // The warmup runs outside the measured window, so it can await a timer; the window itself
        // cannot, because a timer continuation would allocate inside the allocation gate.
        await Task.Delay(s_idleRunWarmup).ConfigureAwait(false);
        var sample = SampleIdleWindow(reads, iterations, windowSeconds);

        // Cooperative stop: the loop is parked in its poll delay at most, so the run completes normally.
        // ReSharper disable once DisposeOnUsingVariable // The stop must be initiated here, before the run is awaited — a parked poll only ends once DisposeAsync sets the pump's stop flag, so awaiting DisposeAsync first would deadlock on the run. The await using scope's own dispose is the exception-path safety net, and NdisCapturePump.DisposeAsync is idempotent (its batch-buffer release is guarded by _buffersReleased).
        var dispose = pump.DisposeAsync();
        await run.ConfigureAwait(false);
        await dispose.ConfigureAwait(false);

        if (sample.AllocatedBytes != 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The idle pump allocated {sample.AllocatedBytes} B over {sample.Seconds:F1} s at a {s_pollDelay.TotalMilliseconds:F0} ms poll delay; zero allocation is this row's gate."));
        }

        // The window can close between a read's return and that iteration's batch-completed callback,
        // so the reader's count may lead the callback's by exactly one.
        if (sample.ReadCalls < sample.Polls || sample.ReadCalls > sample.Polls + 1)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The idle pump issued {sample.ReadCalls} read calls for {sample.Polls} polls; one read per poll is the F5.1 seam contract."));
        }

        context.WriteResult(
            "pump.idle",
            new { requestedSeconds = windowSeconds, cappedSeconds = MaxIdleSeconds, pollDelayMs = s_pollDelay.TotalMilliseconds },
            new
            {
                pollDelayMs = Round(s_pollDelay.TotalMilliseconds, 3),
                windowSeconds = Round(sample.Seconds, 3),
                polls = sample.Polls,
                pollsPerSecond = Round(sample.Polls / sample.Seconds, 1),
                readCalls = sample.ReadCalls,
                packets = 0,
                readCallsPerPoll = Round((double)sample.ReadCalls / Math.Max(1, sample.Polls), 6),
                readCallsPerPacket = 0,
                cpuSeconds = Round(sample.CpuSeconds, 4),
                cpuSecondsPerIdleSecond = Round(sample.CpuSeconds / sample.Seconds, 6),
                allocatedBytes = sample.AllocatedBytes,
                gated = true,
                note = "allocatedBytes is enforced here (a non-zero delta throws) and the read-call pattern is gated exactly in CapturePumpReadCallTests. readCallsPerPacket is 0 because an idle queue yields no packet. CPU is process-wide but sampled on this thread, so it includes the harness thread's own wake-ups; a poll rate well below 1/pollDelay means Thread.Sleep resolution, not pump work, is setting the cadence.",
            });

        return sample;
    }

    /// <summary>
    /// One idle window: park the pump in a gated read, reset the counters with no iteration in flight,
    /// and bracket the window with the CPU and allocation samples. The wait is
    /// <see cref="Thread.Sleep(TimeSpan)"/> rather than <c>Task.Delay</c> — a timer continuation would
    /// allocate inside the very window this row gates.
    /// </summary>
    private static IdleSample SampleIdleWindow(GatedEmptyCaptureReader reads, IterationCounter iterations, int windowSeconds)
    {
        reads.ArmGate();
        if (!reads.WaitUntilEntered(s_entryTimeout))
        {
            throw new InvalidOperationException("The idle pump did not reach its gated read; the measurement window would not bracket a parked pump.");
        }

        // Safe to reset while the reader is parked inside the read: no iteration, and therefore no
        // callback, can be in flight.
        reads.ResetReads();
        iterations.Reset();

        var process = Process.GetCurrentProcess();
        process.Refresh();
        // Every object the window needs is created before the allocation snapshot: a Stopwatch started
        // after it would be charged to the pump and trip the row's own gate.
        var watch = new Stopwatch();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        watch.Start();
        reads.Release();
        Thread.Sleep(TimeSpan.FromSeconds(windowSeconds));
        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        process.Refresh();
        var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
        return new IdleSample(iterations.Count, reads.Reads, watch.Elapsed.TotalSeconds, cpuSeconds, allocated);
    }

    /// <summary>
    /// The wake row. Each measured wake is one arm→dispatch round trip: the harness timestamps the arm,
    /// releases exactly one frame, and waits until the handler has timestamped its dispatch; a wake that
    /// does not happen within the timeout fails the run, so no latency is ever reported for one that did
    /// not. Warmup wakes run on the same pump and are dispatched but not measured.
    /// </summary>
    private static async Task<WakeResult> MeasureWakeAsync(StabilityContext context)
    {
        using var reader = new SignallingCaptureReader(BenchmarkShared.CreateIpv4TcpFrame(128));
        var recorder = new WakeRecorder(WakeWarmup + WakeCount);
        await using var pump = new NdisCapturePump(reader, 0x1D1F, (_, _) => recorder.RecordDispatchAsync(), new NdisCapturePumpOptions { PollDelay = s_pollDelay });

        var run = pump.RunAsync(CancellationToken.None);
        DriveWakes(reader, recorder, WakeWarmup, firstExpected: 1);
        var measuredStart = recorder.Dispatched;
        if (measuredStart != WakeWarmup)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The warmup dispatched {measuredStart} of {WakeWarmup} wakes."));
        }

        var watch = Stopwatch.StartNew();
        var readsAtStart = reader.ReadCalls;
        var packetsAtStart = reader.Packets;
        var arms = DriveWakes(reader, recorder, WakeCount, measuredStart + 1);
        watch.Stop();

        // The counters below are stable while the pump is parked in the next read: a read is counted
        // only once it returns a frame, and no further frame can arrive before the harness arms one.
        var frameReadCalls = reader.ReadCalls - readsAtStart;
        var packets = reader.Packets - packetsAtStart;

        // Stop releases the parked read with an empty result; a stop alone cannot cancel a blocked read,
        // so the reader's own stop flag is what ends the loop.
        // ReSharper disable once DisposeOnUsingVariable // Same contract as the idle row: the stop is initiated before the run is awaited (the reader's Stop() below is what unblocks the parked read), and the await using scope disposes idempotently on exit as the exception-path safety net.
        var dispose = pump.DisposeAsync();
        reader.Stop();
        await run.ConfigureAwait(false);
        await dispose.ConfigureAwait(false);

        var latency = BuildLatency(recorder, arms, measuredStart);
        WriteWakeRow(context, reader, frameReadCalls, packets, latency, watch.Elapsed.TotalSeconds);
        return new WakeResult(WakeCount, frameReadCalls, packets, latency);
    }

    /// <summary>
    /// The wake row's write path, including its own exactness check: the arm-to-dispatch handshake
    /// delivers one frame per wake, so a count that disagrees means the row would describe a wake shape
    /// other than the one it measured.
    /// </summary>
    private static void WriteWakeRow(StabilityContext context, SignallingCaptureReader reader, long frameReadCalls, long packets, LatencyDistribution latency, double seconds)
    {
        if (frameReadCalls != WakeCount || packets != WakeCount)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The wake probe observed {frameReadCalls} read calls and {packets} packets for {WakeCount} wakes; the arm-to-dispatch handshake is one frame per wake."));
        }

        context.WriteResult(
            "pump.idleWake",
            new { wakeWarmup = WakeWarmup, wakes = WakeCount, framesPerWake = 1, pollDelayMs = s_pollDelay.TotalMilliseconds },
            new
            {
                wakes = WakeCount,
                warmupWakes = WakeWarmup,
                windowSeconds = Round(seconds, 3),
                wakesPerSecond = Round(WakeCount / seconds, 1),
                readCalls = frameReadCalls,
                packets,
                emptyReads = reader.EmptyReads,
                readsPerWake = Round((double)frameReadCalls / WakeCount, 6),
                readCallsPerPoll = Round((double)frameReadCalls / WakeCount, 6),
                readCallsPerPacket = Round((double)frameReadCalls / packets, 6),
                latencyMs = latency,
                gated = false,
                note = "Report-only timing (design §3): the arm-to-dispatch percentiles are a series, not a pass line. Every sample follows a confirmed block-then-ready transition, so the number is the OS wake-up cost of the F5.2 SetPacketEvent shape, not a hot semaphore pass. readCalls counts the reads that returned a frame (one per wake); the run's single terminating empty read is reported as emptyReads. Arrival is the harness's timestamp immediately before arming the frame and dispatch is the handler's entry timestamp on the pump thread.",
            });
    }

    /// <summary>
    /// Drives <paramref name="count"/> arm→dispatch round trips, returning the arrival timestamps. The
    /// first <see cref="WakeWarmup"/> are unrecorded by the caller: the first semaphore round trips pay
    /// JIT/tiering and initial thread placement.
    /// </summary>
    private static long[] DriveWakes(SignallingCaptureReader reader, WakeRecorder recorder, int count, long firstExpected)
    {
        var arms = new long[count];
        for (var wake = 0; wake < count; wake++)
        {
            // The reader signals immediately before it blocks, and the park delay then guarantees it
            // is blocked, so every sample is a blocked-read wake-up rather than a hot handoff. Both
            // steps happen before the arrival timestamp, so neither is inside the measured interval.
            if (!reader.WaitUntilParked(s_entryTimeout))
            {
                throw new InvalidOperationException("The signalling reader did not reach its blocking wait; a wake measured here would not be a wake.");
            }

            Thread.Sleep(s_parkDelay);
            arms[wake] = Stopwatch.GetTimestamp();
            reader.Arm();
            WaitForDispatch(recorder, firstExpected + wake, arms[wake]);
        }

        return arms;
    }

    /// <summary>Arrival→dispatch milliseconds per measured wake; a negative value would mean the two timestamps were taken out of order.</summary>
    private static LatencyDistribution BuildLatency(WakeRecorder recorder, long[] arms, long measuredStart)
    {
        var samples = new double[arms.Length];
        for (var wake = 0; wake < arms.Length; wake++)
        {
            var ticks = recorder.TimestampAt(measuredStart + wake) - arms[wake];
            if (ticks < 0)
            {
                throw new InvalidOperationException("A wake latency was negative: the dispatch timestamp preceded its arrival timestamp.");
            }

            samples[wake] = StabilityShared.TicksToMilliseconds(ticks);
        }

        return LatencyDistribution.FromMilliseconds(samples);
    }

    /// <summary>
    /// Waits until the handler has timestamped dispatch <paramref name="expected"/>. The arm-to-dispatch
    /// latency is already sampled by then, so this loop's cost is the harness's, not the probe's; the
    /// bounded wait exists so a lost wake fails the run instead of hanging it.
    /// </summary>
    private static void WaitForDispatch(WakeRecorder recorder, long expected, long armTimestamp)
    {
        var spins = 0;
        while (recorder.Dispatched < expected)
        {
            if (++spins < 10_000)
            {
                Thread.SpinWait(64);
                continue;
            }

            Thread.Yield();
            if (Stopwatch.GetTimestamp() - armTimestamp > s_wakeTimeout.Ticks)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"A wake was not dispatched within {s_wakeTimeout.TotalSeconds:F0} s; refusing to report a latency for a wake that did not happen."));
            }
        }
    }

    private static void WriteVerdict(StabilityContext context, IdleSample idle, WakeResult wake)
    {
        context.WriteResult(
            "pump.idleWake.verdict",
            new { idleSeconds = idle.Seconds, wakes = wake.Wakes, pollDelayMs = s_pollDelay.TotalMilliseconds },
            new
            {
                gated = false,
                idleCpuSecondsPerIdleSecond = Round(idle.CpuSecondsPerIdleSecond, 6),
                idlePollsPerSecond = Round(idle.PollsPerSecond, 1),
                idleAllocatedBytes = idle.AllocatedBytes,
                idleReadCallsPerPoll = Round(idle.ReadCallsPerPoll, 6),
                wakeP50Ms = Round(wake.Latency.P50, 4),
                wakeP95Ms = Round(wake.Latency.P95, 4),
                wakeP99Ms = Round(wake.Latency.P99, 4),
                wakeMaxMs = Round(wake.Latency.Max, 4),
                readsPerWake = Round(wake.ReadsPerWake, 6),
                wakeReadCallsPerPoll = Round(wake.ReadCallsPerPoll, 6),
                wakeReadCallsPerPacket = Round(wake.ReadCallsPerPacket, 6),
                note = "Report-only timing series for F5.2 (design §3). The exact gates live with the numbers: 0 B on pump.idle and the call counts in CapturePumpReadCallTests. The driver's internal queue-query + batch-read IOCTL pair is below the INdisPacketReader seam (NdisApiDriver.TryReadPackets), so its count needs the real driver on Windows; what is established here is that the pump itself reads exactly once per poll.",
            });
    }

    /// <summary>Artifact rounding: the analyzer requires an explicit midpoint mode, and ToEven is the runtime's own default.</summary>
    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.ToEven);

    private sealed record IdleSample(long Polls, long ReadCalls, double Seconds, double CpuSeconds, long AllocatedBytes)
    {
        public double PollsPerSecond => Polls / Seconds;

        public double CpuSecondsPerIdleSecond => CpuSeconds / Seconds;

        public double ReadCallsPerPoll => (double)ReadCalls / Math.Max(1, Polls);
    }

    private sealed record WakeResult(int Wakes, long FrameReadCalls, long Packets, LatencyDistribution Latency)
    {
        public double ReadsPerWake => (double)FrameReadCalls / Wakes;

        public double ReadCallsPerPoll => (double)FrameReadCalls / Wakes;

        public double ReadCallsPerPacket => (double)FrameReadCalls / Math.Max(1, Packets);
    }

    /// <summary>Batch-completed callback counter: one increment per loop iteration, plus one on run exit.</summary>
    private sealed class IterationCounter
    {
        private long _count;

        public long Count => Interlocked.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);
    }

    /// <summary>
    /// An always-empty reader that counts its calls. Reads pass straight through during the warmup; the
    /// first call after <see cref="ArmGate"/> parks on the harness gate and signals entry, so the
    /// harness can reset the counters and start its clocks with the pump parked inside a read and no
    /// partly-counted poll in the window. <see cref="Release"/> clears the arming before releasing, so
    /// the poll loop pays the gate only once.
    /// </summary>
    private sealed class GatedEmptyCaptureReader : INdisPacketReader
    {
        private readonly ManualResetEventSlim _entered = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private long _reads;
        private int _armed;

        public long Reads => Interlocked.Read(ref _reads);

        public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
        {
            if (Volatile.Read(ref _armed) != 0)
            {
                _entered.Set();
                _release.Wait();
            }

            // Counted after the gate so the parked call is not wiped by the harness's reset: the read
            // that completes inside the window is the window's first poll.
            Interlocked.Increment(ref _reads);
            return 0;
        }

        public void ArmGate() => Volatile.Write(ref _armed, 1);

        public bool WaitUntilEntered(TimeSpan timeout) => _entered.Wait(timeout);

        public void Release()
        {
            Volatile.Write(ref _armed, 0);
            _release.Set();
        }

        public void ResetReads() => Interlocked.Exchange(ref _reads, 0);
    }

    /// <summary>
    /// The F5.2 shape: <c>TryReadPackets</c> announces that it is about to block, then blocks until the
    /// harness arms exactly one frame, then returns 1. The pump thread is therefore parked inside the
    /// read between wakes, and the harness's arm timestamp is the arrival the handler's dispatch is
    /// measured against. Only frame-delivering reads are counted in <see cref="ReadCalls"/>, which keeps
    /// the per-wake accounting stable while the pump is parked in the next read. <see cref="Stop"/>
    /// releases the parked read with an empty result, because a stop cannot cancel a read blocked in a
    /// semaphore wait.
    /// </summary>
    private sealed class SignallingCaptureReader(byte[] frame) : INdisPacketReader, IDisposable
    {
        private readonly SemaphoreSlim _armed = new(initialCount: 0);
        private readonly AutoResetEvent _parked = new(initialState: false);
        private long _readCalls;
        private long _emptyReads;
        private long _packets;
        private int _stopped;

        public long ReadCalls => Interlocked.Read(ref _readCalls);

        public long EmptyReads => Interlocked.Read(ref _emptyReads);

        public long Packets => Interlocked.Read(ref _packets);

        public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                Interlocked.Increment(ref _emptyReads);
                return 0;
            }

            _parked.Set();
            _armed.Wait();
            if (Volatile.Read(ref _stopped) != 0)
            {
                Interlocked.Increment(ref _emptyReads);
                return 0;
            }

            buffers[0].SetFrame(frame, NdisApiAbi.PacketFlagOnReceive, adapterHandle);
            Interlocked.Increment(ref _readCalls);
            Interlocked.Increment(ref _packets);
            return 1;
        }

        /// <summary>Blocks until the reader has announced that it is entering its blocking wait.</summary>
        public bool WaitUntilParked(TimeSpan timeout) => _parked.WaitOne(timeout);

        public void Arm() => _armed.Release();

        public void Stop()
        {
            Volatile.Write(ref _stopped, 1);
            _armed.Release();
        }

        public void Dispose()
        {
            _armed.Dispose();
            _parked.Dispose();
        }
    }

    /// <summary>
    /// Dispatch timestamps taken in the handler the pump calls: timestamp first, then a release-fenced
    /// dispatch count, so the harness only reads a slot after the count proves it was written. The
    /// handler runs on the pump thread alone, so the slot cursor needs no interlocked update.
    /// </summary>
    private sealed class WakeRecorder(int capacity)
    {
        private readonly long[] _timestamps = new long[capacity];
        private long _dispatched;
        private int _cursor;

        public long Dispatched => Volatile.Read(ref _dispatched);

        public ValueTask RecordDispatchAsync()
        {
            var index = _cursor;
            _cursor = index + 1;
            _timestamps[index] = Stopwatch.GetTimestamp();
            Volatile.Write(ref _dispatched, index + 1);
            return ValueTask.CompletedTask;
        }

        public long TimestampAt(long index) => Volatile.Read(ref _timestamps[index]);
    }
}
