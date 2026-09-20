using System.Globalization;
using WinForward.NdisApi;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class NdisAdapterGateMapTests
{
    [Fact]
    public void SameHandleResolvesToSameGateInstance()
    {
        var map = new NdisAdapterGateMap();
        var gate = map.Get(1);

        Assert.Same(gate, map.Get(1));
    }

    [Fact]
    public void DistinctHandlesResolveToDistinctGates()
    {
        var map = new NdisAdapterGateMap();

        var first = map.Get(1);
        var second = map.Get(2);

        Assert.False(ReferenceEquals(first, second));
        Assert.Equal(2, map.GetMaxConcurrentCalls().Count);
    }

    [Fact]
    public async Task MapCreationIsThreadSafeUnderStress()
    {
        const int handleCount = 16;
        const int resolvesPerHandle = 64;
        var map = new NdisAdapterGateMap();
        var resolved = new NdisNativeCallGate[handleCount * resolvesPerHandle];

        var resolves = Enumerable.Range(0, resolved.Length)
            .Select(slot => Task.Run(() => resolved[slot] = map.Get((slot % handleCount) + 1)))
            .ToArray();
        await Task.WhenAll(resolves);

        for (var handle = 0; handle < handleCount; handle++)
        {
            var canonical = resolved[handle];
            for (var resolve = 1; resolve < resolvesPerHandle; resolve++)
            {
                var slot = handle + (resolve * handleCount);
                Assert.True(ReferenceEquals(canonical, resolved[slot]), string.Create(CultureInfo.InvariantCulture, $"adapter {handle + 1} resolve #{resolve} returned a different gate instance"));
            }
        }

        Assert.Equal(handleCount, map.GetMaxConcurrentCalls().Count);
    }

    [Fact]
    public async Task SameHandleStaysSerializedWhileDistinctHandlesProceedInParallel()
    {
        var map = new NdisAdapterGateMap();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstOnAdapterA = Task.Run(() =>
        {
            using var gateLease = map.Get(0xA).Enter();
            firstEntered.SetResult();
#pragma warning disable MA0042 // The lease wraps a Monitor (thread-affine): awaiting would resume on another thread and Monitor.Exit in the lease would throw SynchronizationLockException. The deliberate synchronous block keeps the gate held on this thread while the test proves serialization.
            releaseFirst.Task.GetAwaiter().GetResult();
#pragma warning restore MA0042
        });
        await firstEntered.Task;

        // A second caller on adapter B must acquire its gate while adapter A's gate is held:
        // with the per-adapter topology this completes immediately; a process-wide gate would block.
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOnAdapterB = Task.Run(() =>
        {
            using var gateLease = map.Get(0xB).Enter();
            secondEntered.SetResult();
        });
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A second caller on the SAME adapter must stay blocked until the holder releases.
        var sameHandleContended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOnAdapterA = Task.Run(() =>
        {
            using var gateLease = map.Get(0xA).Enter();
            sameHandleContended.SetResult();
        });
        await Assert.ThrowsAsync<TimeoutException>(() => sameHandleContended.Task.WaitAsync(TimeSpan.FromMilliseconds(200)));

        releaseFirst.SetResult();
        await Task.WhenAll(firstOnAdapterA, secondOnAdapterB, secondOnAdapterA);
    }

    [Fact]
    public async Task BlockedGateDoesNotStallUnrelatedHandleLookups()
    {
        var map = new NdisAdapterGateMap();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = Task.Run(() =>
        {
            using var gateLease = map.Get(1).Enter();
            firstEntered.SetResult();
#pragma warning disable MA0042 // The lease wraps a Monitor (thread-affine): awaiting would resume on another thread and Monitor.Exit in the lease would throw SynchronizationLockException. The deliberate synchronous block keeps the gate held on this thread while the test proves serialization.
            releaseFirst.Task.GetAwaiter().GetResult();
#pragma warning restore MA0042
        });
        await firstEntered.Task;

        // Lookups (including for the blocked handle) must stay responsive while a gate is held:
        // the map lock guards the lookup only, never the gate entry itself.
        var lookups = Task.Run(() =>
        {
            for (var index = 0; index < 100_000; index++) map.Get(1 + (index & 3));
        });
        await lookups.WaitAsync(TimeSpan.FromSeconds(5));

        releaseFirst.SetResult();
        await holder;
    }
}
