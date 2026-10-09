using System.Globalization;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.TestSupport;
using WinForward.Windows;
using Xunit;

namespace WinForward.Runtime.Capture.Tests;

/// <summary>
/// How one capture generation's per-adapter packet-arrival signals reach the pumps and when they are
/// released. The signals are positionally paired with the loop's bindings and owned by the loop, so
/// the pairing and the release ordering are checkable without a driver: a signal disposed while a
/// pump is still parked in it is a use-after-dispose, and a mis-paired list would silently wire one
/// adapter's wake to another's pump.
/// </summary>
public sealed class MultiAdapterCaptureLoopArrivalSignalTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ArrivalSignalsArePositionallyPairedWithTheirBindings()
    {
        // The uneventful adapter ("b", index 1) has no signal, so any wait observed on the first
        // signal can only come from the pump the first binding produced.
        await RunWithSignalsAsync([new TrackedArrivalSignal(), null]).ConfigureAwait(false);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AnAdapterWithTheSecondBindingWaitsOnTheSecondSignal()
    {
        // Mirror of the fact above: with only the second entry populated, a wait proves the second
        // pump — not the first — owns it.
        await RunWithSignalsAsync([null, new TrackedArrivalSignal()]).ConfigureAwait(false);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task EverySignalIsDisposedExactlyOnceAndOnlyAfterThePumpsStop()
    {
        var first = new TrackedArrivalSignal();
        var second = new TrackedArrivalSignal();
        using var cts = new CancellationTokenSource();
        var loop = BuildLoop([first, second]);

        var run = loop.RunAsync(cts.Token);
        // Let both pumps park in their own signal before the cancellation, so a teardown that
        // released the signals first would land on a genuinely parked wait.
        await Task.Delay(50).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        await run.ConfigureAwait(false);
        await loop.DisposeAsync().ConfigureAwait(false);
        await loop.DisposeAsync().ConfigureAwait(false);

        Assert.Equal(1, first.Disposes);
        Assert.Equal(1, second.Disposes);
        Assert.False(first.DisposedWhileParked, "a signal was disposed while a pump was still inside its wait");
        Assert.False(second.DisposedWhileParked, "a signal was disposed while a pump was still inside its wait");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ASignalCountThatDoesNotMatchTheBindingsIsRejected()
    {
        Assert.Throws<ArgumentException>(() => BuildLoop([new TrackedArrivalSignal()]));
    }

    /// <summary>
    /// Runs a loop until the one populated signal has been waited on, then cancels and disposes.
    /// Cancelling on a fixed read count would race: the adapter with no signal spins through its
    /// reads far faster than the other one reaches its wait, so the loop could end before the wait
    /// being asserted ever happened.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task RunWithSignalsAsync(INdisPacketArrivalSignal?[] signals)
    {
        using var cts = new CancellationTokenSource();
        var loop = BuildLoop(signals);
        var run = loop.RunAsync(cts.Token);

        var observed = false;
        for (var index = 0; index < signals.Length && !observed; index++)
        {
            if (signals[index] is not TrackedArrivalSignal tracked) continue;
            observed = await WaitUntilAsync(() => tracked.Waits > 0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.True(observed, string.Create(CultureInfo.InvariantCulture, $"the signal paired with binding {index} was never waited on"));
        }

        await cts.CancelAsync().ConfigureAwait(false);
        await run.ConfigureAwait(false);
        await loop.DisposeAsync().ConfigureAwait(false);

        foreach (var signal in signals)
        {
            if (signal is TrackedArrivalSignal tracked) Assert.Equal(1, tracked.Disposes);
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(5).ConfigureAwait(false);
        }

        return condition();
    }

    [SupportedOSPlatform("windows")]
    private static MultiAdapterCaptureLoop BuildLoop(INdisPacketArrivalSignal?[] signals)
    {
        var adapters = new[] { Adapter("a", 0x10), Adapter("b", 0x11) };
        var readers = new Dictionary<nint, INdisPacketReader>
        {
            [0x10] = new EmptyReader(),
            [0x11] = new EmptyReader(),
        };
        var dispatcher = new FlowDispatcher(CreatePassConfiguration(), new FakeGuard(), new FakeExecutor());
        var processor = new CapturePacketProcessor(dispatcher, FlowBuilders.Slots);
        return new MultiAdapterCaptureLoop(
            new PerHandleReader(readers),
            [.. adapters.Select(adapter => new AdapterCaptureBinding(adapter, FlowBuilders.SlotOf(adapter.StableId, adapter.Generation)))],
            processor,
            TimeSpan.Zero,
            arrivalSignals: signals);
    }

    [SupportedOSPlatform("windows")]
    private static ValidatedConfiguration CreatePassConfiguration() =>
        new(
            new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));

    private static WindowsAdapter Adapter(string id, nint handle) => new(id, id, $@"\DEVICE\{{{id}}}", handle, 1);

    /// <summary>
    /// An arrival signal that counts waits and disposes, and records whether a dispose overlapped a
    /// parked wait — the ordering violation the loop's teardown must not have.
    /// </summary>
    private sealed class TrackedArrivalSignal : INdisPacketArrivalSignal
    {
        private int _inWait;
        private int _waits;
        private int _disposes;

        public int Waits => Volatile.Read(ref _waits);

        public int Disposes => Volatile.Read(ref _disposes);

        public bool DisposedWhileParked { get; private set; }

        public bool Wait(TimeSpan timeout)
        {
            Interlocked.Increment(ref _waits);
            Interlocked.Increment(ref _inWait);
            try
            {
                // Short enough to keep the run brief, long enough that a mis-ordered dispose lands
                // while a pump is genuinely inside the wait.
                Thread.Sleep(TimeSpan.FromMilliseconds(1));
                return false;
            }
            finally
            {
                Interlocked.Decrement(ref _inWait);
            }
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposes);
            if (Volatile.Read(ref _inWait) != 0) DisposedWhileParked = true;
        }
    }

    private sealed class PerHandleReader(Dictionary<nint, INdisPacketReader> readers) : INdisPacketReader
    {
        public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers) => readers[adapterHandle].TryReadPackets(adapterHandle, buffers);
    }

    /// <summary>An always-empty reader, so every idle iteration reaches the pacing call.</summary>
    private sealed class EmptyReader : INdisPacketReader
    {
        public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers) => 0;
    }
}
