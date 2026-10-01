using System.Globalization;
using System.Net;
using WinForward.Configuration;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// The sweep allocation contract (research F3.3: "no allocation under any table lock"). One fact per
/// sweep site, with the gated tick the design names for it (<c>design.md</c> §6.1): a <em>retiring</em>
/// tick for the synchronous table sweeps (the flow table and the two test-only tables) and a
/// <em>no-op</em> tick over a populated world for the three async legs, whose retiring ticks await
/// disposal outside the gate's scope.
/// </summary>
/// <remarks>
/// <para>
/// Every fact here follows the shape in <c>hot-path.md</c> §"Allocation-gate stability": a bounded run
/// of probe batches that must each read an <em>exactly zero</em> per-thread delta on an unchanged
/// thread before the measured window opens, then one measured window on the same managed thread, then
/// <c>Assert.Equal(0, allocated)</c> — exact, never a threshold. Assertions stay outside the window
/// (<c>Assert.Equal</c> allocates; the boolean <c>Assert.True</c> form does not).
/// </para>
/// <para>
/// Each fact also states the four-property window contract: the driven operation completed
/// synchronously (<c>IsCompletedSuccessfully</c> on the async sites' <c>ValueTask&lt;int&gt;</c>), the
/// managed thread id is captured before the window and asserted unchanged after it, the exact zero, and
/// a thread-independent call-count backstop. A gate whose window changes is re-discriminated by
/// injecting one allocation into it and recording the exact failing byte count.
/// </para>
/// <para>
/// The flow table is no longer the only site that satisfies F3.3: the other sites' gates land with
/// their fixes, because a gate added before its fix would be red on an unmodified tree. The exact red
/// byte count each of those facts first reported is recorded in
/// <c>benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/README.md</c>.
/// </para>
/// </remarks>
public sealed class SweepAllocationGateTests
{
    private const int Flows = 4_096;

    /// <summary>Non-expired entries the predicate sweep must leave alone; they make the predicate's "idle-elapsed only" contract observable.</summary>
    private const int LiveFlows = 1_024;

    /// <summary>The acceptance fact's retired population: the 65,536 flows one round must complete.</summary>
    private const int SweepFlows = 65_536;

    /// <summary>The acceptance fact's held population: the scenario's observer set, idle-elapsed but held by the predicate.</summary>
    private const int HeldFlows = 4_096;

    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    private static readonly IPAddress s_client = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destination = IPAddress.Parse("192.0.2.53");

    [Fact]
    public void FlowTableSweepAllocatesNoManagedBytes()
    {
        var table = new FlowTable(capacity: Flows + 16);
        var keys = BuildKeys();

        // Probe sweeps until the instrument itself is quiet: the first grows the scratch list and
        // returns every state to the pool, so once a sweep reads an exactly-zero delta on one thread
        // the measured sweep sees steady state rather than one-time growth. Requiring the exact zero
        // here is what keeps a genuine sweep allocation failing rather than stabilizing
        // (hot-path.md, "Allocation-gate stability").
        const int maximumProbeSweeps = 8;
        var stabilized = false;
        for (var sweep = 0; sweep < maximumProbeSweeps && !stabilized; sweep++)
        {
            Seed(table, keys);
            var probeThreadId = Environment.CurrentManagedThreadId;
            var probeBefore = GC.GetAllocatedBytesForCurrentThread();
            var probeRemoved = table.RemoveExpired(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.Zero);
            stabilized = Environment.CurrentManagedThreadId == probeThreadId && GC.GetAllocatedBytesForCurrentThread() == probeBefore;
            // The assertion stays outside the probe's measured region: xunit's Assert.Equal allocates.
            Assert.Equal(Flows, probeRemoved);
        }
        Assert.True(stabilized, "the flow-table sweep never became allocation-stable");

        Seed(table, keys);
        var measuredThreadId = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var removed = table.RemoveExpired(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.Zero);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(Flows, removed);
        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// The gate's own discriminating power: the same measurement path must see a known allocation, or
    /// "0 bytes" would only mean the probe cannot see anything.
    /// </summary>
    [Fact]
    public void AllocationProbeSeesAKnownAllocation()
    {
        var allocated = MeasureAllocated(static () =>
        {
            var probe = new byte[4_096];
            GC.KeepAlive(probe);
        });

        Assert.True(allocated >= 4_096, string.Create(CultureInfo.InvariantCulture, $"The allocation probe reported {allocated} bytes for a 4,096-byte allocation."));
    }

    /// <summary>
    /// Site 1 with a hold predicate: the retiring tick a production sweep actually runs (half the idle
    /// entries held by a live session, the rest retired) must stay byte-exact. Regression-only — an
    /// unmodified <see cref="FlowTable"/> already swept at exactly 0 B once its scratch list had
    /// stabilised, so this fact exists to fail if the chunked rewrite starts allocating. The predicate is
    /// exactly one hoisted instance shared by the probe and measured calls: a lambda written at the call
    /// site would allocate its cached delegate on its first invocation, inside the measured window
    /// (<c>HotPathAllocationGateTests.cs:435-436</c>).
    /// </summary>
    [Fact]
    public void FlowTableSweepWithHoldPredicateAllocatesNoManagedBytes()
    {
        var table = new FlowTable(capacity: Flows + LiveFlows + 16);
        var expiredKeys = BuildKeys();
        var liveKeys = BuildKeys(LiveFlows, 30_000);
        var now = DateTimeOffset.UtcNow;
        // ReSharper disable once SuggestVarOrType_Elsewhere -- 'var' cannot bind a method group (CS0815)
        Func<FlowKey, bool> isHeld = IsHeldEvenPort;
        const int heldCount = Flows / 2;

        const int maximumProbeSweeps = 8;
        var stabilized = false;
        for (var sweep = 0; sweep < maximumProbeSweeps && !stabilized; sweep++)
        {
            SeedAt(table, expiredKeys, now - TimeSpan.FromMinutes(2));
            SeedAt(table, liveKeys, now);
            var probeThreadId = Environment.CurrentManagedThreadId;
            var probeBefore = GC.GetAllocatedBytesForCurrentThread();
            var probeRemoved = table.RemoveExpired(now, TimeSpan.FromMinutes(1), isHeld);
            stabilized = Environment.CurrentManagedThreadId == probeThreadId && GC.GetAllocatedBytesForCurrentThread() == probeBefore;
            // The assertion stays outside the probe's measured region, and it is the call-count backstop:
            // a sweep that retired nothing (or everything) would not be the tick this gate claims to cover.
            Assert.Equal(heldCount, probeRemoved);
        }
        Assert.True(stabilized, "the flow-table predicate sweep never became allocation-stable");

        SeedAt(table, expiredKeys, now - TimeSpan.FromMinutes(2));
        SeedAt(table, liveKeys, now);
        var measuredThreadId = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var removed = table.RemoveExpired(now, TimeSpan.FromMinutes(1), isHeld);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(heldCount, removed);
        Assert.Equal(0, allocated);
        Assert.Equal(heldCount + LiveFlows, table.Count);
        Assert.Equal(table.Count, table.LiveStateCountForDiagnostics);
    }

    /// <summary>
    /// The registry invariant across churn, including the shape that catches a round re-reading the stale
    /// slot at <c>cursor == _liveCount</c> after its last removal: an all-expired sweep, a claim, the
    /// single-entry expiry of that claim, then a re-claim, which a doubly-returned state would answer with
    /// the same instance.
    /// </summary>
    [Fact]
    public void FlowTableSweepPreservesLiveRegistryInvariant()
    {
        var table = new FlowTable(capacity: Flows + 16);
        var keys = BuildKeys();

        for (var round = 0; round < 3; round++)
        {
            Seed(table, keys);
            Assert.Equal(Flows, table.RemoveExpired(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.Zero));
            Assert.Equal(0, table.Count);
            Assert.Equal(0, table.LiveStateCountForDiagnostics);
        }

        Seed(table, keys);
        Assert.Equal(Flows, table.RemoveExpired(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.Zero));
        var survivor = BuildKeys(1, 30_000)[0];

        // The stale-slot shape: two claims issued right after a full round. A state returned to the pool
        // twice would be handed to both flows; the registry must mirror the table after each claim.
        var first = BuildKeys(1, 30_001)[0];
        Assert.True(table.TryClaimResolved(survivor, static () => FlowDecision.Fallback(FlowAction.Pass), out var firstState));
        Assert.True(table.TryClaimResolved(first, static () => FlowDecision.Fallback(FlowAction.Pass), out var secondState));
        Assert.NotSame(firstState, secondState);
        Assert.Equal(2, table.Count);
        Assert.Equal(2, table.LiveStateCountForDiagnostics);

        // A single-entry expiry still drains exactly, and the registry follows it back to empty.
        Assert.Equal(2, table.RemoveExpired(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.Zero));
        Assert.Equal(0, table.Count);
        Assert.Equal(0, table.LiveStateCountForDiagnostics);
        Assert.False(table.TryResolve(survivor, out _));
    }

    /// <summary>
    /// Requirement 3's direct proof: the hold predicate is consulted for idle-elapsed candidates only, and
    /// never while the table gate is held (the nested store/tombstone lock edge the design removes).
    /// </summary>
    [Fact]
    public void SweepHoldPredicateRunsOutsideTheTableGate()
    {
        var table = new FlowTable(capacity: Flows + LiveFlows + 16);
        var expiredKeys = BuildKeys();
        var liveKeys = BuildKeys(LiveFlows, 30_000);
        var now = DateTimeOffset.UtcNow;
        SeedAt(table, expiredKeys, now - TimeSpan.FromMinutes(2));
        SeedAt(table, liveKeys, now);

        var calls = 0;
        var callsUnderGate = 0;
        var removed = table.RemoveExpired(now, TimeSpan.FromMinutes(1), _ =>
        {
            calls++;
            if (table.HoldsGateForDiagnostics) callsUnderGate++;
            return false;
        });

        Assert.Equal(Flows, removed);
        Assert.Equal(Flows, calls);
        Assert.Equal(0, callsUnderGate);
        Assert.Equal(LiveFlows, table.Count);
    }

    /// <summary>
    /// The acceptance fact (design §2.3/§6.1, D-A): the work-per-hold bound, by exact counts. 65,536
    /// idle-elapsed flows plus the scenario's observer-equivalent held set, a
    /// <see cref="FlowTable.SweepHoldProbe"/> attached. No hold may examine more than
    /// <see cref="FlowTable.SweepChunkEntries"/> entries, **no hold may remove more than one entry**, the
    /// round must still retire the whole idle population in one call, and the hold counts and histograms are
    /// the recorded shape. No timing is asserted anywhere here: the sweep's own duration is report-only, and
    /// minimal granularity deliberately pays duration for warm-path progress.
    /// </summary>
    [Fact]
    public void FlowTableSweepHoldWorkIsBoundedByChunkEntries()
    {
        var table = new FlowTable(capacity: SweepFlows + HeldFlows + 16);
        var heldKeys = BuildSweepPopulation(HeldFlows, 30_000, TransportProtocol.Tcp);
        var expiredKeys = BuildSweepPopulation(SweepFlows, 10_000, TransportProtocol.Udp);
        var idleStamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);

        // The held set is claimed first, exactly as the scenario claims its observer keys first, so the
        // round meets held candidates before the retired population.
        SeedAt(table, heldKeys, idleStamp);
        SeedAt(table, expiredKeys, idleStamp);

        // ReSharper disable once SuggestVarOrType_Elsewhere -- 'var' cannot bind a method group (CS0815)
        Func<FlowKey, bool> isHeld = IsHeldTcp;
        var probe = new FlowTable.SweepHoldProbe();
        table.HoldProbe = probe;

        var removed = table.RemoveExpired(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), isHeld);

        Assert.Equal(SweepFlows, removed);
        Assert.Equal(HeldFlows, table.Count);
        Assert.Equal(table.Count, table.LiveStateCountForDiagnostics);
        Assert.True(probe.MaxExaminations <= FlowTable.SweepChunkEntries, string.Create(CultureInfo.InvariantCulture, $"a scan hold examined {probe.MaxExaminations} entries"));
        Assert.True(probe.MaxRemovals <= 1, string.Create(CultureInfo.InvariantCulture, $"a removal hold removed {probe.MaxRemovals} entries"));

        // The recorded minimal-granularity shape: every entry is a candidate in this fixture, so the round
        // runs one scan hold (1 examination) and one removal hold per entry — the 65,536 removals each in
        // their own hold, and the held set's holds removing nothing.
        Assert.Equal(SweepFlows + HeldFlows, probe.ScanHolds);
        Assert.Equal(SweepFlows + HeldFlows, probe.RemovalHolds);
        Assert.Equal(1, probe.MaxExaminations);
        Assert.Equal(1, probe.MaxRemovals);
        Assert.Equal(SweepFlows + HeldFlows, probe.ExaminationHistogram[1]);
        Assert.Equal(HeldFlows, probe.RemovalHistogram[0]);
        Assert.Equal(SweepFlows, probe.RemovalHistogram[1]);

        // The held set survives the round by contract; releasing it lets the next round take the rest,
        // which is the "one round ends with Count == 0" shape for a table with no holds left.
        table.HoldProbe = null;
        Assert.Equal(HeldFlows, table.RemoveExpired(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, table.Count);
        Assert.Equal(0, table.LiveStateCountForDiagnostics);
    }

    private static bool IsHeldEvenPort(FlowKey key) => key.Local.Port % 2 == 0;

    private static bool IsHeldTcp(FlowKey key) => key.Protocol == TransportProtocol.Tcp;

    /// <summary>
    /// The production-shape probe the PRD requires: a mostly-live round (4,096 live flows and 32
    /// idle-elapsed ones) records its hold count and shape. No duration assertion — the round's duration is
    /// a report-only series, recorded in the artifact README — but the counts are exact and pin that a live
    /// table pays full <see cref="FlowTable.SweepChunkEntries"/> scan holds with no removals, while each
    /// idle entry costs exactly one scan hold and one single-entry removal hold.
    /// </summary>
    [Fact]
    public void FlowTableProductionShapeSweepRecordsItsHoldShape()
    {
        const int liveFlows = 4_096;
        const int idleFlows = 32;
        var table = new FlowTable(capacity: liveFlows + idleFlows + 16);
        var liveKeys = BuildSweepPopulation(liveFlows, 30_000, TransportProtocol.Udp);
        var idleKeys = BuildSweepPopulation(idleFlows, 10_000, TransportProtocol.Udp);
        var now = DateTimeOffset.UtcNow;
        SeedAt(table, liveKeys, now);
        SeedAt(table, idleKeys, now - TimeSpan.FromMinutes(2));

        var probe = new FlowTable.SweepHoldProbe();
        table.HoldProbe = probe;
        var removed = table.RemoveExpired(now, TimeSpan.FromMinutes(1));
        table.HoldProbe = null;

        Assert.Equal(idleFlows, removed);
        Assert.Equal(liveFlows, table.Count);
        Assert.Equal(table.Count, table.LiveStateCountForDiagnostics);
        Assert.True(probe.MaxExaminations <= FlowTable.SweepChunkEntries, string.Create(CultureInfo.InvariantCulture, $"a scan hold examined {probe.MaxExaminations} entries"));
        Assert.True(probe.MaxRemovals <= 1, string.Create(CultureInfo.InvariantCulture, $"a removal hold removed {probe.MaxRemovals} entries"));

        // 16 full scan holds over the live prefix (no candidates, no removal holds), then one scan hold and
        // one removal hold per idle entry as the swap-removed tails come forward.
        Assert.Equal((liveFlows / FlowTable.SweepChunkEntries) + idleFlows, probe.ScanHolds);
        Assert.Equal(idleFlows, probe.RemovalHolds);
        Assert.Equal(FlowTable.SweepChunkEntries, probe.MaxExaminations);
        Assert.Equal(1, probe.MaxRemovals);
        Assert.Equal(liveFlows / FlowTable.SweepChunkEntries, probe.ExaminationHistogram[FlowTable.SweepChunkEntries]);
        Assert.Equal(idleFlows, probe.ExaminationHistogram[1]);
        Assert.Equal(idleFlows, probe.RemovalHistogram[1]);
        Assert.Equal(0, probe.RemovalHistogram[0]);
    }

    /// <summary>
    /// Site 2 (<c>TcpRedirectTable.RemoveExpired</c>): a retiring tick over 4,096 idle-elapsed associations.
    /// The table has no production caller (tests only), but its gate is a warm packet-path gate, so the
    /// sweep carries the same contract: one scan hold into a reused scratch, then one short hold per
    /// removal with the presence/idleness re-check.
    /// </summary>
    [Fact]
    public void TcpRedirectTableSweepAllocatesNoManagedBytes()
    {
        const int associations = 4_096;
        var table = new TcpRedirectTable(capacity: associations + 16);
        var redirects = BuildRedirects(associations);
        var idleStamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        var now = DateTimeOffset.UtcNow;

        const int maximumProbeSweeps = 8;
        var stabilized = false;
        for (var sweep = 0; sweep < maximumProbeSweeps && !stabilized; sweep++)
        {
            SeedRedirects(table, redirects, idleStamp);
            var probeThreadId = Environment.CurrentManagedThreadId;
            var probeBefore = GC.GetAllocatedBytesForCurrentThread();
            var probeRemoved = table.RemoveExpired(now, TimeSpan.FromMinutes(1));
            stabilized = Environment.CurrentManagedThreadId == probeThreadId && GC.GetAllocatedBytesForCurrentThread() == probeBefore;
            Assert.Equal(associations, probeRemoved);
        }
        Assert.True(stabilized, "the tcp redirect sweep never became allocation-stable");

        SeedRedirects(table, redirects, idleStamp);
        var measuredThreadId = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var removed = table.RemoveExpired(now, TimeSpan.FromMinutes(1));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(associations, removed);
        Assert.Equal(0, allocated);
        // Thread-independent backstop: a sweep that stopped retiring, or that left the table populated,
        // would not be the tick this gate claims to cover.
        Assert.Equal(0, table.Count);
    }

    private static (FlowKey Original, Endpoint Translated)[] BuildRedirects(int count)
    {
        var redirects = new (FlowKey, Endpoint)[count];
        for (var index = 0; index < count; index++)
        {
            var original = FlowKey.Create(
                Endpoint.From(s_client, checked((ushort)(10_000 + index))),
                Endpoint.From(s_destination, 443),
                TransportProtocol.Tcp,
                FlowOriginKind.Host);
            redirects[index] = (original, Endpoint.From(s_destination, checked((ushort)(20_000 + index))));
        }

        return redirects;
    }

    private static void SeedRedirects(TcpRedirectTable table, (FlowKey Original, Endpoint Translated)[] redirects, DateTimeOffset now)
    {
        foreach (var (original, translated) in redirects)
        {
            if (!table.TryClaim(original, original.Remote, 0x1234, translated, forwardLocalAddress: null, now, out _))
            {
                Assert.Fail("The sweep gate could not seed the redirect table.");
            }
        }
    }

    /// <summary>
    /// Site 3 (<c>TcpRedirectSessionStore.RemoveExpiredAsync</c>, with <c>TcpRedirectTombstoneTable</c>
    /// riding the same tick): the <em>no-op</em> tick over a populated store — registered Redirecting
    /// sessions that are not idle-elapsed, plus unexpired tombstones. The no-op tick is the gated shape
    /// because a retiring tick awaits listener/relay disposal, which is outside the gate's scope by design.
    /// </summary>
    [Fact]
    public async Task TcpRedirectSessionStoreSweepAllocatesNoManagedBytes()
    {
        const int sessions = 64;
        var now = DateTimeOffset.UtcNow;
        var store = BuildPopulatedSessionStore(sessions, now);
        Assert.Equal(sessions, store.SessionCount);

        const int maximumProbeTicks = 8;
        var stabilized = false;
        for (var tick = 0; tick < maximumProbeTicks && !stabilized; tick++)
        {
            var probeThreadId = Environment.CurrentManagedThreadId;
            var probeBefore = GC.GetAllocatedBytesForCurrentThread();
            var probe = store.RemoveExpiredAsync(now, TimeSpan.FromMinutes(5), prunePending: null);
            Assert.True(probe.IsCompletedSuccessfully, "the no-op sweep must complete synchronously");
            var probeRemoved = await probe;
            stabilized = Environment.CurrentManagedThreadId == probeThreadId && GC.GetAllocatedBytesForCurrentThread() == probeBefore;
            Assert.Equal(0, probeRemoved);
        }
        Assert.True(stabilized, "the tcp session-store sweep never became allocation-stable");

        var measuredThreadId = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var pending = store.RemoveExpiredAsync(now, TimeSpan.FromMinutes(5), prunePending: null);
        Assert.True(pending.IsCompletedSuccessfully, "the no-op sweep must complete synchronously");
        var removed = await pending;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(0, removed);
        Assert.Equal(0, allocated);
        // Thread-independent backstop: the populated world must survive the no-op tick.
        Assert.Equal(sessions, store.SessionCount);
        await store.DisposeAsync();
    }

    private static TcpRedirectSessionStore BuildPopulatedSessionStore(int sessions, DateTimeOffset now)
    {
        var table = new TcpRedirectTable(capacity: sessions + 16);
        var store = new TcpRedirectSessionStore(table, NullRuntimeLogger.Instance, sessions + 16, TimeProvider.System);
        foreach (var association in BuildClaimedRedirects(table, sessions, now))
        {
            var session = CreateSession(association, new FakeListener(association.TranslatedListenerTuple));
            Assert.NotNull(store.TryRegister(session));
            // Tombstones ride the same tick, so populate them unexpired rather than leaving that leg vacuous.
            store.Tombstones.TryAdd(association.OriginalKey, association.ReverseSourceEndpoint, association.ReverseDestinationEndpoint, now + TimeSpan.FromMinutes(1));
        }

        return store;
    }

    private static TcpRedirectAssociation[] BuildClaimedRedirects(TcpRedirectTable table, int count, DateTimeOffset now)
    {
        var redirects = BuildRedirects(count);
        var associations = new TcpRedirectAssociation[count];
        for (var index = 0; index < count; index++)
        {
            var (original, translated) = redirects[index];
            if (!table.TryClaim(original, original.Remote, 0x1234, translated, forwardLocalAddress: null, now, out var association) || association is null)
            {
                Assert.Fail("The sweep gate could not seed the redirect table.");
            }

            associations[index] = association;
        }

        return associations;
    }

    /// <summary>
    /// Site 4 (<c>UdpProxyCoordinator.RemoveExpiredAsync</c>): the <em>no-op</em> tick over a populated
    /// session set (16 fake-transport sessions, none idle-elapsed), which is the tick that repeats every
    /// 5 s in production (the effective retention floor is the 5 s one-shot class). The retiring tick is
    /// not the gated shape because its teardown awaits slot removal outside this gate's scope; the
    /// adaptive two-class tick has its own gate in <c>UdpAdaptiveSweepAllocationGateTests</c>.
    /// </summary>
    [Fact]
    public async Task UdpProxyCoordinatorSweepAllocatesNoManagedBytes()
    {
        const int sessions = 16;
        var factory = new FakeTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink());
        for (var index = 0; index < sessions; index++)
        {
            Assert.True(await coordinator.TrySendSpanAsync(MakeUdpFlow(checked((ushort)(53 + index))), s_server, [1], default, CancellationToken.None));
        }
        await WaitForAsync(() => factory.Transports.Count == sessions);
        Assert.Equal(sessions, coordinator.SessionCount);

        var now = DateTimeOffset.UtcNow;
        const int maximumProbeTicks = 8;
        var stabilized = false;
        for (var tick = 0; tick < maximumProbeTicks && !stabilized; tick++)
        {
            var probeThreadId = Environment.CurrentManagedThreadId;
            var probeBefore = GC.GetAllocatedBytesForCurrentThread();
            var probe = coordinator.RemoveExpiredAsync(now, TimeSpan.FromMinutes(5));
            Assert.True(probe.IsCompletedSuccessfully, "the no-op sweep must complete synchronously");
            var probeRemoved = await probe;
            stabilized = Environment.CurrentManagedThreadId == probeThreadId && GC.GetAllocatedBytesForCurrentThread() == probeBefore;
            Assert.Equal(0, probeRemoved);
        }
        Assert.True(stabilized, "the udp coordinator sweep never became allocation-stable");

        var measuredThreadId = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var pending = coordinator.RemoveExpiredAsync(now, TimeSpan.FromMinutes(5));
        Assert.True(pending.IsCompletedSuccessfully, "the no-op sweep must complete synchronously");
        var removed = await pending;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(0, removed);
        Assert.Equal(0, allocated);
        // Thread-independent backstop: every populated session survived the no-op tick.
        Assert.Equal(sessions, coordinator.SessionCount);
        Assert.Equal(sessions, factory.Transports.Count);
    }

    /// <summary>
    /// Site 5 (<c>UdpAssociationPool.SweepIdleAssociationsAsync</c>): the <em>no-op</em> tick over a
    /// populated pool whose shared associations have outstanding leases, so nothing is retirable. The
    /// scan is one hold covering the retire test and the shared removals; the disposal loop is outside.
    /// </summary>
    [Fact]
    public async Task UdpAssociationPoolSweepAllocatesNoManagedBytes()
    {
        const int flowsPerAssociation = 4;
        const int leases = 16;
        await using var server = CreateAssociationPoolServer();
        await using var pool = UdpAssociationFakes.CreatePool(UdpAssociationReuseMode.Always, flowsPerAssociation: flowsPerAssociation);
        var held = new List<UdpAssociationLease>();
        try
        {
            for (var index = 0; index < leases; index++) held.Add(await pool.RentAsync(server.Server, CancellationToken.None));
            Assert.Equal(leases / flowsPerAssociation, pool.AssociationCount);
            Assert.Equal(leases, pool.LeasedFlowCount);

            var now = DateTimeOffset.UtcNow;
            const int maximumProbeTicks = 8;
            var stabilized = false;
            for (var tick = 0; tick < maximumProbeTicks && !stabilized; tick++)
            {
                var probeThreadId = Environment.CurrentManagedThreadId;
                var probeBefore = GC.GetAllocatedBytesForCurrentThread();
                var probe = pool.SweepIdleAssociationsAsync(now);
                Assert.True(probe.IsCompletedSuccessfully, "the no-op sweep must complete synchronously");
                var probeRemoved = await probe;
                stabilized = Environment.CurrentManagedThreadId == probeThreadId && GC.GetAllocatedBytesForCurrentThread() == probeBefore;
                Assert.Equal(0, probeRemoved);
            }
            Assert.True(stabilized, "the udp association pool sweep never became allocation-stable");

            var measuredThreadId = Environment.CurrentManagedThreadId;
            var before = GC.GetAllocatedBytesForCurrentThread();
            var pending = pool.SweepIdleAssociationsAsync(now);
            var synchronous = pending.IsCompletedSuccessfully;
            var removed = await pending;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(synchronous, "the no-op sweep must complete synchronously");
            Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
            Assert.Equal(0, removed);
            Assert.Equal(0, allocated);
            // Thread-independent backstop: the leased world survived the no-op tick.
            Assert.Equal(leases, pool.LeasedFlowCount);
            Assert.Equal(leases / flowsPerAssociation, pool.AssociationCount);
        }
        finally
        {
            // The pool's drain joins every holder by design, so a failing assertion must not leave one.
            foreach (var lease in held) await lease.DisposeAsync();
        }
    }

    /// <summary>
    /// Site 6 (<c>UdpAssociationTable.RemoveExpired</c>): a retiring tick over 64 idle-elapsed
    /// associations. The table has no production caller (tests only); the fact exists so "every sweep site
    /// allocates nothing" holds repo-wide.
    /// </summary>
    [Fact]
    public void UdpAssociationTableSweepAllocatesNoManagedBytes()
    {
        const int associations = 64;
        var table = new UdpAssociationTable(capacity: associations + 16);
        var idleStamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        var now = DateTimeOffset.UtcNow;

        var keys = BuildUdpAssociationKeys(associations);
        const int maximumProbeSweeps = 8;
        var stabilized = false;
        for (var sweep = 0; sweep < maximumProbeSweeps && !stabilized; sweep++)
        {
            SeedUdpAssociations(table, keys, idleStamp);
            var probeThreadId = Environment.CurrentManagedThreadId;
            var probeBefore = GC.GetAllocatedBytesForCurrentThread();
            var probeRemoved = table.RemoveExpired(now, TimeSpan.FromMinutes(1));
            stabilized = Environment.CurrentManagedThreadId == probeThreadId && GC.GetAllocatedBytesForCurrentThread() == probeBefore;
            Assert.Equal(associations, probeRemoved);
        }
        Assert.True(stabilized, "the udp association table sweep never became allocation-stable");

        SeedUdpAssociations(table, keys, idleStamp);
        var measuredThreadId = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var removed = table.RemoveExpired(now, TimeSpan.FromMinutes(1));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(associations, removed);
        Assert.Equal(0, allocated);
        // Thread-independent backstop: the retiring tick must have emptied the table (both indexes).
        Assert.False(table.TryFindOriginal(keys[0], now, out _));
        Assert.False(table.TryFindOriginal(keys[^1], now, out _));
    }

    private static ScriptedSocks5UdpServer CreateAssociationPoolServer() =>
        new(new IPEndPoint(IPAddress.Loopback, 41_000), ordinal => new IPEndPoint(IPAddress.Loopback, 41_000 + ordinal));

    private static FlowKey[] BuildUdpAssociationKeys(int count)
    {
        var keys = new FlowKey[count];
        for (var index = 0; index < count; index++)
        {
            keys[index] = FlowKey.Create(
                Endpoint.From(s_client, checked((ushort)(10_000 + index))),
                Endpoint.From(s_destination, 53),
                TransportProtocol.Udp,
                FlowOriginKind.Host);
        }

        return keys;
    }

    private static void SeedUdpAssociations(UdpAssociationTable table, FlowKey[] keys, DateTimeOffset now)
    {
        for (var index = 0; index < keys.Length; index++)
        {
            var relay = new RelayAlias(FlowKey.Create(
                Endpoint.From(s_client, checked((ushort)(20_000 + index))),
                Endpoint.From(s_destination, 50_000),
                TransportProtocol.Udp,
                FlowOriginKind.Host));
            table.Claim(keys[index], relay, now);
        }
    }

    private static FlowKey MakeUdpFlow(ushort remotePort) =>
        FlowKey.Create(Endpoint.From(s_client, 53_000), Endpoint.From(s_destination, remotePort), TransportProtocol.Udp, FlowOriginKind.Host);

    private static long MeasureAllocated(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void Seed(FlowTable table, FlowKey[] keys)
    {
        foreach (var key in keys)
        {
            if (!table.TryClaimResolved(key, static () => FlowDecision.Fallback(FlowAction.Pass), out _))
            {
                Assert.Fail("The sweep gate could not seed the flow table.");
            }
        }
    }

    /// <summary>
    /// Claims whatever is missing and stamps every key's activity to <paramref name="lastActivityUtc"/>, so
    /// each probe sweep starts from the same populated world: a re-claim alone re-arms the idle window and
    /// would leave the gate's retiring tick with nothing to retire.
    /// </summary>
    private static void SeedAt(FlowTable table, FlowKey[] keys, DateTimeOffset lastActivityUtc)
    {
        foreach (var key in keys)
        {
            if (!table.TryClaimResolved(key, static () => FlowDecision.Fallback(FlowAction.Pass), out var state))
            {
                Assert.Fail("The sweep gate could not seed the flow table.");
            }

            state!.Touch(lastActivityUtc);
        }
    }

    private static FlowKey[] BuildKeys(int count = Flows, int portFloor = 10_000)
    {
        var keys = new FlowKey[count];
        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = FlowKey.Create(
                Endpoint.From(s_client, checked((ushort)(portFloor + index))),
                Endpoint.From(s_destination, 443),
                TransportProtocol.Tcp,
                FlowOriginKind.Host);
        }

        return keys;
    }

    /// <summary>
    /// The acceptance fact's fixture: <paramref name="count"/> distinct keys of one protocol. The local
    /// port cycles over 4,096 values and the remote port carries the rest, so a 65,536-key population fits
    /// the <see cref="ushort"/> port space.
    /// </summary>
    private static FlowKey[] BuildSweepPopulation(int count, int localPortFloor, TransportProtocol protocol)
    {
        var keys = new FlowKey[count];
        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = FlowKey.Create(
                Endpoint.From(s_client, checked((ushort)(localPortFloor + (index % 4_096)))),
                Endpoint.From(s_destination, checked((ushort)(1_000 + (index / 4_096)))),
                protocol,
                FlowOriginKind.Host);
        }

        return keys;
    }
}
