using WinForward.Core;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Runtime.Flow.Tests;

/// <summary>
/// The bounded pending-attribution index's facts: one counted refusal per class, a byte budget
/// credited exactly once at every sink, a prefix-preserving ring, and a decided queue whose depth
/// returns to zero. Every fact drives the index directly, so it holds whatever the dispatcher does
/// with the outcome.
/// </summary>
public sealed class FlowAttributionPendingIndexTests
{
    private const int FrameLength = 64;
    private static readonly DateTimeOffset s_start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheEntryCapRefusesExactlyOncePerRefusedPacket()
    {
        using var pool = new NativeBufferPool(2048, 8);
        var index = new FlowAttributionPendingIndex(pool, new FlowTable(16), capacity: 2);

        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53000));
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53001));
        Assert.Equal(AttributionAdmission.BlockedPending, Admit(index, 53002));
        Assert.Equal(AttributionAdmission.BlockedPending, Admit(index, 53003));

        Assert.Equal(2, index.ActiveCount);
        Assert.Equal(2, index.PendingRejectedCount);
    }

    [Fact]
    public void TheGlobalBudgetIsConservedAcrossEverySink()
    {
        using var pool = new NativeBufferPool(2048, 8);
        var index = new FlowAttributionPendingIndex(pool, new FlowTable(16), capacity: 16, byteBudget: 4 * FrameLength);

        // Every fresh packet of one pending flow is charged; taking a batch does not credit.
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53010));
        Assert.Equal(FrameLength, index.ChargedBytes);
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53010, marker: 2));
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53010, marker: 3));
        Assert.Equal(3 * FrameLength, index.ChargedBytes);

        // Sink 1: the delivery's claim removes the entry and credits what it still holds.
        var entry = EntryFor(index, 53010);
        var batch = new List<RetainedPacket>();
        Assert.Equal(3, index.TakeAll(entry, batch, out _));
        Assert.Equal(3 * FrameLength, index.ChargedBytes);
        Assert.Equal(AttributionClaim.Claimed, Claim(index, entry));
        Assert.Equal(0, index.ChargedBytes);
        foreach (var retained in batch) retained.Frame.Dispose();

        // Sink 2: the failure arm.
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53011));
        index.MarkFailed(EntryFor(index, 53011), shutdown: false);
        Assert.True(index.TryTakeFailed(EntryFor(index, 53011), batch, s_start, out var failedTaken));
        Assert.Equal(1, failedTaken);
        Assert.Equal(0, index.ChargedBytes);


        // Sink 3: the retention TTL.
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53012));
        var reclaimed = new List<PendingFlowAttribution>();
        index.RemoveExpired(s_start + TimeSpan.FromSeconds(6), reclaimed);
        Assert.Single(reclaimed);
        Assert.Equal(1, index.TtlExpiredCount);
        Assert.Equal(0, index.ChargedBytes);
        FlowAttributionPendingIndex.DisposeReclaimed(reclaimed[0]);

        // Sink 4: a budget refusal rolls its own charge back and leaves the budget usable.
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53014));
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53015));
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53016));
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53017));
        Assert.Equal(AttributionAdmission.BlockedPending, Admit(index, 53018));
        Assert.Equal(4 * FrameLength, index.ChargedBytes);

        // Sink 5: the dispose drain, which seals the index and releases every remaining charge.
        var drained = new List<PendingFlowAttribution>();
        index.Seal(drained);
        Assert.Equal(4, drained.Count);
        Assert.Equal(0, index.ChargedBytes);
        Assert.Equal(0, index.ActiveCount);
    }

    [Fact]
    public void TheGlobalBudgetIsConservedUnderTheRefusalAndDuplicateClaimSinks()
    {
        using var pool = new NativeBufferPool(2048, 8);
        var index = new FlowAttributionPendingIndex(pool, new FlowTable(16), capacity: 4, byteBudget: FrameLength * FlowAttributionPendingIndex.RingCapacity);
        var batch = new List<RetainedPacket>();

        // Sink 6: a ring-full refusal charges nothing — the entry keeps exactly what it retained, so
        // charged still equals live.
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53090));
        var entry = EntryFor(index, 53090);
        for (byte marker = 2; marker <= FlowAttributionPendingIndex.RingCapacity; marker++)
        {
            Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53090, marker));
        }

        Assert.Equal((long)FrameLength * FlowAttributionPendingIndex.RingCapacity, index.ChargedBytes);
        Assert.Equal(AttributionAdmission.BlockedFlowFull, Admit(index, 53090, marker: 99));
        Assert.Equal((long)FrameLength * FlowAttributionPendingIndex.RingCapacity, index.ChargedBytes);
        Assert.Equal(FlowAttributionPendingIndex.RingCapacity, index.TakeAll(entry, batch, out _));
        foreach (var retained in batch) retained.Frame.Dispose();

        // Sink 7: a claim the flow table refused at capacity keeps the entry, so its bytes stay live
        // until a later claim removes it.
        var full = new FlowTable(1);
        Assert.True(full.TryClaimResolved(Key(53091), new FlowDecision(FlowAction.Pass, 0, ProxyServerName: null), out _));
        var blocked = new FlowAttributionPendingIndex(pool, full, capacity: 4);
        Assert.Equal(AttributionAdmission.Deferred, blocked.Admit(Packet(53092, 1), s_start, out _, out _));
        var blockedEntry = blocked.EntryForDiagnostics(Key(53092)) ?? throw new InvalidOperationException("no entry");
        blocked.MarkDecided(blockedEntry, blockedEntry.Context, new FlowDecision(FlowAction.Pass, 0, ProxyServerName: null));
        Assert.Equal(AttributionClaim.CapacityBlocked, blocked.Claim(blockedEntry));
        Assert.Equal(FrameLength, blocked.ChargedBytes);
        Assert.Equal(1, blocked.ActiveCount);
        Assert.Equal(1, blocked.TakeAll(blockedEntry, batch, out _));
        foreach (var retained in batch) retained.Frame.Dispose();

        // Sink 8: a second key for a transport tuple that already carries a decision credits its
        // whole charge — the entry is redundant, not live. Both keys are admitted before either is
        // claimed, because an admission that resolves an already-claimed tuple is inline by design.
        var shared = new FlowTable(16);
        var reAdmitted = new FlowAttributionPendingIndex(pool, shared, capacity: 4);
        Assert.Equal(AttributionAdmission.Deferred, reAdmitted.Admit(AdapterPacket(53093, "id-a"), s_start, out _, out _));
        Assert.Equal(AttributionAdmission.Deferred, reAdmitted.Admit(AdapterPacket(53093, "id-b"), s_start, out _, out _));
        var first = reAdmitted.EntryForDiagnostics(AdapterKey(53093, "id-a")) ?? throw new InvalidOperationException("no entry");
        var second = reAdmitted.EntryForDiagnostics(AdapterKey(53093, "id-b")) ?? throw new InvalidOperationException("no entry");
        reAdmitted.MarkDecided(first, first.Context, new FlowDecision(FlowAction.Pass, 0, ProxyServerName: null));
        reAdmitted.MarkDecided(second, second.Context, new FlowDecision(FlowAction.Pass, 0, ProxyServerName: null));

        Assert.Equal(AttributionClaim.Claimed, reAdmitted.Claim(first));
        Assert.Equal(FrameLength, reAdmitted.ChargedBytes);
        Assert.Equal(AttributionClaim.AlreadyAttributed, reAdmitted.Claim(second));
        Assert.Equal(0, reAdmitted.ChargedBytes);
    }

    [Fact]
    public void TheRingKeepsThePrefixAndRefusesTheNewcomer()
    {
        using var pool = new NativeBufferPool(2048, 64);
        var index = new FlowAttributionPendingIndex(pool, new FlowTable(16), capacity: 4);

        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53020));
        for (byte marker = 2; marker <= FlowAttributionPendingIndex.RingCapacity; marker++)
        {
            Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53020, marker));
        }

        Assert.Equal(AttributionAdmission.BlockedFlowFull, Admit(index, 53020, marker: 99));
        Assert.Equal(1, index.FlowFullCount);

        var batch = new List<RetainedPacket>();
        var entry = EntryFor(index, 53020);
        Assert.Equal(FlowAttributionPendingIndex.RingCapacity, index.TakeAll(entry, batch, out _));
        // The head is the flow's triggering packet, so the refused newcomer is the only loss.
        Assert.Equal(1, batch[0].Frame.Span[0]);
        Assert.Equal(FlowAttributionPendingIndex.RingCapacity, batch[^1].Frame.Span[0]);
        foreach (var retained in batch) retained.Frame.Dispose();
    }

    [Fact]
    public void AFrameLargerThanThePoolBufferIsRefusedBeforeTheCopy()
    {
        using var pool = new NativeBufferPool(64, 8);
        var index = new FlowAttributionPendingIndex(pool, new FlowTable(16), capacity: 4);

        Assert.Equal(AttributionAdmission.BlockedFlowFull, Admit(index, 53030, length: 128));
        Assert.Equal(0, index.ActiveCount);
        Assert.Equal(0, index.ChargedBytes);
        Assert.Equal(1, index.FlowFullCount);

        // On an existing entry the ring is left untouched, not half-written.
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53031, length: 64));
        Assert.Equal(AttributionAdmission.BlockedFlowFull, Admit(index, 53031, length: 128));
        var batch = new List<RetainedPacket>();
        Assert.Equal(1, index.TakeAll(EntryFor(index, 53031), batch, out _));
        Assert.Equal(64, batch[0].Length);
        foreach (var retained in batch) retained.Frame.Dispose();
    }

    [Fact]
    public void ATtlReclaimIsIdempotentWithTheRunnersCompletion()
    {
        using var pool = new NativeBufferPool(2048, 8);
        var index = new FlowAttributionPendingIndex(pool, new FlowTable(16), capacity: 4);

        Admit(index, 53040);
        var entry = EntryFor(index, 53040);
        var reclaimed = new List<PendingFlowAttribution>();
        index.RemoveExpired(s_start + TimeSpan.FromSeconds(6), reclaimed);

        Assert.Single(reclaimed);
        Assert.Equal(0, index.ChargedBytes);
        Assert.Equal(1, index.TtlExpiredCount);

        // A worker that finished after the reclaim still calls MarkDecided and the pump still
        // claims: the entry is simply no longer in the map, so nothing is credited twice.
        index.MarkDecided(entry, entry.Context, new FlowDecision(FlowAction.Pass, 0, ProxyServerName: null));
        Assert.Equal(AttributionClaim.Claimed, index.Claim(entry));
        Assert.Equal(0, index.ChargedBytes);
        FlowAttributionPendingIndex.DisposeReclaimed(reclaimed[0]);
    }

    [Fact]
    public void TheCooldownIsArmedOnlyOnAGenuineFailure()
    {
        using var pool = new NativeBufferPool(2048, 8);
        var index = new FlowAttributionPendingIndex(pool, new FlowTable(16), capacity: 4);
        var batch = new List<RetainedPacket>();

        Admit(index, 53050);
        index.MarkFailed(EntryFor(index, 53050), shutdown: false);
        Assert.True(index.TryTakeFailed(EntryFor(index, 53050), batch, s_start, out _));
        Assert.Equal(1, index.CooldownCount);

        // The cooldown consumes a retransmission without creating a second entry or attribution.
        Assert.Equal(AttributionAdmission.BlockedCooldown, Admit(index, 53050));
        Assert.Equal(1, index.CooldownBlockCount);
        Assert.Equal(0, index.ActiveCount);

        // Shutdown cancellation unwinds without a tombstone: no cooldown, no failure counter.
        Admit(index, 53051);
        index.MarkFailed(EntryFor(index, 53051), shutdown: true);
        Assert.True(index.TryTakeFailed(EntryFor(index, 53051), batch, s_start, out _));
        Assert.Equal(1, index.CooldownCount);
        Assert.Equal(AttributionAdmission.Deferred, Admit(index, 53051));

        // An elapsed cooldown is pruned and stops blocking.
        index.RemoveExpired(s_start + TimeSpan.FromSeconds(2), []);
        Assert.Equal(0, index.CooldownCount);
    }

    [Fact]
    public void TheDisposeDrainSettlesAndReleasesEveryEntry()
    {
        using var pool = new NativeBufferPool(2048, 8);
        var index = new FlowAttributionPendingIndex(pool, new FlowTable(16), capacity: 8);

        for (ushort port = 53060; port < 53063; port++) Admit(index, port);
        Assert.Equal(3, index.ActiveCount);
        Assert.Equal(3 * FrameLength, index.ChargedBytes);

        var drained = new List<PendingFlowAttribution>();
        index.Seal(drained);

        Assert.Equal(3, drained.Count);
        Assert.Equal(0, index.ActiveCount);
        Assert.Equal(0, index.ChargedBytes);
        foreach (var entry in drained) FlowAttributionPendingIndex.DisposeReclaimed(entry);
        Assert.Equal(0, pool.Stats.Outstanding);

        // A sealed index blocks fail-closed rather than falling back to an inline attribution.
        Assert.Equal(AttributionAdmission.BlockedSealed, Admit(index, 53063));
        Assert.Equal(1, index.SealedCount);
    }

    [Fact]
    public void TheDecidedQueueDecrementsItsCountInTheSameCriticalSectionAsTheDequeue()
    {
        using var pool = new NativeBufferPool(2048, 8);
        var index = new FlowAttributionPendingIndex(pool, new FlowTable(16), capacity: 8);

        Admit(index, 53070);
        Admit(index, 53071);
        index.MarkDecided(EntryFor(index, 53070), EntryFor(index, 53070).Context, new FlowDecision(FlowAction.Pass, 0, ProxyServerName: null));
        index.MarkDecided(EntryFor(index, 53071), EntryFor(index, 53071).Context, new FlowDecision(FlowAction.Pass, 0, ProxyServerName: null));

        Assert.True(index.HasDecided);
        Assert.Equal(2, index.DecidedDepth);

        Assert.True(index.TryDequeueDecided(7, out var first, out var batch));
        Assert.Equal(1, index.DecidedDepth);
        Assert.NotNull(batch);
        Assert.True(index.TryDequeueDecided(7, out var second, out _));
        Assert.Equal(0, index.DecidedDepth);
        Assert.False(index.HasDecided);
        Assert.NotSame(first, second);

        Assert.False(index.TryDequeueDecided(7, out _, out _));
    }

    [Fact]
    public void AFlowClaimedByAnotherPumpAdmitsInlineInsteadOfASecondEntry()
    {
        using var pool = new NativeBufferPool(2048, 8);
        var flows = new FlowTable(16);
        var index = new FlowAttributionPendingIndex(pool, flows, capacity: 8);
        var packet = Packet(53080, 1);

        Assert.True(flows.TryClaimResolved(packet.Context.Key, new FlowDecision(FlowAction.Pass, 0, ProxyServerName: null), out _));

        Assert.Equal(AttributionAdmission.Inline, index.Admit(packet, s_start, out var created, out var resolved));
        Assert.Null(created);
        Assert.NotNull(resolved);
        Assert.Equal(0, index.ActiveCount);
    }

    private static AttributionAdmission Admit(FlowAttributionPendingIndex index, ushort localPort, byte marker = 1, int length = FrameLength) =>
        index.Admit(Packet(localPort, marker, length), s_start, out _, out _);

    private static PendingFlowAttribution EntryFor(FlowAttributionPendingIndex index, ushort localPort) =>
        index.EntryForDiagnostics(Key(localPort)) ?? throw new InvalidOperationException($"No pending entry exists for local port {localPort}.");

    private static AttributionClaim Claim(FlowAttributionPendingIndex index, PendingFlowAttribution entry)
    {
        if (entry.Decision is null) index.MarkDecided(entry, entry.Context, new FlowDecision(FlowAction.Pass, 0, ProxyServerName: null));
        return index.Claim(entry);
    }

    private static CapturedFlowPacket Packet(ushort localPort, byte marker, int length = FrameLength)
    {
        var frame = new byte[length];
        frame[0] = marker;
        return new CapturedFlowPacket(new PacketLease(frame), FlowBuilders.Context(Key(localPort)), new PacketCaptureMetadata(0, 7));
    }

    private static FlowKey Key(ushort localPort) => FlowKey.Create(
        Endpoint.From(System.Net.IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(System.Net.IPAddress.Parse("192.0.2.53"), 443),
        TransportProtocol.Tcp,
        FlowOriginKind.Host);

    /// <summary>A packet on the same transport tuple as <see cref="Key"/> but carrying an adapter slot: two such keys share one transport-tuple entry in the flow table.</summary>
    private static CapturedFlowPacket AdapterPacket(ushort localPort, string adapterId) =>
        new(new PacketLease(new byte[FrameLength]), FlowBuilders.Context(AdapterKey(localPort, adapterId), adapterId: adapterId), new PacketCaptureMetadata(0, 7));

    private static FlowKey AdapterKey(ushort localPort, string adapterId) => FlowKey.Create(
        Endpoint.From(System.Net.IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(System.Net.IPAddress.Parse("192.0.2.53"), 443),
        TransportProtocol.Tcp,
        FlowOriginKind.Host,
        FlowBuilders.SlotOf(adapterId, 1),
        1);
}
