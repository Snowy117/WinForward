using System.Net;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// Association placement and lifetime: the lease exactly-once contract, the <c>off</c> and
/// <c>always</c> modes, per-server isolation, least-loaded placement with its creation-order
/// tie-break, the per-server cap falling back to private associations instead of refusing a flow,
/// warm retention on the fake clock, and the drain that joins every lease holder.
/// <para>
/// Every association advertises a distinct relay endpoint (one port per control connection), which
/// is what makes placement observable: two leases on the same port share one association.
/// </para>
/// </summary>
public sealed class UdpAssociationPoolTests
{
    private const int FirstRelayPort = 41_000;

    [Fact]
    public async Task LeaseIsReleasedExactlyOnce()
    {
        await using var scope = CreateScope(UdpAssociationReuseMode.Always);
        var first = await scope.RentAsync();
        var second = await scope.RentAsync();
        Assert.Equal(2, scope.Pool.LeasedFlowCount);
        Assert.Equal(1, scope.Pool.AssociationCount);

        await first.DisposeAsync();
        await first.DisposeAsync();

        Assert.Equal(1, scope.Pool.LeasedFlowCount);
        await second.DisposeAsync();
        Assert.Equal(0, scope.Pool.LeasedFlowCount);
        // The association is warm, not closed: its control connection is still live for the next flow.
        Assert.Equal(1, scope.Pool.AssociationCount);
        Assert.Equal(1, scope.Server.LiveConnectionCount);
    }

    [Fact]
    public async Task OffModeOpensOneAssociationPerLeaseAndClosesItOnRelease()
    {
        await using var scope = CreateScope(UdpAssociationReuseMode.Off);
        var leases = new List<UdpAssociationLease>();
        for (var index = 0; index < 5; index++) leases.Add(await scope.RentAsync());

        Assert.Equal(5, scope.Pool.AssociationCount);
        Assert.Equal(5, scope.Pool.LeasedFlowCount);
        Assert.Equal(5, scope.Server.ConnectionCount);
        Assert.Equal(5, leases.Select(static lease => lease.RelayEndpoint.Port).Distinct().Count());

        foreach (var lease in leases) await lease.DisposeAsync();

        Assert.Equal(0, scope.Pool.AssociationCount);
        Assert.Equal(0, scope.Pool.LeasedFlowCount);
        await WaitForAsync(() => scope.Server.LiveConnectionCount == 0);
    }

    [Fact]
    public async Task AutoModeSharesUntilEvidenceSaysPerFlowOnly()
    {
        // Step 3 contract: auto is the production default — share, with passive detection. Two flows
        // with no evidence between them land on one association and the server stays on trial;
        // UdpAssociationCapabilityTests pins the flip itself.
        await using var scope = CreateScope(UdpAssociationReuseMode.Auto);
        var first = await scope.RentAsync();
        var second = await scope.RentAsync();

        Assert.Equal(1, scope.Pool.AssociationCount);
        Assert.Equal(1, scope.Server.ConnectionCount);
        Assert.Equal(first.RelayEndpoint.Port, second.RelayEndpoint.Port);
        Assert.Equal(UdpServerCapability.Unknown, scope.Pool.CapabilityOf(scope.Server.Server));
    }

    [Fact]
    public async Task AlwaysModeSharesSixteenFlowsAndOpensASecondAssociationAtSeventeen()
    {
        await using var scope = CreateScope(UdpAssociationReuseMode.Always);
        var leases = new List<UdpAssociationLease>();
        for (var index = 0; index < UdpAssociationPool.DefaultFlowsPerAssociation; index++) leases.Add(await scope.RentAsync());

        Assert.Equal(1, scope.Pool.AssociationCount);
        Assert.Equal(1, scope.Server.ConnectionCount);
        Assert.Single(leases.Select(static lease => lease.RelayEndpoint.Port).Distinct());

        var seventeenth = await scope.RentAsync();
        Assert.Equal(2, scope.Pool.AssociationCount);
        Assert.Equal(2, scope.Server.ConnectionCount);
        Assert.NotEqual(leases[0].RelayEndpoint.Port, seventeenth.RelayEndpoint.Port);
        Assert.Equal(UdpAssociationPool.DefaultFlowsPerAssociation + 1, scope.Pool.LeasedFlowCount);
    }

    [Fact]
    public async Task ServersNeverShareAnAssociation()
    {
        var registry = new SelfTrafficRegistry();
        await using var first = CreateServer(portBase: FirstRelayPort);
        await using var second = CreateServer(portBase: FirstRelayPort + 100);
        var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Always);
        var firstLease = await pool.RentAsync(first.Server, CancellationToken.None);
        var secondLease = await pool.RentAsync(second.Server, CancellationToken.None);
        try
        {
            Assert.Equal(2, pool.AssociationCount);
            Assert.NotEqual(firstLease.RelayEndpoint, secondLease.RelayEndpoint);
            Assert.Equal(1, first.ConnectionCount);
            Assert.Equal(1, second.ConnectionCount);
        }
        finally
        {
            await firstLease.DisposeAsync();
            await secondLease.DisposeAsync();
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public async Task PlacementPrefersTheLeastLoadedAssociationAndBreaksTiesByCreationOrder()
    {
        await using var scope = CreateScope(UdpAssociationReuseMode.Always);
        var first = new List<UdpAssociationLease>();
        for (var index = 0; index < UdpAssociationPool.DefaultFlowsPerAssociation; index++) first.Add(await scope.RentAsync());
        var second = await scope.RentAsync();
        Assert.Equal(2, scope.Pool.AssociationCount);

        // 16 on the first association, 1 on the second: releasing 14 leaves 2 vs 1, so the next
        // flow must land on the second (least loaded).
        for (var index = 0; index < 14; index++) await first[index].DisposeAsync();
        var leastLoaded = await scope.RentAsync();
        Assert.Equal(second.RelayEndpoint.Port, leastLoaded.RelayEndpoint.Port);

        // 2 vs 2 is a tie: creation order keeps the first association the winner.
        var tieBreak = await scope.RentAsync();
        Assert.Equal(first[14].RelayEndpoint.Port, tieBreak.RelayEndpoint.Port);
    }

    [Fact]
    public async Task PerServerCeilingFallsBackToAPrivateAssociationInsteadOfRefusingTheFlow()
    {
        // Both bounds are configuration, not constants: with a ceiling of two associations of at
        // most three flows the first six flows are shared, and the seventh is still served — from a
        // private association — because the pool never refuses a flow.
        await using var scope = CreateScope(UdpAssociationReuseMode.Always, maxAssociationsPerServer: 2, flowsPerAssociation: 3);
        const int sharedCapacity = 2 * 3;
        for (var index = 0; index < sharedCapacity; index++) _ = await scope.RentAsync();

        Assert.Equal(2, scope.Pool.AssociationCount);
        Assert.Equal(sharedCapacity, scope.Pool.LeasedFlowCount);

        // The ceiling is reached: the flow is still served, from a private association.
        var overCap = await scope.RentAsync();
        Assert.Equal(3, scope.Pool.AssociationCount);
        Assert.Equal(sharedCapacity + 1, scope.Pool.LeasedFlowCount);

        // A private association dies with its only lease and never enters the shared set.
        await overCap.DisposeAsync();
        Assert.Equal(2, scope.Pool.AssociationCount);
        Assert.Equal(sharedCapacity, scope.Pool.LeasedFlowCount);

        Assert.Equal(3, scope.Server.ConnectionCount);
    }

    [Fact]
    public async Task IdleRetirementWaitsOutTheRetentionWindowAndNeverClosesALeasedAssociation()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        await using var scope = CreateScope(UdpAssociationReuseMode.Always, time);
        var first = await scope.RentAsync();
        var second = await scope.RentAsync();
        await first.DisposeAsync();

        time.Advance(UdpAssociationPool.s_idleRetireTimeout + TimeSpan.FromSeconds(1));
        Assert.Equal(0, await scope.Pool.SweepIdleAssociationsAsync(time.GetUtcNow()));
        Assert.Equal(1, scope.Pool.AssociationCount);

        await second.DisposeAsync();
        time.Advance(UdpAssociationPool.s_idleRetireTimeout - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await scope.Pool.SweepIdleAssociationsAsync(time.GetUtcNow()));
        Assert.Equal(1, scope.Pool.AssociationCount);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await scope.Pool.SweepIdleAssociationsAsync(time.GetUtcNow()));
        Assert.Equal(0, scope.Pool.AssociationCount);
        await WaitForAsync(() => scope.Server.LiveConnectionCount == 0);
    }

    [Fact]
    public async Task WarmAssociationIsReusedAfterAnIdleWindowAndRetiredOnlyByTheSweep()
    {
        await using var scope = CreateScope(UdpAssociationReuseMode.Always);
        var lease = await scope.RentAsync();
        var port = lease.RelayEndpoint.Port;
        await lease.DisposeAsync();

        var reused = await scope.RentAsync();

        Assert.Equal(port, reused.RelayEndpoint.Port);
        Assert.Equal(1, scope.Server.ConnectionCount);
    }

    [Fact]
    public async Task DisposeJoinsEveryLeaseHolderAndRefusesNewRents()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = CreateServer();
        var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Always);
        var lease = await pool.RentAsync(server.Server, CancellationToken.None);
        var registration = await pool.RentAsync(server.Server, CancellationToken.None);

        var drain = pool.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(drain.IsCompleted, "the pool drain must wait for its lease holders");
        var refusal = pool.RentAsync(server.Server, CancellationToken.None);
        await Assert.ThrowsAsync<ObjectDisposedException>(refusal.AsTask);

        // A held lease can still be used while the drain waits, then released.
        Assert.Equal(1, pool.AssociationCount);
        await lease.DisposeAsync();
        await Task.Delay(50);
        Assert.False(drain.IsCompleted);
        await registration.DisposeAsync();

        await drain;
        Assert.Equal(0, pool.AssociationCount);
        Assert.Equal(0, pool.LeasedFlowCount);
        await WaitForAsync(() => server.LiveConnectionCount == 0);
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task FailedDialLeavesNoAssociationBehindAndTheNextRentRetries()
    {
        var registry = new SelfTrafficRegistry();
        await using var server = CreateServer();
        var attempts = 0;
        await using var pool = new UdpAssociationPool(
            registry,
            UdpAssociationReuseMode.Always,
            createControl: (target, token) => Interlocked.Increment(ref attempts) == 1
                ? ValueTask.FromException<Socks5ControlConnection>(new IOException("control setup failed"))
                : Socks5ControlConnection.ConnectAsync(target, token));

        await Assert.ThrowsAsync<IOException>(() => pool.RentAsync(server.Server, CancellationToken.None).AsTask());
        Assert.Equal(0, pool.AssociationCount);
        Assert.Equal(0, pool.LeasedFlowCount);

        var lease = await pool.RentAsync(server.Server, CancellationToken.None);
        Assert.Equal(2, attempts);
        Assert.Equal(1, pool.AssociationCount);
        Assert.Equal(1, pool.LeasedFlowCount);
        Assert.NotNull(lease.RelayEndpoint);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task NTransportsFromOneAlwaysPoolShareOneControlConnectionAndOneRelay()
    {
        const int flows = 8;
        var registry = new SelfTrafficRegistry();
        await using var server = CreateServer();
        await using var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Always);
        var factory = new Socks5UdpTransportFactory(pool, registry, UdpFrameBuilder.DefaultMaximumEthernetFrame);
        var transports = new List<IUdpProxyTransport>();
        try
        {
            for (var index = 0; index < flows; index++) transports.Add(await factory.CreateAsync(ProxyTarget.FromServer(server.Server), CancellationToken.None));

            // One authenticated control connection and one negotiated relay serve every flow…
            Assert.Equal(1, server.ConnectionCount);
            Assert.Equal(1, server.AssociateReplyCount);
            Assert.Equal(1, pool.AssociationCount);
            Assert.Equal(flows, pool.LeasedFlowCount);
            Assert.Single(transports.Select(static transport => transport.PeerEndpoint.Port).Distinct());
            // …while each flow keeps its own local relay socket, so reverse routing stays per-flow.
            Assert.Equal(flows, transports.Select(static transport => transport.LocalEndpoint.Port).Distinct().Count());
        }
        finally
        {
            foreach (var transport in transports) await transport.DisposeAsync();
        }

        // Every flow's socket and registration are released; the warm association stays for the next flow.
        Assert.Equal(0, pool.LeasedFlowCount);
        Assert.Equal(1, pool.AssociationCount);
    }

    [Fact]
    public async Task MaintenanceRetiresASharedAssociationThatFaultedWithNoLeaseOutstanding()
    {
        // A shared association whose only lease was already released faults with zero holders: its
        // last release ran while it still looked healthy, so without the sweep it would linger in
        // the live set until pool shutdown and keep AssociationCount overstated. The injected clock
        // never advances, so the retirement cannot be the idle window elapsing.
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        await using var scope = CreateScope(UdpAssociationReuseMode.Always, time);
        var lease = await scope.RentAsync();
        await lease.DisposeAsync();
        Assert.Equal(1, scope.Pool.AssociationCount);
        Assert.Equal(0, scope.Pool.LeasedFlowCount);

        await scope.Server.StopAsync();
        await WaitForAsync(() => lease.IsFaulted);
        Assert.Equal(1, scope.Pool.AssociationCount);

        // The pool's own maintenance child retires it on its tick (5 s), not a manual sweep.
        await WaitForAsync(() => scope.Pool.AssociationCount == 0, timeoutMs: 20_000);
        Assert.Equal(0, scope.Pool.LeasedFlowCount);
    }

    private static ScriptedSocks5UdpServer CreateServer(int portBase = FirstRelayPort) =>
        new(new IPEndPoint(IPAddress.Loopback, portBase), ordinal => new IPEndPoint(IPAddress.Loopback, portBase + ordinal));

    private static PoolScope CreateScope(
        UdpAssociationReuseMode mode,
        TimeProvider? timeProvider = null,
        int maxAssociationsPerServer = UdpAssociationPool.DefaultMaxAssociationsPerServer,
        int flowsPerAssociation = UdpAssociationPool.DefaultFlowsPerAssociation) =>
        new(
            new UdpAssociationPool(
                new SelfTrafficRegistry(),
                mode,
                timeProvider: timeProvider,
                maxAssociationsPerServer: maxAssociationsPerServer,
                flowsPerAssociation: flowsPerAssociation),
            CreateServer());

    /// <summary>
    /// Owns the pool and every lease a test rents from it, so a failed assertion still releases the
    /// holders before the pool's drain (which otherwise waits for them by design). Repeated lease
    /// disposal is a no-op, so a test may still release explicitly where it asserts refcount state.
    /// </summary>
    private sealed class PoolScope(UdpAssociationPool pool, ScriptedSocks5UdpServer server) : IAsyncDisposable
    {
        private readonly List<UdpAssociationLease> _leases = [];

        public UdpAssociationPool Pool { get; } = pool;

        public ScriptedSocks5UdpServer Server { get; } = server;

        public async ValueTask<UdpAssociationLease> RentAsync()
        {
            var lease = await Pool.RentAsync(Server.Server, CancellationToken.None);
            _leases.Add(lease);
            return lease;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var lease in _leases) await lease.DisposeAsync();
            await Pool.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
