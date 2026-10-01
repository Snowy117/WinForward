using System.Runtime.Versioning;
using WinForward.Benchmarks.Stability;
using WinForward.NdisApi;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.NdisApi.Tests;

/// <summary>
/// The pump's read-call contract at the <see cref="INdisPacketReader"/> seam (research F5.1): exactly
/// one <c>TryReadPackets</c> call per loop iteration — one for an empty poll and one for a whole batch,
/// never one per packet — and exactly one handler invocation per packet returned. The F5.1 proposal
/// ("speculative read halves the IOCTLs under load") changes the driver's internal queue-query + read
/// pair, which sits <em>below</em> this seam in <c>NdisApiDriver.TryReadPackets</c>; the counts gated
/// here are the seam-level half that makes the driver-internal pair the only remaining candidate, and
/// the driver's own count stays a Windows measurement.
/// </summary>
public sealed class CapturePumpReadCallTests
{
    private const nint AdapterHandle = 0x2A;

    private static readonly byte[] s_frame = new byte[64];

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PumpMakesExactlyOneReadCallPerPollAndPerBatch()
    {
        // Script: two empty polls, a full 4-slot batch, a 3-slot partial batch, one empty poll, then a
        // cancelling read that ends the run. One read per entry is the contract, so these are exact —
        // and seven packets for six reads is what separates "one read per batch" from "one per packet".
        using var completion = new CancellationTokenSource();
        var reader = new CountingCaptureReader(
        [
            static (_, _) => 0,
            static (_, _) => 0,
            static (_, buffers) => Fill(buffers, offset: 0, count: 4),
            static (_, buffers) => Fill(buffers, offset: 0, count: 3),
            static (_, _) => 0,
            (_, _) =>
            {
                // ReSharper disable once AccessToDisposedClosure // the run is awaited (Assert.ThrowsAnyAsync) before the using scope disposes completion.
                completion.Cancel();
                return 0;
            },
        ]);
        var dispatched = new List<nint>();
        var callbacks = new CallbackCounter();
        await using var pump = new NdisCapturePump(
            reader,
            AdapterHandle,
            (packet, _) =>
            {
                dispatched.Add(packet.AdapterHandle);
                return ValueTask.CompletedTask;
            },
            new NdisCapturePumpOptions { PollDelay = TimeSpan.Zero, BatchCapacity = 4, OnBatchCompleted = callbacks.Increment });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(completion.Token).AsTask()).ConfigureAwait(false);

        // Six iterations, six reads: no speculative extra read, no skipped poll, and the 4-packet batch
        // cost one read call rather than four. The returned-count sequence is the iteration shape itself.
        Assert.Equal(6, reader.ReadCalls);
        Assert.Equal([0, 0, 4, 3, 0, 0], reader.ObservedCounts());
        Assert.Equal(4, reader.EmptyReads);
        Assert.Equal(2, reader.BatchReads);
        Assert.Equal(7, reader.PacketsReturned);
        Assert.Equal(7, dispatched.Count);
        Assert.All(dispatched, handle => Assert.Equal(AdapterHandle, handle));

        // The batch-completed hook fires once per iteration plus once on run exit, which is what makes
        // it the poll counter the stability scenario's idle row uses.
        Assert.Equal(7, callbacks.Count);
    }

    /// <summary>
    /// The idle-wait shape at the read seam: an iteration with an arrival signal installed issues
    /// exactly one read and exactly one wait — the signal replaces the poll delay, it never adds a
    /// second read or turns the wait into a re-read loop.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SignalInstalledIdleIterationIssuesOneReadAndOneWait()
    {
        var signal = new CountingArrivalSignal();
        var reader = new CountingCaptureReader([static (_, _) => 0]);
        await using var pump = new NdisCapturePump(reader, AdapterHandle + 1, static (_, _) => ValueTask.CompletedTask,
            new NdisCapturePumpOptions { PollDelay = TimeSpan.Zero, IdleWaitTimeout = TimeSpan.Zero, PacketArrivalSignal = signal });

        for (var iteration = 0; iteration < 8; iteration++) Assert.True(pump.RunIterationForTests(CancellationToken.None));

        Assert.Equal(8, reader.ReadCalls);
        Assert.Equal(8, signal.Waits);
    }

    /// <summary>
    /// The idle-path zero-allocation invariant, held for the counting shape this file's gate uses: a
    /// probe that allocated per read would perturb the very loop it counts, and the pump's idle
    /// iteration — read, batch-completed callback, zero-allocation pacing — must stay byte-free. The
    /// seam runs the identical synchronous body production runs on the dedicated pump thread.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task CountingReaderIdleIterationsAllocateNoManagedBytes()
    {
        const int iterations = 1_000;
        var reader = new CountingCaptureReader([static (_, _) => 0]);
        await using var pump = new NdisCapturePump(reader, AdapterHandle + 1, static (_, _) => ValueTask.CompletedTask, new NdisCapturePumpOptions { PollDelay = TimeSpan.Zero });

        // Warm the JIT outside the measured window.
        for (var warm = 0; warm < 64; warm++) pump.RunIterationForTests(CancellationToken.None);

        // Open the measured window only after the instrument itself is quiet: the per-thread counter
        // can move by a host-level lump that no driven code caused (hot-path.md, "Allocation-gate
        // stability"). Every probe batch must read an exactly-zero delta, so a genuine per-call
        // allocation still fails before the window opens.
        const int maximumProbeBatches = 8;
        var stabilized = false;
        for (var batch = 0; batch < maximumProbeBatches && !stabilized; batch++)
        {
            var probeThreadId = Environment.CurrentManagedThreadId;
            var probeBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < iterations; index++) pump.RunIterationForTests(CancellationToken.None);
            stabilized = Environment.CurrentManagedThreadId == probeThreadId && GC.GetAllocatedBytesForCurrentThread() == probeBefore;
        }
        Assert.True(stabilized, "the pump idle path never became allocation-stable");

        var measuredThreadId = Environment.CurrentManagedThreadId;
        var callsBefore = reader.ReadCalls;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var keepGoing = true;
        for (var index = 0; index < iterations; index++) keepGoing &= pump.RunIterationForTests(CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(keepGoing);
        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(0, allocated);
        Assert.Equal(iterations, reader.ReadCalls - callsBefore);
    }

    /// <summary>
    /// The documented probe invocation (<c>--stability --scenario pump</c>) resolves to the pump
    /// scenario, and that scenario stays out of the shared stability sweep like every other micro probe.
    /// </summary>
    [Fact]
    public void PumpScenarioIsSelectableAndExcludedFromAll()
    {
        Assert.Equal(SoakScenario.Pump, SoakOptions.Parse(["--scenario", "pump"]).Scenario);

        var scenarios = SoakRunner.SelectScenarios(SoakScenario.Pump);
        var (name, _) = Assert.Single(scenarios);
        Assert.Equal("pump", name);
        Assert.DoesNotContain(SoakRunner.SelectScenarios(SoakScenario.All), entry => string.Equals(entry.Name, "pump", StringComparison.Ordinal));
    }

    private static int Fill(NdisPacketBuffer[] buffers, int offset, int count)
    {
        for (var index = 0; index < count; index++) buffers[offset + index].SetFrame(s_frame, NdisApiAbi.PacketFlagOnReceive, AdapterHandle);
        return count;
    }

    /// <summary>Iteration counter for the pump's batch-completed callback; the pump thread is the only writer.</summary>
    private sealed class CallbackCounter
    {
        public int Count { get; private set; }

        public void Increment() => Count++;
    }

}
