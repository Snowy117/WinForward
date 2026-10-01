using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using WinForward.NdisApi;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.NdisApi.Tests;

/// <summary>
/// The capture pump's idle path once an arrival signal is installed (finding F5.2): one bounded
/// wait per idle iteration instead of sleep pacing, the sleep fallback when no signal exists, and
/// the disposal/allocation bounds that go with a blocking wait. Kept beside
/// <see cref="NdisCapturePumpTests"/> rather than inside it — the batch-processing facts and these
/// are different subjects, and the file-size convention caps each at 400 effective lines.
/// </summary>
public sealed class NdisCapturePumpIdleWaitTests
{
    /// <summary>
    /// The idle wait's exact shape: one bounded arrival wait per idle iteration, at the configured
    /// bound, and no sleep pacing. The 5 s poll delay against a 1 ms wait makes "not a sleep" a
    /// ~5000× discrimination rather than a timing guess.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task IdleIterationsIssueExactlyOneBoundedWaitAndNoSleepPacing()
    {
        const int iterations = 16;
        var idleWaitTimeout = TimeSpan.FromMilliseconds(1);
        var signal = new CountingArrivalSignal();
        var reader = new ScriptedReader([_ => 0]);
        await using var pump = new NdisCapturePump(reader, 0x4C, static (_, _) => ValueTask.CompletedTask,
            new NdisCapturePumpOptions { PollDelay = TimeSpan.FromSeconds(5), IdleWaitTimeout = idleWaitTimeout, PacketArrivalSignal = signal });

        var watch = Stopwatch.StartNew();
        for (var iteration = 0; iteration < iterations; iteration++) Assert.True(pump.RunIterationForTests(CancellationToken.None));
        watch.Stop();

        Assert.Equal(iterations, reader.Calls);
        Assert.Equal(iterations, signal.Waits);
        Assert.All(signal.ObservedTimeouts, timeout => Assert.Equal(idleWaitTimeout, timeout));
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250), string.Create(CultureInfo.InvariantCulture, $"{iterations} idle iterations took {watch.Elapsed.TotalMilliseconds:F1} ms; sleeping the 5 s poll delay would need at least 80 s."));
    }

    /// <summary>
    /// The fallback that keeps an event-less adapter byte-identical to the poll shape: no signal
    /// means no wait at all and the poll delay still paces the loop.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task WithoutAnArrivalSignalTheIdlePathKeepsThePollDelaySleep()
    {
        var reader = new ScriptedReader([_ => 0]);
        await using var pump = new NdisCapturePump(reader, 0x4D, static (_, _) => ValueTask.CompletedTask,
            new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(15) });

        var watch = Stopwatch.StartNew();
        for (var iteration = 0; iteration < 4; iteration++) Assert.True(pump.RunIterationForTests(CancellationToken.None));
        watch.Stop();

        Assert.Equal(4, reader.Calls);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(40), string.Create(CultureInfo.InvariantCulture, $"four idle iterations at a 15 ms poll delay took only {watch.Elapsed.TotalMilliseconds:F1} ms"));
    }

    /// <summary>
    /// A signal raised while the pump is parked ends the wait long before the timeout: the packet
    /// the wake makes readable is dispatched, which is the whole point of the arrival signal.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AnArrivalSignalWakeEndsTheIdleWaitBeforeTheTimeout()
    {
        using var cts = new CancellationTokenSource();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<byte>();
        var reader = new ScriptedReader(
            [
                _ =>
                {
                    parked.TrySetResult();
                    return 0;
                },
                slots =>
                {
                    slots[0].SetFrame([0x70, 0xAA, 0xBB], NdisApiAbi.PacketFlagOnReceive, 0x99, flags: 0x40);
                    return 1;
                },
                _ =>
                {
                    // ReSharper disable once AccessToDisposedClosure // This scripted-reader cancel step ends the pump's third read; the run is awaited (Assert.ThrowsAnyAsync) before the using scope disposes cts.
                    cts.Cancel();
                    return 0;
                },
            ]);
        using var arrival = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var signal = new NdisPacketArrivalSignal(arrival);
        await using var pump = new NdisCapturePump(reader, 0x7A, (packet, _) =>
        {
            observed.Add(packet.Buffer.GetFrame()[0]);
            return ValueTask.CompletedTask;
        }, new NdisCapturePumpOptions { IdleWaitTimeout = TimeSpan.FromSeconds(10), PacketArrivalSignal = signal });

        var run = pump.RunAsync(cts.Token).AsTask();
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50); // the pump reaches the wait only after the empty read returns
        arrival.Set();

        // The 5 s bound is the discrimination: a lost signal would leave the pump in a 10 s wait.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("p"u8.ToArray(), observed);
    }

    /// <summary>
    /// The disposal contract with a parked pump: the stop flag is checked before the wait, so a
    /// disposal costs at most the wait already in flight — the bound the class doc states.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DisposeWhileParkedInTheIdleWaitCompletesWithinTheTimeout()
    {
        const int idleWaitTimeoutMs = 200;
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new ScriptedReader(
            [
                _ =>
                {
                    parked.TrySetResult();
                    return 0;
                },
            ]);
        using var arrival = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var signal = new NdisPacketArrivalSignal(arrival);
        var pump = new NdisCapturePump(reader, 0x7B, static (_, _) => ValueTask.CompletedTask,
            new NdisCapturePumpOptions { IdleWaitTimeout = TimeSpan.FromMilliseconds(idleWaitTimeoutMs), PacketArrivalSignal = signal });

        var run = pump.RunAsync(CancellationToken.None).AsTask();
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50); // let the pump enter the wait

        var watch = Stopwatch.StartNew();
        var dispose = pump.DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(idleWaitTimeoutMs * 4), string.Create(CultureInfo.InvariantCulture, $"disposal with a parked pump took {watch.Elapsed.TotalMilliseconds:F1} ms against a {idleWaitTimeoutMs} ms wait."));
    }

    /// <summary>
    /// The arrival wait's zero-allocation gate, over the production <see cref="NdisPacketArrivalSignal"/>
    /// rather than a fake, so the real <c>WaitOne(0)</c> entry point is inside the window. The
    /// window contract is the suite's standard one (hot-path.md, "Allocation-gate stability").
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task IdleWaitIterationsAllocateNoManagedBytes()
    {
        const int iterations = 1_000;
        var reader = new ScriptedReader([_ => 0]);
        using var arrival = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var signal = new NdisPacketArrivalSignal(arrival);
        await using var pump = new NdisCapturePump(reader, 0x7C, static (_, _) => ValueTask.CompletedTask,
            new NdisCapturePumpOptions { PacketArrivalSignal = signal, IdleWaitTimeout = TimeSpan.Zero });

        for (var warm = 0; warm < 64; warm++) pump.RunIterationForTests(CancellationToken.None);

        const int maximumProbeBatches = 8;
        var stabilized = false;
        for (var probe = 0; probe < maximumProbeBatches && !stabilized; probe++)
        {
            var probeThreadId = Environment.CurrentManagedThreadId;
            var probeBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < iterations; index++) pump.RunIterationForTests(CancellationToken.None);
            stabilized = Environment.CurrentManagedThreadId == probeThreadId && GC.GetAllocatedBytesForCurrentThread() == probeBefore;
        }
        Assert.True(stabilized, "the pump's arrival-wait path never became allocation-stable");

        var measuredThreadId = Environment.CurrentManagedThreadId;
        var callsBefore = reader.Calls;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var keepGoing = true;
        for (var index = 0; index < iterations; index++) keepGoing &= pump.RunIterationForTests(CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(keepGoing);
        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(0, allocated);
        Assert.Equal(iterations, reader.Calls - callsBefore);
    }
}
