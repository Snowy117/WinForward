using System.Net;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class CoreFlowStructuresTests
{
    [Fact]
    public void SameUdpTupleClaimsOneStateAndDifferentRemoteGetsAnother()
    {
        var local = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var dns1 = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var dns2 = Endpoint.From(IPAddress.Parse("192.0.2.54"), 53);
        var table = new FlowTable();
        var decisionCount = 0;

        Assert.True(table.TryClaimResolved(FlowKey.Create(local, dns1, TransportProtocol.Udp, FlowOriginKind.Host), () =>
        {
            decisionCount++;
            return new FlowDecision(FlowAction.Proxy, 0, "dns");
        }, out var first));
        Assert.True(table.TryClaimResolved(FlowKey.Create(local, dns1, TransportProtocol.Udp, FlowOriginKind.Host), () =>
        {
            decisionCount++;
            return new FlowDecision(FlowAction.Block, 1, ProxyServerName: null);
        }, out var second));
        Assert.True(table.TryClaimResolved(FlowKey.Create(local, dns2, TransportProtocol.Udp, FlowOriginKind.Host), () =>
        {
            decisionCount++;
            return new FlowDecision(FlowAction.Proxy, 0, "dns");
        }, out var third));

        Assert.Same(first, second);
        Assert.NotSame(first, third);
        Assert.Equal(2, decisionCount);
    }

    [Fact]
    public void PacketLeaseAllowsExactlyOneDisposition()
    {
        using var lease = new PacketLease(new byte[] { 1, 2 });

        Assert.True(lease.TryComplete(PacketDisposition.ProxyConsumed));
        Assert.False(lease.TryComplete(PacketDisposition.Pass));
        Assert.Equal(PacketDisposition.ProxyConsumed, lease.Disposition);
    }

    [Fact]
    public void SetupQueueFailsClosedWhenPacketOrByteLimitIsReached()
    {
        using var pool = new NativeBufferPool(8);
        var queue = new BoundedSetupQueue(maxPackets: 2, maxBytes: 4);

        Assert.True(Enqueue(queue, pool, [1, 2]));
        Assert.True(Enqueue(queue, pool, [3, 4]));
        Assert.False(Enqueue(queue, pool, [5]));
        Assert.Equal(2, queue.Count);
        Assert.Equal(4, queue.Bytes);
    }

    private static bool Enqueue(BoundedSetupQueue queue, NativeBufferPool pool, ReadOnlySpan<byte> payload)
    {
        var lease = pool.Rent();
        payload.CopyTo(lease.Span);
        if (queue.TryEnqueue(lease, payload.Length, default)) return true;
        lease.Dispose();
        return false;
    }

    [Fact]
    public void FlowTableFailsClosedAtCapacity()
    {
        var table = new FlowTable(capacity: 1);
        var first = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 1), Endpoint.From(IPAddress.Parse("192.0.2.1"), 2), TransportProtocol.Udp, FlowOriginKind.Host);
        var second = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 3), Endpoint.From(IPAddress.Parse("192.0.2.1"), 4), TransportProtocol.Udp, FlowOriginKind.Host);
        Assert.True(table.TryClaimResolved(first, () => FlowDecision.Fallback(FlowAction.Pass), out _));

        Assert.False(table.TryClaimResolved(second, () => FlowDecision.Fallback(FlowAction.Pass), out _));
    }

    [Fact]
    public void FlowTableLookupRefreshesActivityBeforeIdleExpiry()
    {
        var table = new FlowTable();
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var claimed = table.TryClaimResolved(key, () => FlowDecision.Fallback(FlowAction.Pass), out var state)
            ? state!
            : throw new InvalidOperationException("Flow table claim failed.");
        claimed.Touch(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2));

        Assert.True(table.TryResolve(key, out var resolved));
        Assert.Same(claimed, resolved);
        // The hit stores the clock's published bucket, so the stamp is that bucket's start instant.
        Assert.Equal(ActivityBucket.ToUtc(table.ActivityClock.Current), claimed.LastActivityUtc);

        // Retained at exactly the idle boundary, retired by the first sweep at or after the next bucket
        // edge: a quantised-down stamp is never retired early.
        var stampBucket = ActivityBucket.FromUtc(claimed.LastActivityUtc);
        var retirement = ActivityBucket.ToUtc(stampBucket + 1) + TimeSpan.FromMinutes(1);
        Assert.Equal(0, table.RemoveExpired(retirement - TimeSpan.FromTicks(1), TimeSpan.FromMinutes(1)));
        Assert.Equal(1, table.RemoveExpired(retirement, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void FlowTableLookupRefreshesActivityFromInjectedClock()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var table = new FlowTable(timeProvider: time);
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var claimed = table.TryClaimResolved(key, () => FlowDecision.Fallback(FlowAction.Pass), out var state)
            ? state!
            : throw new InvalidOperationException("Flow table claim failed.");
        claimed.Touch(time.GetUtcNow() - TimeSpan.FromMinutes(2));

        time.Advance(TimeSpan.FromMinutes(30));
        // Claims, sweeps and the pump's per-iteration callback are the tick sources; a unit test drives
        // the tick itself, exactly as the composition's per-iteration callback does in production.
        table.ActivityClock.Tick();

        Assert.True(table.TryResolve(key, out var resolved));
        Assert.Same(claimed, resolved);
        Assert.Equal(ActivityBucket.ToUtc(ActivityBucket.FromUtc(time.GetUtcNow())), claimed.LastActivityUtc);

        var stampBucket = ActivityBucket.FromUtc(claimed.LastActivityUtc);
        var retirement = ActivityBucket.ToUtc(stampBucket + 1) + TimeSpan.FromMinutes(1);
        Assert.Equal(0, table.RemoveExpired(retirement - TimeSpan.FromTicks(1), TimeSpan.FromMinutes(1)));
        Assert.Equal(1, table.RemoveExpired(retirement, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void FlowTableRemoveExpiredHonorsHoldPredicateWithoutTouchingActivity()
    {
        var table = new FlowTable();
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var claimed = table.TryClaimResolved(key, () => FlowDecision.Fallback(FlowAction.Pass), out var state)
            ? state!
            : throw new InvalidOperationException("Flow table claim failed.");
        var lastActivity = claimed.LastActivityUtc;
        var now = lastActivity + TimeSpan.FromMinutes(10);

        // A held entry (e.g. a silently relaying TCP redirect session) survives the idle sweep and
        // keeps its activity timestamp untouched.
        Assert.Equal(0, table.RemoveExpired(now, TimeSpan.FromMinutes(1), isHeld: heldKey => heldKey == key));
        Assert.Equal(lastActivity, claimed.LastActivityUtc);

        // Once the hold lapses, the very same sweep time removes the entry at its original idle
        // point — the hold skipped removal without re-arming the idle window.
        Assert.Equal(1, table.RemoveExpired(now, TimeSpan.FromMinutes(1), isHeld: _ => false));
        Assert.False(table.TryResolve(key, out _));
    }

    [Fact]
    public void FlowTableSweepCompletesOneRoundWithClaimsInterleaved()
    {
        // The sweep releases the table gate between chunks now, so a claim can land mid-round. Every state
        // that was idle-elapsed at entry must still be gone when the call returns, and the replacement —
        // appended past the cursor — must survive. On the pre-chunk tree this shape failed with
        // InvalidOperationException: the callback's claim mutated the dictionary the sweep enumerated
        // under its gate.
        var table = new FlowTable(capacity: 64);
        var expiredKeys = new FlowKey[12];
        for (var index = 0; index < expiredKeys.Length; index++) expiredKeys[index] = MakeUdpKey(checked((ushort)(20_000 + index)));
        var liveKey = MakeUdpKey(21_000);
        var idleStamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        foreach (var key in expiredKeys) ClaimAt(table, key, idleStamp);
        ClaimAt(table, liveKey, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var replacement = MakeUdpKey(22_000);

        var holds = 0;
        var claimedMidRound = false;
        var claimed = false;
        FlowState? claimedState = null;
        var removed = table.RemoveExpired(now, TimeSpan.FromMinutes(1), isHeld: _ =>
        {
            holds++;
            // The claim is issued from the second candidate on, so at least one entry has already been
            // removed and the round is provably in progress.
            if (holds > 1 && !claimedMidRound)
            {
                claimedMidRound = true;
                claimed = table.TryClaimResolved(replacement, static () => FlowDecision.Fallback(FlowAction.Pass), out claimedState);
            }

            return false;
        });

        Assert.True(claimedMidRound);
        Assert.True(claimed);
        Assert.Equal(expiredKeys.Length, removed);
        Assert.Equal(expiredKeys.Length, holds);
        foreach (var key in expiredKeys) Assert.False(table.TryResolve(key, out _));
        Assert.True(table.TryResolve(liveKey, out _));
        Assert.True(table.TryResolve(replacement, out var replacementState));
        Assert.Same(claimedState, replacementState);
        Assert.Equal(2, table.Count);
        Assert.Equal(table.Count, table.LiveStateCountForDiagnostics);
    }

    [Fact]
    public void FlowTableSweepPredicateParkDoesNotBlockConcurrentResolve()
    {
        // Requirement 3, the concurrency half: while the hold predicate is parked, the table gate must be
        // free, so a warm resolve completes instead of queueing behind caller code that takes the store's
        // and the tombstone's locks. Dedicated threads, never Task.Run: the xunit thread is itself a pool
        // thread, and an inlined task would park the test thread inside its own predicate.
        var table = new FlowTable(capacity: 32);
        var liveKey = MakeUdpKey(21_000);
        ClaimAt(table, liveKey, DateTimeOffset.UtcNow);
        var expiredKey = MakeUdpKey(20_000);
        ClaimAt(table, expiredKey, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2));
        var now = DateTimeOffset.UtcNow;

        // TaskCompletionSource rather than ManualResetEventSlim: neither thread that captures these may
        // outlive a disposal, and a TCS owns no handle to dispose in the first place.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = -1;
        var sweeper = new Thread(() => removed = table.RemoveExpired(now, TimeSpan.FromMinutes(1), _ =>
        {
            parked.TrySetResult();
            // Parked until the release; after it, the entry is not held and expires at its original point.
            return !release.Task.Wait(TimeSpan.FromSeconds(10));
        }))
        {
            IsBackground = true,
            Name = "flow-sweep-parked-predicate",
        };

        var resolved = false;
        var resolver = new Thread(() => resolved = table.TryResolve(liveKey, out _))
        {
            IsBackground = true,
            Name = "flow-resolve-during-park",
        };

        sweeper.Start();
        try
        {
            Assert.True(parked.Task.Wait(TimeSpan.FromSeconds(10)), "the sweep never invoked the hold predicate");
            resolver.Start();
            Assert.True(resolver.Join(TimeSpan.FromSeconds(10)), "the concurrent resolve queued behind the parked predicate");
            Assert.True(resolved);
        }
        finally
        {
            // Release before asserting anything else: on the failure this test exists to detect, the
            // sweeper is still parked inside the predicate, and the assertion would leave it there.
            release.TrySetResult();
        }

        Assert.True(sweeper.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, removed);
        Assert.Equal(1, table.Count);
        Assert.True(table.TryResolve(liveKey, out _));
    }

    /// <summary>
    /// Round completeness at 65,536 idle-elapsed flows in one call. Each removal happens at the cursor and
    /// pulls the tail down onto it, so the swapped-in element is examined next and the round walks the whole
    /// registry without a rewind (the batched-removal variant needed one, and this is the shape that caught
    /// its absence: with batching and no rewind the call returned half the table). It is also the shape the
    /// sweep scenario's <c>removedPerSweep == 65,536</c> tripwire requires.
    /// </summary>
    [Fact]
    public void FlowTableSweepRemovesEveryIdleFlowInOneRound()
    {
        const int flows = 65_536;
        var table = new FlowTable(capacity: flows + 16);
        var idleStamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        for (var index = 0; index < flows; index++)
        {
            ClaimAt(table, MakeUdpKey(checked((ushort)(20_000 + (index % 4_096))), checked((ushort)(1_000 + (index / 4_096)))), idleStamp);
        }

        Assert.Equal(flows, table.RemoveExpired(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, table.Count);
        Assert.Equal(0, table.LiveStateCountForDiagnostics);
    }

    [Fact]
    public void FlowTableExpiryRemovesCrossAdapterTransportAliases()
    {
        var table = new FlowTable();
        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000),
            Endpoint.From(IPAddress.Parse("198.51.100.53"), 53),
            TransportProtocol.Udp,
            FlowOriginKind.Host,
            FlowBuilders.SlotOf("host", 1),
            1);
        var claimed = table.TryClaimResolved(key, () => FlowDecision.Fallback(FlowAction.Pass), out var state)
            ? state!
            : throw new InvalidOperationException("Flow table claim failed.");
        claimed.Touch(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2));
        var crossAdapter = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000),
            Endpoint.From(IPAddress.Parse("198.51.100.53"), 53),
            TransportProtocol.Udp,
            FlowOriginKind.Forwarded,
            FlowBuilders.SlotOf("forwarded", 2),
            2);

        Assert.Equal(1, table.RemoveExpired(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
        Assert.False(table.TryResolve(crossAdapter, out _));
        Assert.True(table.TryClaimResolved(crossAdapter, () => FlowDecision.Fallback(FlowAction.Block), out var replacement));
        Assert.Equal(FlowAction.Block, replacement!.Decision.Action);
    }

    [Fact]
    public void UdpAssociationUsesOriginalAndRelayAliasesWithoutCrossWiring()
    {
        var original = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var secondOriginal = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.54"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var relay = new RelayAlias(FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 40000), Endpoint.From(IPAddress.Parse("198.51.100.10"), 50000), TransportProtocol.Udp, FlowOriginKind.Host));
        var secondRelay = new RelayAlias(FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 40001), Endpoint.From(IPAddress.Parse("198.51.100.10"), 50001), TransportProtocol.Udp, FlowOriginKind.Host));
        var table = new UdpAssociationTable();
        var now = DateTimeOffset.UtcNow;

        var first = table.Claim(original, relay, now);
        var same = table.Claim(original, relay, now.AddSeconds(1));
        var second = table.Claim(secondOriginal, secondRelay, now.AddSeconds(1));

        Assert.Same(first, same);
        Assert.NotSame(first, second);
        Assert.True(table.TryFindRelay(relay, now.AddSeconds(2), out var found));
        Assert.Same(first, found);
    }

    [Fact]
    public void UdpAssociationExpiryRemovesBothIndexes()
    {
        var original = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var relay = new RelayAlias(original);
        var table = new UdpAssociationTable();
        table.Claim(original, relay, DateTimeOffset.UtcNow);

        Assert.Equal(1, table.RemoveExpired(DateTimeOffset.UtcNow.AddMinutes(2), TimeSpan.FromMinutes(1)));
        Assert.False(table.TryFindOriginal(original, DateTimeOffset.UtcNow, out _));
        Assert.False(table.TryFindRelay(relay, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void UdpAssociationRejectsRelayAliasCollisionAcrossOriginalFlows()
    {
        var firstOriginal = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var secondOriginal = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53001), Endpoint.From(IPAddress.Parse("192.0.2.54"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var relay = new RelayAlias(FlowKey.Create(Endpoint.From(IPAddress.Loopback, 40000), Endpoint.From(IPAddress.Parse("198.51.100.10"), 50000), TransportProtocol.Udp, FlowOriginKind.Host));
        var table = new UdpAssociationTable();

        Assert.True(table.TryClaim(firstOriginal, relay, DateTimeOffset.UtcNow, out _));
        Assert.False(table.TryClaim(secondOriginal, relay, DateTimeOffset.UtcNow, out var collision));
        Assert.Null(collision);
    }

    private static FlowKey MakeUdpKey(ushort port, ushort remotePort = 53) =>
        FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), port),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), remotePort),
            TransportProtocol.Udp,
            FlowOriginKind.Host);

    private static void ClaimAt(FlowTable table, FlowKey key, DateTimeOffset lastActivityUtc)
    {
        var state = table.TryClaimResolved(key, static () => FlowDecision.Fallback(FlowAction.Pass), out var claimed)
            ? claimed!
            : throw new InvalidOperationException("Flow table claim failed.");
        state.Touch(lastActivityUtc);
    }
}
