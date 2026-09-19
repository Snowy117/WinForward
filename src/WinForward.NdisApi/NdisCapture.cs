using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;

namespace WinForward.NdisApi;

public readonly record struct NdisCapturedPacket(NdisPacketBuffer Buffer, nint AdapterHandle, uint DeviceFlags)
{
    public uint Flags { get; init; }

    internal static NdisCapturedPacket FromCapture(NdisPacketBuffer buffer, nint enumerationAdapterHandle)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return new NdisCapturedPacket(buffer, enumerationAdapterHandle, buffer.DeviceFlags) { Flags = buffer.Flags };
    }
}

/// <summary>
/// The batched packet-read seam of <see cref="NdisApiDriver"/> consumed by
/// <see cref="NdisCapturePump"/>. Exposed as an interface so the pump's batch processing loop
/// (in-batch ordering, partial batches, empty-queue polling) is testable without native hardware.
/// </summary>
public interface INdisPacketReader
{
    /// <summary>
    /// Reads up to <c>buffers.Length</c> packets into the prepared buffers and returns how many
    /// were actually filled; 0 means the adapter queue was empty.
    /// </summary>
    int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers);
}

/// <summary>
/// The optional knobs of the <see cref="NdisCapturePump"/> constructor as one record of named
/// init properties, so call sites set only the knobs they need. Every member defaults to null,
/// which selects the pump's built-in default for that knob. The two <see langword="internal"/> members are
/// test and benchmark seams (reached through <c>InternalsVisibleTo</c>) and are never set by
/// production.
/// </summary>
public sealed record NdisCapturePumpOptions
{
    /// <summary>Idle pacing between empty-queue polls; null keeps the pump's 1 ms default.</summary>
    public TimeSpan? PollDelay { get; init; }

    /// <summary>Runs after every iteration's slot loop (and once on run exit); null disables.</summary>
    public Action? OnBatchCompleted { get; init; }

    /// <summary>Observes each transient-read retry with (nativeError, attempt); null disables.</summary>
    public Action<int, int>? OnTransientRetry { get; init; }

    /// <summary>Runs once when the pump exits through the degraded path; null disables.</summary>
    public Action<int>? OnDegraded { get; init; }

    /// <summary>Test/benchmark seam: overrides the batch-buffer count; null keeps the production default (32).</summary>
    internal int? BatchCapacity { get; init; }

    /// <summary>Test/benchmark seam: overrides the retry backoff base; null keeps the production default (100 ms).</summary>
    internal TimeSpan? TransientRetryBaseDelay { get; init; }
}

/// <summary>
/// Pumps captured packets until cancelled or stopped. The loop runs on ONE dedicated
/// background-lifetime <see cref="Thread"/> whose body is fully synchronous, so idle polling
/// (<see cref="Thread.Sleep(TimeSpan)"/>, no <c>Task.Delay</c>) and per-packet dispatch allocate no
/// managed memory. <see cref="RunAsync"/> creates/starts that thread and returns a
/// <see cref="ValueTask"/> that completes when the loop exits (success, cancellation, or failure);
/// it may be called at most once per pump instance.
/// <para>
/// The handler's <see cref="ValueTask"/> is completed inline on the pump thread. A synchronously
/// completing handler — the dispatcher fast path returns the executor's ValueTask directly and
/// new-flow work goes to the pooled setup executor — costs a plain zero-allocation result read and
/// preserves strict in-batch slot ordering. A genuinely pending handler blocks the dedicated
/// thread; the zero-copy capture contract (<c>PumpDoesNotReuseBatchSlotWhileHandlerIsInFlight</c>)
/// forbids starting the next batch read while a slot's handler is still in flight, and blocking a
/// thread this pump solely owns is not thread-pool starvation. Handing the pending handler to a
/// pooled executor instead is not possible without changing the fixed
/// <see cref="Func{T1, T2, TResult}"/> handler signature into something queued, so the pump keeps
/// ownership of the slot lifetime by blocking until the handler completes.
/// </para>
/// Each iteration fetches one batch (single kernel round trip) and invokes the handler for slots
/// 0..readCount-1 strictly in order, so reinjection order within an adapter matches arrival order.
/// An empty batch keeps the poll-delay pacing. The optional batch-completed callback (constructor)
/// runs after the slot loop of every iteration — before the next read can reuse batch slots — and
/// once more when the run loop exits, so deferred work (batched reinjection) never outlives the
/// batch it belongs to. Batch buffers live for the pump's lifetime and are released exactly once,
/// when the run loop exits or the pump is disposed; disposal signals stop but never cancels the run
/// (the loop re-checks the stop flag every iteration and its only waits are bounded by the
/// configured poll delay, a single transient-retry backoff sleep, or an in-flight handler) and
/// parks on the run's completion when a run is in flight, so the buffers are never freed under a
/// live loop.
/// A transient native read failure (see <see cref="NdisNativeCallStatus.IsTransientReadError"/>)
/// is retried with bounded exponential backoff; exhaustion or a permanent failure is a
/// degraded exit — the loop returns normally (no throw) and the optional
/// <c>onDegraded(nativeError)</c> callback fires exactly once, so the process and sibling
/// adapter pumps keep running while this adapter's interception stops.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NdisCapturePump : IAsyncDisposable
{
    private const int DefaultBatchCapacity = 32;

    /// <summary>
    /// How many consecutive transient read failures one incident may retry before the adapter's
    /// interception degrades (R7). With the doubling base delay capped per attempt, the worst-case
    /// incident window is ~3.1 s.
    /// </summary>
    internal const int TransientRetryMaxAttempts = 5;

    private static readonly TimeSpan s_defaultTransientRetryBaseDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_transientRetryDelayCap = TimeSpan.FromMilliseconds(1600);

    private readonly INdisPacketReader _driver;
    private readonly nint _adapterHandle;
    private readonly Func<NdisCapturedPacket, CancellationToken, ValueTask> _handler;
    private readonly TimeSpan _pollDelay;
    private readonly NdisPacketBuffer[] _batchBuffers;
    private readonly Action? _onBatchCompleted;
    private readonly Action<int, int>? _onTransientRetry;
    private readonly Action<int>? _onDegraded;
    private readonly TimeSpan _transientRetryBaseDelay;
    private int _stopped;
    private int _buffersReleased;
    private int _runStarted;
    // Completed by the run loop's exit sequence (after the buffer release); DisposeAsync parks on
    // it so native batch buffers are never freed under a loop that is still in flight. Always
    // completed successfully — the run's own failure is surfaced through RunAsync's ValueTask.
    private readonly TaskCompletionSource _runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _transientReadRetryCount;
    private long _transientReadIncidentCount;
    private int _transientRetryAttempt;
    private int _degraded;
    private int _lastDegradedNativeError;
    private Thread? _pumpThread;

    public NdisCapturePump(INdisPacketReader driver, nint adapterHandle, Func<NdisCapturedPacket, CancellationToken, ValueTask> handler, NdisCapturePumpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(handler);
        var capacity = options?.BatchCapacity ?? DefaultBatchCapacity;
        // The default is always positive, so a non-positive capacity can only come from options.
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(options), capacity, "BatchCapacity must be positive.");
        _driver = driver;
        _adapterHandle = adapterHandle;
        _handler = handler;
        _pollDelay = options?.PollDelay ?? TimeSpan.FromMilliseconds(1);
        _batchBuffers = new NdisPacketBuffer[capacity];
        for (var index = 0; index < capacity; index++) _batchBuffers[index] = new NdisPacketBuffer();
        _onBatchCompleted = options?.OnBatchCompleted;
        _onTransientRetry = options?.OnTransientRetry;
        _onDegraded = options?.OnDegraded;
        _transientRetryBaseDelay = options?.TransientRetryBaseDelay ?? s_defaultTransientRetryBaseDelay;
    }

    /// <summary>
    /// Starts the dedicated pump thread and returns a <see cref="ValueTask"/> that completes when
    /// its loop exits: successfully on a stop (<see cref="DisposeAsync"/>, degradation, or a
    /// permanent read failure that degrades), faulted on an unexpected failure, or canceled when
    /// <paramref name="cancellationToken"/> is requested. Calling this more than once per pump
    /// instance throws — the batch buffers and the single run-completion signal are owned by the
    /// one run.
    /// </summary>
    public ValueTask RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("NdisCapturePump.RunAsync may only be called once per pump instance.");
        }

        // The closure (thread + outcome source) is a one-time startup allocation; the loop body
        // that follows on the dedicated thread is allocation-free in steady state.
        var outcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunLoop(outcome, cancellationToken))
        {
            IsBackground = true,
            Name = string.Create(CultureInfo.InvariantCulture, $"WinForward.CapturePump.{_adapterHandle:X}"),
        };
        Volatile.Write(ref _pumpThread, thread);
        thread.Start();
        return new ValueTask(outcome.Task);
    }

    /// <summary>The dedicated run thread once <see cref="RunAsync"/> has started; null before.</summary>
    internal Thread? PumpThread => Volatile.Read(ref _pumpThread);

    /// <summary>
    /// Test seam: runs exactly one synchronous loop iteration on the calling thread so the
    /// idle-poll path's allocation behavior is measurable deterministically without thread timing.
    /// Production executes the identical <see cref="RunIteration"/> body on the dedicated pump
    /// thread. Returns false when the iteration ended the loop (stop/degradation).
    /// </summary>
    internal bool RunIterationForTests(CancellationToken cancellationToken) => RunIteration(cancellationToken);

    /// <summary>
    /// The dedicated thread body. Runs the synchronous loop, then its exit sequence (final
    /// batch-completed callback, buffer release, signals), mapping the outcome onto the run's
    /// <see cref="ValueTask"/>. An exception must never escape a dedicated thread — .NET tears the
    /// process down on an unhandled thread exception — and <see cref="_runCompletion"/> must always
    /// be signaled so a parked <see cref="DisposeAsync"/> cannot hang.
    /// </summary>
    private void RunLoop(TaskCompletionSource outcome, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        var canceled = false;
        try
        {
            while (ShouldContinue(cancellationToken))
            {
                if (!RunIteration(cancellationToken)) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            canceled = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        // Loop exit (stop, cancellation, degradation, or failure) must not strand deferred work:
        // the callback runs while the batch buffers are still valid, before their release. It is
        // contained because a throwing wiring callback must not tear down the process.
        try
        {
            _onBatchCompleted?.Invoke();
        }
        catch (Exception callbackException)
        {
            failure ??= callbackException;
        }

        try
        {
            ReleaseBatchBuffers();
        }
        catch (Exception releaseException)
        {
            failure ??= releaseException;
        }

        // Signaled only after the buffers are released, so a disposal parked on this completion
        // resumes into a pump whose native memory is already gone.
        _runCompletion.TrySetResult();

        // The run outcome completes last: awaiting RunAsync observes released buffers, matching the
        // historical async method, whose finally ran before the returned task transitioned.
        if (failure is not null) outcome.TrySetException(failure);
        else if (canceled) outcome.TrySetCanceled(cancellationToken);
        else outcome.TrySetResult();
    }

    /// <summary>
    /// Cancellation surfaces as <see cref="OperationCanceledException"/> (RunAsync's historical
    /// contract); a stop (<see cref="DisposeAsync"/>) is a normal, non-throwing exit.
    /// </summary>
    private bool ShouldContinue(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Volatile.Read(ref _stopped) == 0;
    }

    /// <summary>
    /// One synchronous loop iteration. Returns false when the loop must exit (stop or degraded
    /// read failure); throws on cancellation and on an unexpected handler/read failure, both of
    /// which <see cref="RunLoop"/> maps onto the run outcome.
    /// </summary>
    private bool RunIteration(CancellationToken cancellationToken)
    {
        int readCount;
        try
        {
            readCount = _driver.TryReadPackets(_adapterHandle, _batchBuffers);
            // A healthy read (including an empty poll) closes the failure incident, so a later
            // transient error starts a fresh retry budget.
            _transientRetryAttempt = 0;
        }
        catch (Win32Exception exception)
        {
            if (!NdisNativeCallStatus.IsTransientReadError(exception.NativeErrorCode)
                || !RetryTransientRead(exception.NativeErrorCode, cancellationToken))
            {
                Degrade(exception.NativeErrorCode);
                return false;
            }

            return true;
        }

        if (readCount == 0)
        {
            // The every-iteration callback contract holds on empty queues too: a flush side effect
            // must not wait for the next non-empty batch.
            _onBatchCompleted?.Invoke();
            // Signal checks before pacing so a cancellation/stop that arrived around the read adds
            // no poll-delay latency to the exit.
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _stopped) != 0) return false;
            PaceIdle();
            return true;
        }

        for (var index = 0; index < readCount; index++)
        {
            // NDISAPI contract: reinjection requests must carry the enumeration handle
            // (GetTcpipBoundAdaptersInfo); the captured buffer's m_hAdapter is rejected
            // by the driver with ERROR_INVALID_PARAMETER. See spec/backend/windows-ndisapi.md.
            var packet = NdisCapturedPacket.FromCapture(_batchBuffers[index], _adapterHandle);
            InvokeHandler(packet, cancellationToken);
        }

        _onBatchCompleted?.Invoke();
        return true;
    }

    /// <summary>
    /// Zero-allocation idle pacing: <see cref="Thread.Sleep(TimeSpan)"/> replaces <c>Task.Delay</c>
    /// so an idle pump allocates nothing while keeping the ~1 ms poll cadence. The loop re-checks
    /// cancellation/stop on the next iteration, so a signal adds at most one poll delay of
    /// latency — the same bound as the historical <c>Task.Delay(_pollDelay, token)</c>.
    /// </summary>
    private void PaceIdle() => Thread.Sleep(_pollDelay);

    /// <summary>
    /// Invokes the packet handler and waits for its <see cref="ValueTask"/> to complete before the
    /// caller can process the next slot or start the next batch read. The steady-state
    /// synchronously-completing shape is a plain allocation-free result read; a still-pending
    /// handler blocks this dedicated pump thread (documented deviation from design §3.1 — see the
    /// class doc). <c>AsTask()</c> is only paid on that genuinely-pending path.
    /// </summary>
    private void InvokeHandler(NdisCapturedPacket packet, CancellationToken cancellationToken)
    {
        var pending = _handler(packet, cancellationToken);
        if (pending.IsCompletedSuccessfully)
        {
            // Steady-state path: the handler completed inline, so consuming its result here
            // is a plain allocation-free read.
#pragma warning disable VSTHRD002
            pending.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            return;
        }

        // The dedicated pump thread intentionally blocks until the still-pending handler
        // completes: it is the only thread that may touch the slot, so it must not move on to the
        // next read until the handler is done (no thread-pool thread is parked).
#pragma warning disable VSTHRD002
        pending.AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    /// <summary>
    /// Consumes one transient read failure against the bounded retry budget: sleeps with
    /// exponential backoff (cancellation-honoring) and reports whether the run should keep going.
    /// Returns false when the incident's budget is exhausted — the caller degrades.
    /// </summary>
    private bool RetryTransientRead(int nativeError, CancellationToken cancellationToken)
    {
        var attempt = _transientRetryAttempt + 1;
        if (attempt > TransientRetryMaxAttempts) return false;
        _transientRetryAttempt = attempt;
        Interlocked.Increment(ref _transientReadRetryCount);
        if (attempt == 1) Interlocked.Increment(ref _transientReadIncidentCount);
        _onTransientRetry?.Invoke(nativeError, attempt);
        SleepInterruptible(TransientRetryDelay(attempt), cancellationToken);
        return true;
    }

    /// <summary>
    /// A cancellation-aware blocking sleep for the retry backoff. Blocking the pump's own thread
    /// is safe (no thread-pool starvation); the token's wait handle is created once per token by
    /// the runtime, so prompt cancellation costs no per-retry timer allocation.
    /// </summary>
    private static void SleepInterruptible(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero) return;
        if (!cancellationToken.CanBeCanceled)
        {
            Thread.Sleep(delay);
            return;
        }

        if (cancellationToken.WaitHandle.WaitOne(delay)) cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// One read-only telemetry snapshot of this pump's transient-retry counters and degraded
    /// state (see <see cref="NdisPumpDiagnostics"/>; R7). The control/test seams
    /// (<see cref="PumpThread"/> and <c>RunIterationForTests</c>) stay on the pump itself.
    /// </summary>
    internal NdisPumpDiagnostics Diagnostics => new(
        Volatile.Read(ref _degraded) != 0,
        Volatile.Read(ref _lastDegradedNativeError),
        Interlocked.Read(ref _transientReadRetryCount),
        Interlocked.Read(ref _transientReadIncidentCount));

    private void Degrade(int nativeError)
    {
        Interlocked.Exchange(ref _lastDegradedNativeError, nativeError);
        Interlocked.Exchange(ref _degraded, 1);
        _onDegraded?.Invoke(nativeError);
    }

    private TimeSpan TransientRetryDelay(int attempt)
    {
        var delay = TimeSpan.FromTicks(_transientRetryBaseDelay.Ticks << Math.Min(attempt - 1, 20));
        return delay > s_transientRetryDelayCap ? s_transientRetryDelayCap : delay;
    }

    /// <summary>
    /// Stops the pump and returns only once an in-flight run has fully exited, so the native
    /// batch buffers are never freed while the loop — or a handler it awaits — may still be
    /// using them (previously safe only by the caller convention of awaiting
    /// <see cref="RunAsync"/> first). Disposal signals stop but never cancels the run: the loop
    /// re-checks the stop flag every iteration and its only waits are bounded by the configured
    /// poll delay (default 1 ms), a single transient-retry backoff sleep, or an in-flight handler,
    /// so this await is bounded too. A
    /// run that already started owns the buffer release in its own exit sequence; a pump whose run
    /// never started releases the buffers directly and completes synchronously.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stopped, 1);
        if (Volatile.Read(ref _runStarted) != 0) return new ValueTask(_runCompletion.Task);
        ReleaseBatchBuffers();
        return ValueTask.CompletedTask;
    }

    private void ReleaseBatchBuffers()
    {
        if (Interlocked.Exchange(ref _buffersReleased, 1) != 0) return;
        foreach (var buffer in _batchBuffers) buffer?.Dispose();
    }
}
