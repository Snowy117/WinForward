using System.Runtime.Versioning;
using WinForward.NdisApi;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class NdisCapturePumpTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PumpProcessesBatchInArrivalOrder()
    {
        var observed = new List<byte>();
        var handles = new List<nint>();
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader(
            [
                slots =>
                {
                    Fill(slots[0], 0x10);
                    Fill(slots[1], 0x11);
                    Fill(slots[2], 0x12);
                    return 3;
                },
                _ =>
                {
                    cts.Cancel();
                    return 0;
                },
            ]);

        await using var pump = new NdisCapturePump(reader, (nint)0x55, CaptureHandler(observed, handles), new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(cts.Token).AsTask());

        Assert.Equal(new byte[] { 0x10, 0x11, 0x12 }, observed);
        Assert.All(handles, handle => Assert.Equal((nint)0x55, handle));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PumpProcessesOnlyFilledSlotsOfPartialBatch()
    {
        var observed = new List<byte>();
        var handles = new List<nint>();
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader(
            [
                slots =>
                {
                    Fill(slots[0], 0x20);
                    Fill(slots[1], 0x21);
                    // Slots 2..7 stay untouched: the driver returned fewer packets than requested.
                    return 2;
                },
                _ =>
                {
                    cts.Cancel();
                    return 0;
                },
            ]);

        await using var pump = new NdisCapturePump(reader, (nint)0x66, CaptureHandler(observed, handles), new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1), BatchCapacity = 8 });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(cts.Token).AsTask());

        Assert.Equal(" !"u8.ToArray(), observed);
        Assert.Equal(2, reader.Calls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PumpPollsWhenQueueIsEmpty()
    {
        var observed = new List<byte>();
        var handles = new List<nint>();
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader(
            [
                _ => 0,
                _ => 0,
                _ =>
                {
                    cts.Cancel();
                    return 0;
                },
            ]);

        await using var pump = new NdisCapturePump(reader, (nint)0x77, CaptureHandler(observed, handles), new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1) });

        // Two empty polls go through the poll-delay branch; the cancelled third poll surfaces as
        // the pump's normal cancellation propagation, not a failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(cts.Token).AsTask());

        Assert.Empty(observed);
        Assert.Equal(3, reader.Calls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PumpReleasesBatchBuffersExactlyOnce()
    {
        var buffers = new List<NdisPacketBuffer>();
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader(
            [
                slots =>
                {
                    Fill(slots[0], 0x30);
                    return 1;
                },
                _ =>
                {
                    cts.Cancel();
                    return 0;
                },
            ]);

        var pump = new NdisCapturePump(reader, (nint)0x88, (packet, _) => { buffers.Add(packet.Buffer); return ValueTask.CompletedTask; }, new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(cts.Token).AsTask());
        await pump.DisposeAsync();
        await pump.DisposeAsync();

        var released = Assert.Single(buffers);
        // Disposed native buffers refuse further frame access, and the double dispose stays idempotent.
        Assert.Throws<ObjectDisposedException>(() => released.GetFrame());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [SupportedOSPlatform("windows")]
    public void PumpRejectsNonPositiveBatchCapacity(int batchCapacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NdisCapturePump(new ScriptedReader([_ => 0]), (nint)1, static (_, _) => ValueTask.CompletedTask, new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1), BatchCapacity = batchCapacity }));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PumpRejectsNullDriverAndHandler()
    {
        Assert.Throws<ArgumentNullException>(() => new NdisCapturePump(null!, (nint)1, static (_, _) => ValueTask.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new NdisCapturePump(new ScriptedReader([_ => 0]), (nint)1, null!));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PumpDoesNotReuseBatchSlotWhileHandlerIsInFlight()
    {
        // The zero-copy capture contract: a batch buffer stays untouched until its packet's
        // handler completes, so in-place reinjection and native-span parsing inside the handler
        // cannot observe a reused slot. The handler gates on a completion source; the second
        // read must not start before the gate opens.
        using var cts = new CancellationTokenSource();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReadEnteredBeforeHandlerCompleted = false;

        var reader = new ScriptedReader(
            [
                slots =>
                {
                    Fill(slots[0], 0x50);
                    return 1;
                },
                slots =>
                {
                    secondReadEnteredBeforeHandlerCompleted = !handlerReleased.Task.IsCompleted;
                    Fill(slots[0], 0x51);
                    return 1;
                },
                _ =>
                {
                    cts.Cancel();
                    return 0;
                },
            ]);

        await using var pump = new NdisCapturePump(reader, (nint)0x99, (_, _) =>
        {
            if (!handlerStarted.TrySetResult()) return ValueTask.CompletedTask;
            return new ValueTask(handlerReleased.Task);
        }, new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1) });

        var pumpTask = pump.RunAsync(cts.Token).AsTask();
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.False(secondReadEnteredBeforeHandlerCompleted, "The pump started a second batch read before the previous handler completed.");
        handlerReleased.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pumpTask);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DisposeDuringRunWaitsForTheRunLoopToExit()
    {
        // Mid-run disposal must never free the batch buffers under the loop's feet:
        // DisposeAsync parks on the run's completion source, which the loop's finally signals
        // only after the final batch callback and the buffer release. The gated reader makes
        // "the run is still in flight" a deterministic fact rather than a scheduler race.
        using var cts = new CancellationTokenSource();
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new GatedReader(readEntered, readReleased);

        var pump = new NdisCapturePump(reader, (nint)0xBB, static (_, _) => ValueTask.CompletedTask, new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1) });

        // Task.Run: the run's synchronous prefix blocks inside the gated read, so it cannot
        // start on the test thread.
        var runTask = Task.Run(() => pump.RunAsync(cts.Token));
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeTask = pump.DisposeAsync().AsTask();
        await Task.Delay(50); // negative window, same shape as the slot-reuse regression above
        Assert.False(disposeTask.IsCompleted, "DisposeAsync completed while the run loop was still inside its first read.");

        readReleased.SetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DisposeBeforeAnyRunCompletesSynchronously()
    {
        var pump = new NdisCapturePump(new ScriptedReader([_ => 0]), (nint)1, static (_, _) => ValueTask.CompletedTask);

        var dispose = pump.DisposeAsync();

        Assert.True(dispose.IsCompletedSuccessfully);
        await dispose;
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DedicatedPumpThreadExitsAndIsBackgroundAfterRun()
    {
        // Lifecycle: RunAsync runs the loop on one dedicated background thread. When the loop
        // exits (here via cancellation) that thread must have terminated — no thread leak across
        // start/stop, and the background flag means the pump can never keep the process alive.
        // The gated reader parks the thread so its live state (IsBackground/IsAlive) is readable.
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var pump = new NdisCapturePump(new GatedReader(readEntered, readReleased), (nint)0x44, static (_, _) => ValueTask.CompletedTask, new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1) });

        var run = pump.RunAsync(cts.Token).AsTask();
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var thread = pump.PumpThread;
        Assert.NotNull(thread);
        Assert.True(thread.IsAlive, "the dedicated pump thread should be parked in its first read");
        Assert.True(thread.IsBackground, "the pump must run on a background thread");

        await cts.CancelAsync();
        readReleased.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "the dedicated pump thread did not exit after the run loop ended");
        Assert.False(thread.IsAlive);

        await pump.DisposeAsync();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DisposeStopsTheLoopNormallyAndReapsTheThread()
    {
        // A DisposeAsync stop (without cancellation) is a normal, non-throwing loop exit, and it
        // also terminates the dedicated thread.
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new NdisCapturePump(new GatedReader(readEntered, readReleased), (nint)0x45, static (_, _) => ValueTask.CompletedTask, new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1) });

        var run = pump.RunAsync(CancellationToken.None).AsTask();
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = pump.DisposeAsync();
        readReleased.SetResult();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pump.PumpThread!.Join(TimeSpan.FromSeconds(5)), "the dedicated pump thread did not exit after disposal");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task IdlePollIterationsAllocateNoManagedBytes()
    {
        // Allocation gate: an idle poll iteration — the read, the batch-completed callback,
        // and the zero-allocation Thread.Sleep pacing — must not touch the managed heap. The seam
        // runs the identical synchronous iteration body production runs on the dedicated thread,
        // so this gates the real loop body rather than a re-implementation. The pump is disposed
        // so the constructor's native batch buffers are freed (they have no finalizer).
        await using var pump = new NdisCapturePump(new ScriptedReader([_ => 0]), (nint)0x46, static (_, _) => ValueTask.CompletedTask, new NdisCapturePumpOptions { PollDelay = TimeSpan.Zero });

        // Warm the JIT outside the measured window.
        for (var warm = 0; warm < 64; warm++) pump.RunIterationForTests(CancellationToken.None);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var keepGoing = true;
        const int iterations = 1_000;
        for (var index = 0; index < iterations; index++) keepGoing &= pump.RunIterationForTests(CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(keepGoing);
        Assert.Equal(0, allocated);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SecondRunAsyncThrowsWithoutDisturbingTheFirstRun()
    {
        // Run-once guard: a second start must fail fast instead of launching a second loop over the
        // batch buffers and run-completion signal the first run owns — which would double-read the
        // shared slots and corrupt the zero-copy slot contract.
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pump = new NdisCapturePump(new GatedReader(readEntered, readReleased), (nint)0x47, static (_, _) => ValueTask.CompletedTask, new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1) });

        var run = pump.RunAsync(CancellationToken.None).AsTask();
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

#pragma warning disable CA2012 // The run-once guard throws synchronously before any ValueTask is produced; the Action-bound lambda pins exactly that synchronous exception and nothing consumes a result.
        Assert.Throws<InvalidOperationException>(() => pump.RunAsync(CancellationToken.None));
#pragma warning restore CA2012

        var dispose = pump.DisposeAsync();
        readReleased.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Func<NdisCapturedPacket, CancellationToken, ValueTask> CaptureHandler(List<byte> observed, List<nint> handles) =>
        (packet, _) =>
        {
            observed.Add(packet.Buffer.GetFrame()[0]);
            handles.Add(packet.AdapterHandle);
            return ValueTask.CompletedTask;
        };

    private static void Fill(NdisPacketBuffer buffer, byte marker) =>
        buffer.SetFrame([marker, 0xAA, 0xBB], NdisApiAbi.PacketFlagOnReceive, (nint)0x99, flags: 0x40);

    /// <summary>
    /// A reader that parks every read until the test releases it, making "the run loop is in
    /// flight" deterministic. The bounded wait keeps a lost release from hanging the suite.
    /// </summary>
    private sealed class GatedReader(TaskCompletionSource entered, TaskCompletionSource release) : INdisPacketReader
    {
        public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
        {
            entered.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(10));
            return 0;
        }
    }
}
