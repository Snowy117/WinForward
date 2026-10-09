using System.Diagnostics;
using System.Globalization;
using WinForward.Runtime;
using Xunit;

namespace WinForward.NdisApi.Tests;

/// <summary>
/// The composite arrival signal's facts: the two-handle park the pump's idle path waits in once a
/// pipeline wake event is composed with the driver's borrowed signal, the registry's ownership of
/// exactly one event per adapter handle, and the exact-window allocation gate over the production
/// wait entry point (allocation-gates.md's exact-window class list).
/// </summary>
public sealed class CompositePacketArrivalSignalTests
{
    /// <summary>
    /// Both handles end the park, and the caller's single timeout is what bounds the combined wait:
    /// the pump cannot distinguish a driver wake from a pipeline wake, and neither can extend the
    /// park past the idle bound.
    /// </summary>
    [Fact]
    public void TheCompositeWaitsOnBothHandlesWithTheSameTimeout()
    {
        using var driverEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var driver = new NdisPacketArrivalSignal(driverEvent);
        using var wake = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var composite = new CompositePacketArrivalSignal(driver, wake);

        // Neither handle: the park honours the caller's timeout and reports "not signalled".
        var watch = Stopwatch.StartNew();
        Assert.False(composite.Wait(TimeSpan.FromMilliseconds(30)));
        watch.Stop();
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(20), string.Create(CultureInfo.InvariantCulture, $"a 30 ms park returned after {watch.Elapsed.TotalMilliseconds:F1} ms; the timeout was not applied to the combined wait"));

        // The driver half: the borrowed signal still ends the park on its own.
        driverEvent.Set();
        Assert.True(composite.Wait(TimeSpan.FromSeconds(5)));

        // The owned half: an auto-reset event raised before the pump parks is retained and ends it.
        wake.Set();
        Assert.True(composite.Wait(TimeSpan.FromSeconds(5)));

        // A non-positive timeout never blocks.
        watch.Restart();
        Assert.False(composite.Wait(TimeSpan.Zero));
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), string.Create(CultureInfo.InvariantCulture, $"a zero-timeout park took {watch.Elapsed.TotalMilliseconds:F1} ms"));
    }

    /// <summary>
    /// The registry owns one event per adapter handle — a re-registration (the next generation's
    /// driver signal for the same adapter) reuses it instead of accumulating handles — and its
    /// disposal releases only that event, never the borrowed driver signal the capture loop owns.
    /// </summary>
    [Fact]
    public void TheRegistryKeepsOneOwnedEventPerAdapterAndReleasesOnlyThatEvent()
    {
        var registry = new FlowAttributionWakeRegistry();
        using var firstEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var firstDriver = new NdisPacketArrivalSignal(firstEvent);
        _ = registry.Register(0x21, firstDriver);

        using var replacementEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var replacementDriver = new NdisPacketArrivalSignal(replacementEvent);
        var replacement = registry.Register(0x21, replacementDriver);

        Assert.Equal(1, registry.Count);
        Assert.Equal(1, registry.OwnedEventCount);
        registry.Signal(0x21);
        Assert.True(replacement.Wait(TimeSpan.Zero), "the re-registered composite must wait on the reused event");

        registry.Dispose();

        // The registry's own event is gone: a signal is a no-op and the park reports "not signalled"
        // rather than throwing out of the pump's idle path.
        registry.Signal(0x21);
        Assert.False(replacement.Wait(TimeSpan.Zero));

        // The borrowed driver signals are untouched: the capture loop, not the registry, owns them.
        firstEvent.Set();
        Assert.True(firstDriver.Wait(TimeSpan.Zero), "the registry disposed a borrowed driver signal");
        replacementEvent.Set();
        Assert.True(replacementDriver.Wait(TimeSpan.Zero), "the registry disposed a borrowed driver signal");
    }

    /// <summary>
    /// The composite park's zero-allocation gate: the production two-handle wait
    /// (<see cref="CompositePacketArrivalSignal.Wait"/>) over two unsignaled handles at a zero
    /// timeout allocates nothing. The window contract is the suite's standard one (allocation-gates.md,
    /// "Allocation-gate stability"): the exact zero, the unchanged measured thread, and the
    /// call-count backstop.
    /// </summary>
    [Fact]
    public void CompositeArrivalWaitAllocatesNoManagedBytes()
    {
        const int iterations = 1_000;
        using var driverEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var driver = new NdisPacketArrivalSignal(driverEvent);
        using var wake = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var composite = new CompositePacketArrivalSignal(driver, wake);

        for (var warm = 0; warm < 64; warm++) _ = composite.Wait(TimeSpan.Zero);

        const int maximumProbeBatches = 8;
        var stabilized = false;
        for (var probe = 0; probe < maximumProbeBatches && !stabilized; probe++)
        {
            var probeThreadId = Environment.CurrentManagedThreadId;
            var probeBefore = GC.GetAllocatedBytesForCurrentThread();
            var probeSignalled = false;
            for (var index = 0; index < iterations; index++) probeSignalled |= composite.Wait(TimeSpan.Zero);
            stabilized = Environment.CurrentManagedThreadId == probeThreadId && GC.GetAllocatedBytesForCurrentThread() == probeBefore && !probeSignalled;
        }

        Assert.True(stabilized, "the composite arrival wait never became allocation-stable");

        var measuredThreadId = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var signalled = false;
        for (var index = 0; index < iterations; index++) signalled |= composite.Wait(TimeSpan.Zero);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(signalled, "the gate relies on an unsignaled park, not on a consumed signal");
        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(0, allocated);
    }
}
