#pragma warning disable CA1416 // The capture pump is Windows-attributed; these probes drive its managed-only loop through fake readers, so they run on any OS (same rationale as Perf/CapturePumpBenchmarks).
using System.Diagnostics;
using System.Globalization;
using WinForward.NdisApi;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The capture pump's idle cost and its wake-to-dispatch latency. Four rows over the
/// real <see cref="NdisCapturePump"/> with a fake reader:
/// <list type="bullet">
/// <item><c>pump.idle</c> — an always-empty reader for a capped window, reporting process CPU seconds
/// per idle second, the poll count and the window's managed allocation, which must be exactly 0 and
/// is the row's own gate. No arrival signal reaches this pump, so the row is also the proof that the
/// sleep-poll fallback did not move;</item>
/// <item><c>pump.idleEvent</c> — the same window with the production <see cref="NdisPacketArrivalSignal"/>
/// installed over an unsignaled OS event, so every wait runs to the 100 ms bound: the row reports the
/// wait cadence beside the CPU number, and its 0 B gate covers ~150 real timeouts (the xunit gates can
/// only exercise <c>WaitOne(0)</c>'s fast path);</item>
/// <item><c>pump.idleWake</c> — a reader whose read blocks until the harness arms exactly one frame,
/// the reader-side proxy for the arrival shape;</item>
/// <item><c>pump.idleWakeEvent</c> — the pump parks in the arrival wait and the harness arms the frame
/// and sets the event, with the park confirmed before every measured wake.</item>
/// </list>
/// The driver's internal read-call shape sits below the <see cref="INdisPacketReader"/> seam and is
/// gated exactly in <c>NdisApiReadShapeTests</c>; what these rows establish is the pump's own cadence
/// and its wake cost. Report-only timing: the exact counts and bytes are the gates, the
/// latencies are a series.
/// </summary>
internal static class PumpIdleWakeScenario
{
    /// <summary>The idle window's cap: a CPU-cost sample, not a soak — a longer window only adds wall time.</summary>
    private const int MaxIdleSeconds = 15;

    /// <summary>Iterations run through the test seam before the run, so the first-call JIT cost is outside the window.</summary>
    private const int IdleWarmupIterations = 64;

    /// <summary>Unrecorded wakes before the measured ones: the first round trips pay JIT/tiering and initial thread placement.</summary>
    private const int WakeWarmup = 500;

    /// <summary>Measured wakes. A fixed count (not a duration) keeps the distribution comparable across hosts.</summary>
    private const int WakeCount = 5_000;

    /// <summary>
    /// How long the harness waits after the reader announces its blocking wait, so the wake it measures
    /// is a real blocked→ready transition. It runs before the arrival timestamp and therefore adds
    /// nothing to any sample; it is what keeps a hot handoff from masquerading as a wake.
    /// </summary>
    private static readonly TimeSpan s_parkDelay = TimeSpan.FromMilliseconds(1);

    private static readonly TimeSpan s_idleRunWarmup = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_pollDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan s_idleWaitTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_entryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_wakeTimeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        var idleSeconds = Math.Clamp(options.DurationSeconds, 1, MaxIdleSeconds);
        var idle = await MeasureIdleAsync(context, idleSeconds).ConfigureAwait(false);
        var idleEvent = await MeasureIdleWithSignalAsync(context, idleSeconds).ConfigureAwait(false);
        var wake = await MeasureWakeAsync(context).ConfigureAwait(false);
        var eventWake = await MeasureEventWakeAsync(context).ConfigureAwait(false);
        WriteVerdict(context, idle, idleEvent, wake, eventWake);
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
        var sample = SampleIdleWindow(reads, iterations, windowSeconds, resetCounters: null);

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
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The idle pump issued {sample.ReadCalls} read calls for {sample.Polls} polls; one read per poll is the seam contract."));
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
                note = "allocatedBytes is enforced here (a non-zero delta throws) and the read-call pattern is gated exactly in CapturePumpReadCallTests. readCallsPerPacket is 0 because an idle queue yields no packet. CPU is process-wide but sampled on this thread, so it includes the harness thread's own wake-ups; a poll rate well below 1/pollDelay means Thread.Sleep resolution, not pump work, is setting the cadence. No arrival signal reaches this pump, so this row is also the byte-identical fallback comparator for pump.idleEvent.",
            });

        return sample;
    }

    /// <summary>
    /// The <c>pump.idleEvent</c> row: the same idle window with the production arrival signal
    /// installed over an event the harness never sets, so every wait runs to <see cref="s_idleWaitTimeout"/>.
    /// The wait cadence is reported beside the CPU number so a CPU change without a cadence change is
    /// visible as unattributed, and the row's own 0 B throw covers the real timeouts the xunit gates
    /// cannot reach.
    /// </summary>
    private static async Task<IdleSample> MeasureIdleWithSignalAsync(StabilityContext context, int windowSeconds)
    {
        var reads = new GatedEmptyCaptureReader();
        var iterations = new IterationCounter();
        using var arrival = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var productionSignal = new NdisPacketArrivalSignal(arrival);
        using var signal = new ParkConfirmingArrivalSignal(productionSignal);
        await using var pump = new NdisCapturePump(
            reads,
            0x1D2E,
            static (_, _) => ValueTask.CompletedTask,
            new NdisCapturePumpOptions
            {
                PollDelay = s_pollDelay,
                IdleWaitTimeout = s_idleWaitTimeout,
                PacketArrivalSignal = signal,
                OnBatchCompleted = iterations.Increment,
            });

        for (var warm = 0; warm < IdleWarmupIterations; warm++) pump.RunIterationForTests(CancellationToken.None);

        var run = pump.RunAsync(CancellationToken.None);
        await Task.Delay(s_idleRunWarmup).ConfigureAwait(false);
        // Reset the wait counter in the same no-iteration-in-flight moment as the read counter, so the
        // reported cadence covers exactly the window.
        var sample = SampleIdleWindow(reads, iterations, windowSeconds, signal.ResetCounters);
        var waits = signal.Entered;

        // The stop flag ends the parked wait; the await using scope above releases the pump after this.
        pump.RequestStopForTests();
        await run.ConfigureAwait(false);

        if (sample.AllocatedBytes != 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The arrival-waiting idle pump allocated {sample.AllocatedBytes} B over {sample.Seconds:F1} s; zero allocation is this row's gate."));
        }

        // One wait per empty iteration, and the wait must be the configured bound: a shorter one would
        // mean the pump fell back to sleep pacing.
        if (waits != sample.Polls)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The arrival-waiting idle pump issued {waits} waits for {sample.Polls} polls; one bounded wait per idle iteration is the contract."));
        }

        if (signal.LastTimeout != s_idleWaitTimeout)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The arrival wait was issued with a {signal.LastTimeout.TotalMilliseconds:F1} ms bound, not the configured {s_idleWaitTimeout.TotalMilliseconds:F0} ms."));
        }

        WriteIdleEventRow(context, windowSeconds, sample, waits);
        return sample;
    }

    /// <summary>The <c>pump.idleEvent</c> row's payload, with the cadence reported beside the CPU number.</summary>
    private static void WriteIdleEventRow(StabilityContext context, int windowSeconds, IdleSample sample, long waits)
    {
        context.WriteResult(
            "pump.idleEvent",
            new { requestedSeconds = windowSeconds, cappedSeconds = MaxIdleSeconds, idleWaitTimeoutMs = s_idleWaitTimeout.TotalMilliseconds },
            new
            {
                idleWaitTimeoutMs = Round(s_idleWaitTimeout.TotalMilliseconds, 3),
                windowSeconds = Round(sample.Seconds, 3),
                waits,
                waitsPerSecond = Round(waits / sample.Seconds, 1),
                polls = sample.Polls,
                pollsPerSecond = Round(sample.Polls / sample.Seconds, 1),
                emptyReads = sample.ReadCalls,
                emptyReadsPerSecond = Round(sample.ReadCalls / sample.Seconds, 1),
                readCallsPerPoll = Round((double)sample.ReadCalls / Math.Max(1, sample.Polls), 6),
                cpuSeconds = Round(sample.CpuSeconds, 4),
                cpuSecondsPerIdleSecond = Round(sample.CpuSeconds / sample.Seconds, 6),
                allocatedBytes = sample.AllocatedBytes,
                gated = true,
                note = "allocatedBytes is enforced here (a non-zero delta throws) over ~150 genuine 100 ms timeouts, which is the half the xunit gates cannot cover (they exercise WaitOne(0)'s immediate return). The wait count and the timeout argument are exact; the CPU number is a series and is attributed by waitsPerSecond/emptyReadsPerSecond beside it. The event is never set by the harness: this row measures the bounded-wait cadence, not the wake cost.",
            });
    }

    /// <summary>
    /// One idle window: park the pump in a gated read, reset the counters with no iteration in flight,
    /// and bracket the window with the CPU and allocation samples. The wait is
    /// <see cref="Thread.Sleep(TimeSpan)"/> rather than <c>Task.Delay</c> — a timer continuation would
    /// allocate inside the very window this row gates.
    /// </summary>
    private static IdleSample SampleIdleWindow(GatedEmptyCaptureReader reads, IterationCounter iterations, int windowSeconds, Action? resetCounters)
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
        resetCounters?.Invoke();

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
        using var reader = new SignallingCaptureReader(BenchmarkShared.CreateIPv4TcpFrame(128));
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
        WriteWakeRow(context, frameReadCalls, packets, reader.EmptyReads, latency, watch.Elapsed.TotalSeconds);
        return new WakeResult(WakeCount, frameReadCalls, packets, latency);
    }

    /// <summary>
    /// The wake row's write path, including its own exactness check: the arm-to-dispatch handshake
    /// delivers one frame per wake, so a count that disagrees means the row would describe a wake shape
    /// other than the one it measured.
    /// </summary>
    private static void WriteWakeRow(StabilityContext context, long frameReadCalls, long packets, long emptyReads, LatencyDistribution latency, double seconds)
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
                emptyReads,
                readsPerWake = Round((double)frameReadCalls / WakeCount, 6),
                readCallsPerPoll = Round((double)frameReadCalls / WakeCount, 6),
                readCallsPerPacket = Round((double)frameReadCalls / packets, 6),
                latencyMs = latency,
                gated = false,
                note = "Report-only timing: the arm-to-dispatch percentiles are a series, not a pass line. Every sample follows a confirmed block-then-ready transition, so the number is the OS wake-up cost of the SetPacketEvent shape, not a hot semaphore pass. readCalls counts the reads that returned a frame (one per wake); the run's single terminating empty read is reported as emptyReads. Arrival is the harness's timestamp immediately before arming the frame and dispatch is the handler's entry timestamp on the pump thread. This row parks the reader, not the pump; pump.idleWakeEvent measures the pump-side wait.",
            });
    }

    /// <summary>
    /// The <c>pump.idleWakeEvent</c> row: the pump parks in the arrival wait and the harness arms the
    /// frame and sets the event. Every measured wake is confirmed to follow a park — the harness waits
    /// for the decorator to record one more <c>Wait</c> entry than the previous wake, then applies the
    /// park delay, then stamps arrival — so an arm-and-set that merely rode a retained auto-reset token
    /// cannot be reported as a wake.
    /// </summary>
    private static async Task<EventWakeResult> MeasureEventWakeAsync(StabilityContext context)
    {
        using var arrival = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var productionSignal = new NdisPacketArrivalSignal(arrival);
        using var signal = new ParkConfirmingArrivalSignal(productionSignal);
        var reader = new ArmedFrameCaptureReader(BenchmarkShared.CreateIPv4TcpFrame(128));
        var recorder = new WakeRecorder(WakeWarmup + WakeCount);
        await using var pump = new NdisCapturePump(reader, 0x1D3F, (_, _) => recorder.RecordDispatchAsync(), new NdisCapturePumpOptions
        {
            PollDelay = s_pollDelay,
            IdleWaitTimeout = s_idleWaitTimeout,
            PacketArrivalSignal = signal,
        });

        var run = pump.RunAsync(CancellationToken.None);
        DriveEventWakes(reader, signal, arrival, recorder, WakeWarmup, firstExpected: 1);
        var measuredStart = recorder.Dispatched;
        if (measuredStart != WakeWarmup)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The warmup dispatched {measuredStart} of {WakeWarmup} wakes."));
        }

        var watch = Stopwatch.StartNew();
        var readsAtStart = reader.FrameReads;
        var packetsAtStart = reader.Packets;
        var entriesAtStart = signal.Entered;
        var signalDrivenAtStart = signal.ReturnedBeforeTimeout;
        var emptyReadsAtStart = reader.EmptyReads;
        var arms = DriveEventWakes(reader, signal, arrival, recorder, WakeCount, measuredStart + 1);
        watch.Stop();

        var frameReads = reader.FrameReads - readsAtStart;
        var packets = reader.Packets - packetsAtStart;
        var entries = signal.Entered - entriesAtStart;
        var signalDriven = signal.ReturnedBeforeTimeout - signalDrivenAtStart;

        // The pump is parked in the wait; the stop flag ends the loop when that wait returns, and the
        // await using scope above releases the pump once this run has been awaited.
        pump.RequestStopForTests();
        reader.Stop();
        await run.ConfigureAwait(false);

        if (frameReads != WakeCount || packets != WakeCount)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The event wake probe observed {frameReads} frame reads and {packets} packets for {WakeCount} wakes; the arm-to-dispatch handshake is one frame per wake."));
        }

        // Every measured wake must be a signal-driven return, not a 100 ms timeout that happened to
        // find the armed frame: a timeout-driven sample would report the bound as the wake cost.
        if (signalDriven != WakeCount)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{WakeCount - signalDriven} of {WakeCount} measured wakes returned on the timeout rather than on the signal; the row would be reporting the wait bound as a wake cost."));
        }

        var latency = BuildLatency(recorder, arms, measuredStart);
        WriteEventWakeRow(context, frameReads, entries, signalDriven, reader.EmptyReads - emptyReadsAtStart, latency, watch.Elapsed.TotalSeconds);
        return new EventWakeResult(WakeCount, frameReads, entries, signalDriven, latency);
    }

    /// <summary>
    /// The event wake row's write path: exact counts first (one frame read, one confirmed park and one
    /// signal-driven return per wake), then the latency series.
    /// </summary>
    private static void WriteEventWakeRow(StabilityContext context, long frameReads, long entries, long signalDriven, long emptyReads, LatencyDistribution latency, double seconds)
    {
        context.WriteResult(
            "pump.idleWakeEvent",
            new { wakeWarmup = WakeWarmup, wakes = WakeCount, framesPerWake = 1, idleWaitTimeoutMs = s_idleWaitTimeout.TotalMilliseconds },
            new
            {
                wakes = WakeCount,
                warmupWakes = WakeWarmup,
                windowSeconds = Round(seconds, 3),
                wakesPerSecond = Round(WakeCount / seconds, 1),
                frameReadCalls = frameReads,
                frameReadCallsPerWake = Round((double)frameReads / WakeCount, 6),
                packets = frameReads,
                emptyReads,
                emptyReadsPerWake = Round((double)emptyReads / WakeCount, 6),
                parkEntries = entries,
                parkConfirmationsPerWake = Round((double)entries / WakeCount, 6),
                signalDrivenReturns = signalDriven,
                latencyMs = latency,
                gated = false,
                note = "Report-only timing. Every sample is a confirmed park-then-wake: the harness waits for one more Wait entry than the previous wake, sleeps the park delay, then stamps arrival, arms the frame and sets the event; a missing entry fails the row rather than reporting a latency for a wake that did not happen, and the signalDrivenReturns/gated check rejects a timeout-driven sample. emptyReads and every other counter cover the measured window only, so the warmup's parks are not in the ratio. Accounting change against pump.idleWake: this row counts one extra seam-level read per wake (the empty read that parks the pump) because the proxy row counted only frame-delivering reads and hid its empty one inside the parked TryReadPackets — one IOCTL replaces ~886 empty-queue queries per second, so the bare readsPerWake 1.0 -> 2.0 comparison is not like-for-like and readCallsPerPacket is not an acceptance figure here.",
            });
    }

    /// <summary>
    /// Drives <paramref name="count"/> arm→dispatch round trips through a reader that blocks, returning
    /// the arrival timestamps. The first <see cref="WakeWarmup"/> are unrecorded by the caller: the first
    /// semaphore round trips pay JIT/tiering and initial thread placement.
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
            WaitForDispatch(recorder, firstExpected + wake);
        }

        return arms;
    }

    /// <summary>
    /// Drives <paramref name="count"/> park→wake round trips: confirm the pump entered its arrival
    /// wait, apply the park delay, then stamp arrival, arm the frame and set the event. A wait entry
    /// that never appears fails the run — that is what separates a real wake from a hot handoff, since
    /// auto-reset would retain an arm-and-set issued before the park and return immediately.
    /// </summary>
    private static long[] DriveEventWakes(ArmedFrameCaptureReader reader, ParkConfirmingArrivalSignal signal, EventWaitHandle arrival, WakeRecorder recorder, int count, long firstExpected)
    {
        var arms = new long[count];
        var expectedEntry = signal.Entered;
        for (var wake = 0; wake < count; wake++)
        {
            expectedEntry++;
            if (!WaitForParkEntry(signal, expectedEntry))
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The pump did not enter its arrival wait for wake {wake + 1} of {count} (entries seen: {signal.Entered}, frame reads: {reader.FrameReads}, empty reads: {reader.EmptyReads}); a latency reported here would not be a wake."));
            }

            Thread.Sleep(s_parkDelay);
            arms[wake] = Stopwatch.GetTimestamp();
            reader.Arm();
            arrival.Set();
            WaitForDispatch(recorder, firstExpected + wake);
        }

        return arms;
    }

    /// <summary>Bounded wait for the decorator's park-entry count to reach <paramref name="expected"/>.</summary>
    private static bool WaitForParkEntry(ParkConfirmingArrivalSignal signal, long expected)
    {
        var deadline = Deadline(s_entryTimeout);
        while (signal.Entered < expected)
        {
            if (Stopwatch.GetTimestamp() > deadline) return false;
            Thread.Yield();
        }

        return true;
    }

    /// <summary>
    /// A deadline on the <see cref="Stopwatch"/> clock. <see cref="Stopwatch.Frequency"/> is not
    /// <see cref="TimeSpan.TicksPerSecond"/> off Windows, so comparing a raw <c>TimeSpan.Ticks</c>
    /// against <see cref="Stopwatch.GetTimestamp"/> measures in the wrong unit and silently shortens
    /// the bound by that ratio.
    /// </summary>
    private static long Deadline(TimeSpan timeout) => Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

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
    private static void WaitForDispatch(WakeRecorder recorder, long expected)
    {
        var spins = 0;
        var deadline = Deadline(s_wakeTimeout);
        while (recorder.Dispatched < expected)
        {
            if (++spins < 10_000)
            {
                Thread.SpinWait(64);
                continue;
            }

            Thread.Yield();
            if (Stopwatch.GetTimestamp() > deadline)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"A wake was not dispatched within {s_wakeTimeout.TotalSeconds:F0} s; refusing to report a latency for a wake that did not happen."));
            }
        }
    }

    private static void WriteVerdict(StabilityContext context, IdleSample idle, IdleSample idleEvent, WakeResult wake, EventWakeResult eventWake)
    {
        context.WriteResult(
            "pump.idleWake.verdict",
            new { idleSeconds = idle.Seconds, wakes = wake.Wakes, pollDelayMs = s_pollDelay.TotalMilliseconds, idleWaitTimeoutMs = s_idleWaitTimeout.TotalMilliseconds },
            new
            {
                gated = false,
                idleCpuSecondsPerIdleSecond = Round(idle.CpuSecondsPerIdleSecond, 6),
                idlePollsPerSecond = Round(idle.PollsPerSecond, 1),
                idleAllocatedBytes = idle.AllocatedBytes,
                idleReadCallsPerPoll = Round(idle.ReadCallsPerPoll, 6),
                idleEventCpuSecondsPerIdleSecond = Round(idleEvent.CpuSecondsPerIdleSecond, 6),
                idleEventWaitsPerSecond = Round(idleEvent.PollsPerSecond, 1),
                idleEventAllocatedBytes = idleEvent.AllocatedBytes,
                idleEventReadCallsPerPoll = Round(idleEvent.ReadCallsPerPoll, 6),
                wakeP50Ms = Round(wake.Latency.P50, 4),
                wakeP95Ms = Round(wake.Latency.P95, 4),
                wakeP99Ms = Round(wake.Latency.P99, 4),
                wakeMaxMs = Round(wake.Latency.Max, 4),
                readsPerWake = Round(wake.ReadsPerWake, 6),
                wakeReadCallsPerPoll = Round(wake.ReadCallsPerPoll, 6),
                wakeReadCallsPerPacket = Round(wake.ReadCallsPerPacket, 6),
                eventWakeP50Ms = Round(eventWake.Latency.P50, 4),
                eventWakeP95Ms = Round(eventWake.Latency.P95, 4),
                eventWakeP99Ms = Round(eventWake.Latency.P99, 4),
                eventWakeMaxMs = Round(eventWake.Latency.Max, 4),
                eventWakeFrameReadCallsPerWake = Round((double)eventWake.FrameReads / eventWake.Wakes, 6),
                eventWakeParkConfirmationsPerWake = Round((double)eventWake.Entries / eventWake.Wakes, 6),
                eventWakeSignalDrivenReturns = eventWake.SignalDriven,
                note = "Report-only timing series. The exact gates live with the numbers: 0 B on pump.idle and pump.idleEvent, the wait/read counts on pump.idleEvent, and the call counts in CapturePumpReadCallTests. The driver's internal read shape is gated in NdisApiReadShapeTests at the driver↔native seam. pump.idle carries no arrival signal and must not move; the CPU and cadence pair in pump.idleEvent is what attributes the drop to the wait cadence rather than to pump work.",
            });
    }

    /// <summary>Artifact rounding: the analyzer requires an explicit midpoint mode, and ToEven is the runtime's own default.</summary>
    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.ToEven);

    /// <summary>Per-wake counters for the event row's own exactness checks.</summary>
    private sealed record EventWakeResult(int Wakes, long FrameReads, long Entries, long SignalDriven, LatencyDistribution Latency);
}
