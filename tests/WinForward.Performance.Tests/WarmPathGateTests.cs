using System.Net;
using WinForward.Core;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Performance.Tests;

/// <summary>
/// The structural proof class for the warm packet path: the bucketed activity boundaries of Step 2 and
/// the direct-mapped warm cache's facts — gate/clock counts, the collision fallback, the removal
/// invalidation and the transport-tuple invariant the cache's corroboration rests on. Unlike the
/// allocation gate classes this one drives concurrency structure, so its repetition proof is its own
/// (see the warm-path task record) rather than an entry in the allocation-gate loop.
/// </summary>
public sealed class WarmPathGateTests
{
    [Fact]
    public void ActivityBucketClockReadsTheClockExactlyOncePerTickAndNeverOnAWarmResolve()
    {
        var time = new CountingTimeProvider(DateTimeOffset.UnixEpoch);
        var clock = new ActivityBucketClock(time);
        Assert.Equal(1, time.Reads);                        // construction publishes one bucket
        Assert.Equal(ActivityBucket.FromUtc(DateTimeOffset.UnixEpoch), clock.Current);   // warm read: no clock call
        Assert.Equal(1, time.Reads);

        var start = time.GetUtcNow();
        var readsBeforeTick = time.Reads;
        var bucket = clock.Tick();
        Assert.Equal(readsBeforeTick + 1, time.Reads);      // exactly one read per tick
        Assert.Equal(ActivityBucket.FromUtc(start), bucket);

        clock.Publish(start + TimeSpan.FromMinutes(1));
        Assert.Equal(readsBeforeTick + 1, time.Reads);      // publish uses the argument it was handed

        var table = new FlowTable(capacity: 4, activityClock: clock);
        Assert.True(table.TryClaimResolved(MakeUdpKey(30_000), static () => FlowDecision.Fallback(FlowAction.Pass), out _));
        var now = time.GetUtcNow();
        var readsAfterClaim = time.Reads;

        time.ThrowOnRead = true;
        Assert.True(table.TryResolveWarm(MakeUdpKey(30_000), out _));
        Assert.Equal(readsAfterClaim, time.Reads);
        Assert.True(table.TryResolve(MakeUdpKey(30_000), out _));
        Assert.Equal(readsAfterClaim, time.Reads);
        Assert.Equal(0, table.RemoveExpired(now, TimeSpan.FromMinutes(1)));
        Assert.Equal(readsAfterClaim, time.Reads);
    }

    [Fact]
    public void FlowTableRetainsAStateUntilTheBucketAfterItsIdleWindow()
    {
        var start = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var time = new MutableTimeProvider(start);
        var table = new FlowTable(timeProvider: time);
        var idleTimeout = TimeSpan.FromSeconds(10);
        Assert.True(table.TryClaimResolved(MakeUdpKey(30_001), static () => FlowDecision.Fallback(FlowAction.Pass), out _));
        var stampBucket = ActivityBucket.FromUtc(start);

        // Exactly at the idle boundary the state is retained: the stamp is quantised down, so the
        // never-early operator is a strict `<` on buckets.
        Assert.Equal(0, table.RemoveExpired(start + idleTimeout, idleTimeout));

        // The first bucket edge after the boundary is what retires it — one tick earlier still retains.
        var edge = ActivityBucket.ToUtc(stampBucket + 1);
        Assert.Equal(0, table.RemoveExpired(edge + idleTimeout - TimeSpan.FromTicks(1), idleTimeout));
        Assert.Equal(1, table.RemoveExpired(edge + idleTimeout, idleTimeout));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void ZeroIdleTimeoutStillRetiresEveryStateOnTheCall()
    {
        var table = new FlowTable(capacity: 8);
        for (var port = 30_100; port < 30_103; port++)
        {
            Assert.True(table.TryClaimResolved(MakeUdpKey((ushort)port), static () => FlowDecision.Fallback(FlowAction.Pass), out _));
        }

        // The states are stamped in the current bucket; a derived cutoff would keep them to the next
        // bucket edge, so a non-positive timeout must keep its "retire everything on this call" meaning.
        Assert.Equal(3, table.RemoveExpired(DateTimeOffset.UtcNow, TimeSpan.Zero));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void FlowTableActivitySurvivesARecycledState()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var table = new FlowTable(capacity: 1, timeProvider: time);
        var first = MakeUdpKey(30_200);
        var second = MakeUdpKey(30_201);
        Assert.True(table.TryClaimResolved(first, static () => FlowDecision.Fallback(FlowAction.Pass), out var claimed));
        Assert.Equal(ActivityBucket.FromUtc(DateTimeOffset.UnixEpoch), claimed!.ActivityBucketForDiagnostics);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, table.RemoveExpired(time.GetUtcNow(), TimeSpan.FromMinutes(1)));

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(table.TryClaimResolved(second, static () => FlowDecision.Fallback(FlowAction.Pass), out var recycled));
        Assert.Same(claimed, recycled);
        Assert.Equal(ActivityBucket.FromUtc(time.GetUtcNow()), recycled!.ActivityBucketForDiagnostics);
        Assert.Equal(second, recycled.Key);
    }

    [Fact]
    public void WarmResolveTakesNoFlowTableGateEntries()
    {
        var table = new FlowTable(capacity: 8);
        var key = MakeUdpKey(31_000);
        Assert.True(table.TryClaimResolved(key, static () => FlowDecision.Fallback(FlowAction.Pass), out _));

        var entries = 0;
        table.GateHoldProbe = () => Interlocked.Increment(ref entries);
        var hits = 0;
        for (var index = 0; index < 256; index++) hits += table.TryResolveWarm(key, out _) ? 1 : 0;
        table.GateHoldProbe = null;

        // The probe resolves every time and never enters the gate: the pre-change tree measured
        // `Expected: 0, Actual: 256` here (recorded in warm-path-gate-counts.txt).
        Assert.Equal(256, hits);
        Assert.Equal(0, entries);
    }

    [Fact]
    public void WarmResolveCompletesWhileFlowTableGateIsHeld()
    {
        // Dedicated threads, never Task.Run: a pool thread could inline the resolve onto the parked
        // holder's thread and the fact would pass vacuously.
        var table = new FlowTable(capacity: 8);
        var liveKey = MakeUdpKey(31_001);
        var holderKey = MakeUdpKey(31_002);
        Assert.True(table.TryClaimResolved(liveKey, static () => FlowDecision.Fallback(FlowAction.Pass), out _));

        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        table.GateHoldProbe = () =>
        {
            parked.TrySetResult();
            // Parked until the release; the holder's own bounded wait keeps a failing run from hanging.
            release.Task.Wait(TimeSpan.FromSeconds(10));
        };

        var holderDone = false;
        var holder = new Thread(() => holderDone = table.TryClaimResolved(holderKey, static () => FlowDecision.Fallback(FlowAction.Pass), out _))
        {
            IsBackground = true,
            Name = "flow-table-gate-holder",
        };

        holder.Start();
        try
        {
            Assert.True(parked.Task.Wait(TimeSpan.FromSeconds(10)), "no thread parked inside the flow-table gate");

            var resolved = false;
            var resolver = new Thread(() => resolved = table.TryResolveWarm(liveKey, out _))
            {
                IsBackground = true,
                Name = "flow-warm-resolve-during-gate-hold",
            };
            resolver.Start();
            Assert.True(resolver.Join(TimeSpan.FromSeconds(10)), "the warm resolve queued behind the parked flow-table gate");
            Assert.True(resolved);
        }
        finally
        {
            // Release before asserting anything else: on the failure this fact exists to detect the
            // holder is still parked inside the gate, and an assertion would leave it there.
            release.TrySetResult();
        }

        Assert.True(holder.Join(TimeSpan.FromSeconds(10)));
        Assert.True(holderDone);
    }

    [Fact]
    public void FlowTableWarmResolveAllocatesNoManagedBytes()
    {
        const int count = 256;
        var table = new FlowTable(capacity: 8);
        var key = MakeUdpKey(34_100);
        Assert.True(table.TryClaimResolved(key, static () => FlowDecision.Fallback(FlowAction.Pass), out _));
        Assert.True(table.TryResolveWarm(key, out _), "the gate relies on a warm slot, not on a false miss");

        // Probe batches until the per-thread counter reads exactly zero on an unchanged thread
        // (hot-path.md's window contract); the loop is bounded, so a genuine per-call allocation fails
        // the fact instead of passing.
        var stabilized = false;
        for (var attempt = 0; attempt < 8 && !stabilized; attempt++)
        {
            var probeBefore = GC.GetAllocatedBytesForCurrentThread();
            var probeHits = 0;
            for (var index = 0; index < count; index++) probeHits += table.TryResolveWarm(key, out _) ? 1 : 0;
            var probeAllocated = GC.GetAllocatedBytesForCurrentThread() - probeBefore;
            stabilized = probeAllocated == 0 && probeHits == count;
        }
        Assert.True(stabilized, "the flow table's warm resolve never became allocation-stable");

        var measuredThreadId = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var resolutions = 0;
        for (var index = 0; index < count; index++) resolutions += table.TryResolveWarm(key, out _) ? 1 : 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(0, allocated);
        Assert.Equal(count, resolutions);
    }

    [Fact]
    public void FlowTableCollidingFlowsFallBackToTheGatedPath()
    {
        var table = new FlowTable(capacity: 8);
        var mask = table.WarmSlotCountForDiagnostics - 1;
        var (first, second) = FindCollidingPair(mask);
        Assert.True(table.TryClaimResolved(first, static () => FlowDecision.Fallback(FlowAction.Pass), out _));
        Assert.True(table.TryClaimResolved(second, static () => FlowDecision.Fallback(FlowAction.Block), out _));

        // The second claim overwrote the shared slot, so the first flow is a false miss — but its gated
        // resolve still answers with its own decision and warms the slot back.
        Assert.True(table.TryResolveWarm(second, out var secondWarm));
        Assert.Equal(FlowAction.Block, secondWarm.Decision.Action);
        Assert.False(table.TryResolveWarm(first, out _));

        Assert.True(table.TryResolve(first, out var firstGated));
        Assert.Equal(FlowAction.Pass, firstGated!.Decision.Action);
        Assert.True(table.TryResolveWarm(first, out var firstWarm));
        Assert.Equal(FlowAction.Pass, firstWarm.Decision.Action);

        // The displaced flow's own probe falls back, and its decision is still its own.
        Assert.False(table.TryResolveWarm(second, out _));
        Assert.True(table.TryResolve(second, out var secondGated));
        Assert.Equal(FlowAction.Block, secondGated!.Decision.Action);
    }

    [Fact]
    public void FlowTableRemovedFlowIsNeverServedFromItsOldSlot()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var table = new FlowTable(capacity: 1, timeProvider: time);
        var first = MakeUdpKey(33_000);
        var second = MakeUdpKey(33_001);

        Assert.True(table.TryClaimResolved(first, static () => FlowDecision.Fallback(FlowAction.Pass), out var firstState));
        Assert.True(table.TryResolveWarm(first, out var claimedWarm));
        Assert.Equal(FlowAction.Pass, claimedWarm.Decision.Action);

        // The sweep clears the slot under the same hold that removes the state and before it can return
        // to the pool, so the removed flow's warm probe misses.
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, table.RemoveExpired(time.GetUtcNow(), TimeSpan.FromMinutes(1)));
        Assert.False(table.TryResolveWarm(first, out _));

        // The pooled instance is recycled for another key: a snapshot of the held reference describes
        // the new flow, so corroboration can never serve it under the old key's tuple.
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(table.TryClaimResolved(second, static () => FlowDecision.Fallback(FlowAction.Block), out var secondState));
        Assert.Same(firstState, secondState);
        Assert.True(firstState!.TrySnapshot(out var snapshot));
        Assert.Equal(second, snapshot.Key);
        Assert.Equal(FlowAction.Block, snapshot.Decision.Action);
        Assert.False(table.TryResolveWarm(first, out _));
        Assert.True(table.TryResolveWarm(second, out var secondWarm));
        Assert.Equal(FlowAction.Block, secondWarm.Decision.Action);

        // A re-claim of the removed key serves the new publication, never the stale one.
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, table.RemoveExpired(time.GetUtcNow(), TimeSpan.FromMinutes(1)));
        Assert.True(table.TryClaimResolved(first, static () => FlowDecision.Fallback(FlowAction.Proxy), out _));
        Assert.True(table.TryResolveWarm(first, out var reclaimedWarm));
        Assert.Equal(FlowAction.Proxy, reclaimedWarm.Decision.Action);
        Assert.True(reclaimedWarm.Generation > claimedWarm.Generation);
    }

    [Fact]
    public void FlowTableTransportTupleIsUniqueAcrossOrigins()
    {
        var table = new FlowTable(capacity: 8);
        var host = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 35_000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host, FlowBuilders.SlotOf("host", 1), 1);
        var forwarded = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 35_000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Forwarded, FlowBuilders.SlotOf("forwarded", 2), 2);
        var decisionCount = 0;

        Assert.True(table.TryClaimResolved(host, () => { decisionCount++; return FlowDecision.Fallback(FlowAction.Pass); }, out var firstState));
        Assert.True(table.TryClaimResolved(forwarded, () => { decisionCount++; return FlowDecision.Fallback(FlowAction.Block); }, out var secondState));

        // One transport tuple is one logical flow: the second claim resolves to the existing state (the
        // invariant the cache's tuple-only corroboration rests on), so policy runs exactly once.
        Assert.Same(firstState, secondState);
        Assert.Equal(1, decisionCount);
        Assert.Equal(1, table.Count);
        Assert.True(table.TryResolveWarm(forwarded, out var warm));
        Assert.Equal(FlowAction.Pass, warm.Decision.Action);

        var reverse = host.Reverse();
        Assert.True(table.TryResolveWarm(reverse, out var reverseWarm));
        Assert.Equal(host, reverseWarm.Key);
    }

    [Fact]
    public void FlowTableRegistryMirrorsCountAcrossChurn()
    {
        var table = new FlowTable(capacity: 64);
        for (var round = 0; round < 8; round++)
        {
            for (var index = 0; index < 32; index++)
            {
                Assert.True(table.TryClaimResolved(MakeUdpKey((ushort)(36_000 + (round * 32) + index)), static () => FlowDecision.Fallback(FlowAction.Pass), out _));
            }
            Assert.Equal(32, table.Count);
            Assert.Equal(table.Count, table.LiveStateCountForDiagnostics);

            Assert.Equal(32, table.RemoveExpired(DateTimeOffset.UtcNow.AddMinutes(10), TimeSpan.FromMinutes(1)));
            Assert.Equal(0, table.Count);
            Assert.Equal(0, table.LiveStateCountForDiagnostics);
        }
    }

    [Fact]
    public void WarmCacheHitServesTheValidatedView()
    {
        var table = new FlowTable(capacity: 8);
        var key = MakeUdpKey(34_000);
        var decision = new FlowDecision(FlowAction.Proxy, 3, "dns");
        Assert.True(table.TryClaimResolved(key, () => decision, out var state));

        Assert.True(table.TryResolveWarm(key, out var warm));
        Assert.True(table.TryResolve(key, out var gated));
        Assert.Equal(gated!.Key, warm.Key);
        Assert.Equal(gated.Decision, warm.Decision);
        Assert.Equal(gated.Generation, warm.Generation);
        Assert.Equal(decision, warm.Decision);
        Assert.Equal(state!.Generation, warm.Generation);

        var reverse = key.Reverse();
        Assert.True(table.TryResolveWarm(reverse, out var reverseWarm));
        Assert.Equal(warm.Key, reverseWarm.Key);
        Assert.Equal(warm.Decision, reverseWarm.Decision);
    }

    /// <summary>
    /// Two keys whose canonical slot index collides at <paramref name="mask"/>: the first pair a
    /// deterministic sweep of generated keys finds (a 4,096-slot table collides after ~77 keys).
    /// </summary>
    private static (FlowKey First, FlowKey Second) FindCollidingPair(int mask)
    {
        var seen = new Dictionary<int, FlowKey>();
        for (var index = 0; index < 100_000; index++)
        {
            var candidate = MakeUdpKey((ushort)(40_000 + index));
            var slot = FlowHash.CombineCanonical(candidate.AddressFamily, candidate.Protocol, candidate.Local, candidate.Remote) & mask;
            if (seen.TryGetValue(slot, out var first) && !first.Equals(candidate)) return (first, candidate);
            seen[slot] = candidate;
        }

        throw new InvalidOperationException("No canonical-slot collision found for the cache fact.");
    }

    private static FlowKey MakeUdpKey(ushort port, ushort remotePort = 53) =>
        FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), port),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), remotePort),
            TransportProtocol.Udp,
            FlowOriginKind.Host);
}
