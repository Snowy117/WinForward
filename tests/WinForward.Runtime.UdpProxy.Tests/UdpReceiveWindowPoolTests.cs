using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The receive-window pool's sizing contract: one lease per live session plus a documented
/// retire/admit allowance, so a population cycle at the configured session capacity stops running on
/// tracked overflow allocations.
/// <para>
/// <see cref="NativeBufferPool"/>'s counter counts every rent that missed the free list, so a first
/// fill of N live sessions reads N at <em>any</em> capacity; the discriminating reading is the growth
/// a second population cycle adds. The red-before — capacity 256 with a 300-session population, which
/// grew by 44 on the second cycle — is recorded in
/// <c>benchmarks/results/2026-10-01-udp-session-footprint/README.md</c> and asserted by
/// <c>TheRetiredDefaultCapacityGrowsByTheUnpooledPopulationAcrossACycle</c>, so the green gate stays
/// falsifiable in the tree.
/// </para>
/// </summary>
public sealed class UdpReceiveWindowPoolTests
{
    /// <summary>The population the exact cycle gate drives; small enough to run in-process, above the retired 256-lease default.</summary>
    private const int CycleSessions = 300;

    private const int MaximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;

    private static readonly byte[] s_populatePayload = [1];
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    [Fact]
    public void ReceiveWindowPoolCapacityIsTheSessionCapacityPlusTheRetireAllowance()
    {
        const int defaultFanOut = ConfigurationLoader.DefaultUdpAssociationFlowsPerAssociation;

        // The floor: four association-wide fault bursts at the default fan-out.
        Assert.Equal(4 * defaultFanOut, UdpProxyCoordinator.ReceiveWindowRetireHeadroom(CycleSessions));
        Assert.Equal(CycleSessions + (4 * defaultFanOut), UdpProxyCoordinator.ReceiveWindowPoolCapacity(CycleSessions));
        Assert.Equal(364, UdpProxyCoordinator.ReceiveWindowPoolCapacity(CycleSessions));

        // The proportional term: a sixteenth of the shipped session capacity dominates the floor there.
        const int shipped = ConfigurationLoader.DefaultUdpSessionCapacity;
        Assert.Equal(shipped / 16, UdpProxyCoordinator.ReceiveWindowRetireHeadroom(shipped));
        Assert.Equal(17_408, UdpProxyCoordinator.ReceiveWindowPoolCapacity(shipped));

        // The rule is never an exact capacity: the retire/admit overlap has no in-code bound.
        foreach (var capacity in new[] { 1, CycleSessions, 4_096, shipped })
        {
            Assert.True(UdpProxyCoordinator.ReceiveWindowPoolCapacity(capacity) > capacity);
            Assert.Equal(capacity + Math.Max(4 * defaultFanOut, capacity / 16), UdpProxyCoordinator.ReceiveWindowPoolCapacity(capacity));
        }
    }

    [Fact]
    public void ReceiveWindowPoolSizedFromTheSessionCapacityDoesNotGrowAcrossAPopulationCycle()
    {
        using var pool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(1_514), UdpProxyCoordinator.ReceiveWindowPoolCapacity(CycleSessions));
        var leases = new List<NativeLease>(CycleSessions);
        long firstWave;
        long secondWave;
        try
        {
            RentAll(pool, leases);
            firstWave = pool.Stats.OverflowAllocations;
            ReleaseAll(leases);
            RentAll(pool, leases);
            secondWave = pool.Stats.OverflowAllocations;
        }
        finally
        {
            // Released before any assertion so a failing window cannot strand a rental.
            ReleaseAll(leases);
        }

        var balance = pool.Stats;
        Assert.Equal(CycleSessions, firstWave);
        Assert.Equal(firstWave, secondWave);
        Assert.Equal(2L * CycleSessions, balance.Rented);
        Assert.Equal(2L * CycleSessions, balance.Returned);
        Assert.Equal(CycleSessions, balance.InPool);
        Assert.Equal(0, balance.Outstanding);
    }

    [Fact]
    public void TheRetiredDefaultCapacityGrowsByTheUnpooledPopulationAcrossACycle()
    {
        // The red-before the rule above replaces, kept as an executable fact instead of a transient
        // probe: at the retired default capacity the free list can never hold the population, so the
        // second cycle allocates exactly the unpooled population as fresh leases. Without this fact the
        // green cycle gate would be unfalsifiable — a counter that stopped reporting growth would pass
        // it silently. The capacity argument is deliberately omitted: 256 is `NativeBufferPool`'s own
        // default, which is exactly what a composition that passed no capacity used to get.
        const int retiredCapacity = 256;
        using var pool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(1_514));
        var leases = new List<NativeLease>(CycleSessions);
        long firstWave;
        long secondWave;
        try
        {
            RentAll(pool, leases);
            firstWave = pool.Stats.OverflowAllocations;
            ReleaseAll(leases);
            RentAll(pool, leases);
            secondWave = pool.Stats.OverflowAllocations;
        }
        finally
        {
            ReleaseAll(leases);
        }

        Assert.Equal(CycleSessions, firstWave);
        Assert.Equal(CycleSessions - retiredCapacity, secondWave - firstWave);
        Assert.Equal(retiredCapacity, pool.Stats.InPool);
        Assert.Equal(0, pool.Stats.Outstanding);
    }

    [Fact]
    public async Task ACoordinatorCyclesItsSessionCapacityWithoutOverflowGrowth()
    {
        var factory = new FakeTransportFactory();
        using var setupQueuePool = new NativeBufferPool(MaximumFrameSize);
        using var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(MaximumFrameSize), UdpProxyCoordinator.ReceiveWindowPoolCapacity(CycleSessions));
        using var setupExecutor = new SetupExecutor();
        var flows = new FlowKey[CycleSessions];
        for (var index = 0; index < flows.Length; index++) flows[index] = MakeUdpFlow(index);

        var firstCycle = CreateCoordinator(factory, setupQueuePool, receiveWindowPool, setupExecutor);
        try
        {
            await PopulateAsync(firstCycle, factory, flows, expectedCreated: flows.Length);
        }
        finally
        {
            await firstCycle.DisposeAsync();
        }

        var firstWave = receiveWindowPool.Stats.OverflowAllocations;
        Assert.Equal(0, receiveWindowPool.Stats.Outstanding);

        var secondCycle = CreateCoordinator(factory, setupQueuePool, receiveWindowPool, setupExecutor);
        try
        {
            await PopulateAsync(secondCycle, factory, flows, expectedCreated: 2 * flows.Length);
        }
        finally
        {
            await secondCycle.DisposeAsync();
        }

        Assert.Equal(flows.Length, firstWave);
        Assert.Equal(firstWave, receiveWindowPool.Stats.OverflowAllocations);
    }

    private static UdpProxyCoordinator CreateCoordinator(
        IUdpProxyTransportFactory factory,
        NativeBufferPool setupQueuePool,
        NativeBufferPool receiveWindowPool,
        SetupExecutor setupExecutor) =>
        UdpCoordinatorFakes.CreateCoordinator(
            factory,
            new NoopResponseSink(),
            new UdpProxyOptions { Capacity = CycleSessions },
            setupQueuePool,
            receiveWindowPool,
            setupExecutor);

    /// <summary>
    /// Drives one full population and returns only once it is proven live: the transport factory's
    /// created count (an independent counter) and the coordinator's own session count must both agree
    /// with the offered population, so a cycle whose sessions silently failed to build cannot read as
    /// a pool result.
    /// </summary>
    private static async Task PopulateAsync(UdpProxyCoordinator coordinator, FakeTransportFactory factory, FlowKey[] flows, int expectedCreated)
    {
        for (var round = 0; round < 3 && factory.CreateCalls < expectedCreated; round++)
        {
            foreach (var flow in flows)
            {
                _ = await coordinator.TrySendSpanAsync(flow, s_server, s_populatePayload, default, CancellationToken.None);
            }

            await WaitForAsync(() => factory.CreateCalls >= expectedCreated);
        }

        await WaitForAsync(() => coordinator.SessionCount == flows.Length);
        Assert.Equal(expectedCreated, factory.CreateCalls);
        Assert.Equal(flows.Length, coordinator.SessionCount);
    }

    private static void RentAll(NativeBufferPool pool, List<NativeLease> leases)
    {
        for (var index = 0; index < CycleSessions; index++) leases.Add(pool.Rent());
    }

    private static void ReleaseAll(List<NativeLease> leases)
    {
        foreach (var lease in leases) lease.Dispose();
        leases.Clear();
    }

    private static FlowKey MakeUdpFlow(int index) =>
        FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), checked((ushort)(53_000 + index))),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), 53),
            TransportProtocol.Udp,
            FlowOriginKind.Host);
}
