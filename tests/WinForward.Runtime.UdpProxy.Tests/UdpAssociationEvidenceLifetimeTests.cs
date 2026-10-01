using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.FlowBuilders;
using static WinForward.TestSupport.UdpAssociationFakes;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// Capability evidence belongs to the leases attached when the sampler runs (design §5): a shared
/// association that has served many leases must neither lose a live flow's evidence nor sample a
/// released flow's. The first three tests drive one association through more attach/release cycles
/// than <see cref="UdpAssociationPool.DefaultFlowsPerAssociation"/> before the sampled pair attaches; the
/// last one exercises the flip through the real session/admission stack.
/// </summary>
public sealed class UdpAssociationEvidenceLifetimeTests
{
    private const int PayloadLength = 16;
    private static readonly Endpoint s_destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);

    [Fact]
    public async Task APinningServerIsStillDetectedAfterMoreLeasesThanTheAssociationCanHoldAtOnce()
    {
        await using var relay = new PinningRelay();
        await using var server = new ScriptedSocks5UdpServer(relay.Endpoint);
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto);
        var registry = new SelfTrafficRegistry();
        await RecycleLeasesAsync(pool, server);
        await using var answering = CreateTransport(pool, registry, server);
        await using var pinned = CreateTransport(pool, registry, server);
        var payload = new byte[PayloadLength];

        // The answering flow reaches the relay first and pins its client port; the second flow's
        // datagrams arrive from another port and are never answered.
        await answering.Transport.SendSpanAsync(s_destination, payload, CancellationToken.None);
        Assert.Equal(1, await relay.PumpAsync(1));
        Assert.True(await ReceiveDatagramAsync(answering.Transport));
        for (var index = 0; index < UdpAssociationCapabilitySampler.PinningSuspicionThreshold; index++)
        {
            await pinned.Transport.SendSpanAsync(s_destination, payload, CancellationToken.None);
        }

        // The recycled leases left no evidence behind, so a sample that read their records instead of
        // the attached pair would find no pinning at all and never flip.
        Assert.Equal(2, await relay.PumpAsync(UdpAssociationCapabilitySampler.PinningSuspicionThreshold));
        Assert.Equal(1, pool.AssociationCount);
        Assert.Equal(1, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.PerFlowOnly, pool.CapabilityOf(server.Server));
    }

    [Fact]
    public async Task ASharingServerIsStillConfirmedAfterMoreLeasesThanTheAssociationCanHoldAtOnce()
    {
        await using var relay = new EchoRelay();
        await using var server = new ScriptedSocks5UdpServer(relay.Endpoint);
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto);
        var registry = new SelfTrafficRegistry();
        await RecycleLeasesAsync(pool, server);
        await using var first = CreateTransport(pool, registry, server);
        await using var second = CreateTransport(pool, registry, server);
        var payload = new byte[PayloadLength];

        await first.Transport.SendSpanAsync(s_destination, payload, CancellationToken.None);
        Assert.True(await ReceiveDatagramAsync(first.Transport));
        await second.Transport.SendSpanAsync(s_destination, payload, CancellationToken.None);
        Assert.True(await ReceiveDatagramAsync(second.Transport));

        // Both attached flows are answered: confirming sharing must not be lost behind the recycled
        // leases' records (which would leave the server on trial forever, costing the sharing win).
        Assert.Equal(1, pool.AssociationCount);
        Assert.Equal(0, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.SharedOk, pool.CapabilityOf(server.Server));
    }

    [Fact]
    public async Task ALongLivedLeasesEvidenceSurvivesShortAttachReleaseCycles()
    {
        await using var relay = new PinningRelay();
        await using var server = new ScriptedSocks5UdpServer(relay.Endpoint);
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto);
        var registry = new SelfTrafficRegistry();
        await using var longLived = CreateTransport(pool, registry, server);
        var payload = new byte[PayloadLength];

        // The long-lived flow pins the relay's client port before any other flow exists.
        await longLived.Transport.SendSpanAsync(s_destination, payload, CancellationToken.None);
        Assert.Equal(1, await relay.PumpAsync(1));
        Assert.True(await ReceiveDatagramAsync(longLived.Transport));

        // More leases than the association can hold come and go underneath it.
        await RecycleLeasesAsync(pool, server);
        await using var pinned = CreateTransport(pool, registry, server);
        for (var index = 0; index < UdpAssociationCapabilitySampler.PinningSuspicionThreshold; index++)
        {
            await pinned.Transport.SendSpanAsync(s_destination, payload, CancellationToken.None);
        }

        // The long-lived flow's response is still part of the sample: without it the silent flow has
        // no answered sibling and no verdict can be reached.
        Assert.Equal(2, await relay.PumpAsync(UdpAssociationCapabilitySampler.PinningSuspicionThreshold));
        Assert.Equal(1, pool.AssociationCount);
        Assert.Equal(2, pool.LeasedFlowCount);
        Assert.Equal(1, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.PerFlowOnly, pool.CapabilityOf(server.Server));
    }

    [Fact]
    public async Task ACoordinatorFlipKeepsItsSessionsAndPlacesNewFlowsOnPrivateAssociations()
    {
        await using var pinnedRelay = new PinningRelay();
        await using var privateRelay = new EchoRelay();
        // The advertised endpoints are immutable snapshots, so the factory delegate captures no
        // disposable relay.
        var pinnedEndpoint = pinnedRelay.Endpoint;
        var privateEndpoint = privateRelay.Endpoint;
        await using var server = new ScriptedSocks5UdpServer(
            pinnedEndpoint,
            ordinal => ordinal == 0 ? pinnedEndpoint : privateEndpoint);
        var registry = new SelfTrafficRegistry();
        await using var pool = new UdpAssociationPool(registry, UdpAssociationReuseMode.Auto);
        var responses = new FakeResponseSink();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            new Socks5UdpTransportFactory(pool, registry, UdpFrameBuilder.DefaultMaximumEthernetFrame),
            responses,
            new UdpProxyOptions { Capacity = 16 });
        var answered = CreateFlow("192.0.2.53");
        var silent = CreateFlow("192.0.2.54");
        var payload = new byte[PayloadLength];
        var serverKey = server.Server;

        // The answered flow pins the shared association's client port; the flow that joins it is
        // multiplexed onto the same relay from its own port and stays unanswered.
        Assert.True(await coordinator.TrySendSpanAsync(answered, serverKey, payload, default, CancellationToken.None));
        Assert.Equal(1, await pinnedRelay.PumpAsync(1));
        Assert.Equal(answered, await ReceiveResponseAsync(responses));
        Assert.True(await coordinator.TrySendSpanAsync(silent, serverKey, payload, default, CancellationToken.None));
        for (var index = 1; index < UdpAssociationCapabilitySampler.PinningSuspicionThreshold; index++)
        {
            Assert.True(await coordinator.TrySendSpanAsync(silent, serverKey, payload, default, CancellationToken.None));
        }

        Assert.Equal(2, await pinnedRelay.PumpAsync(UdpAssociationCapabilitySampler.PinningSuspicionThreshold));
        Assert.Equal(2, coordinator.SessionCount);
        Assert.Equal(2, pool.LeasedFlowCount);
        Assert.Equal(1, pool.AssociationCount);

        // The flip changes placement only: no session is retired, no cooldown is armed, and the
        // answered flow keeps sending and receiving on the association it already holds.
        Assert.Equal(1, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.PerFlowOnly, pool.CapabilityOf(serverKey));
        Assert.Equal(2, coordinator.SessionCount);
        Assert.Equal(UdpSessionState.Active, coordinator.SessionState(answered));
        Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);
        Assert.True(await coordinator.TrySendSpanAsync(answered, serverKey, payload, default, CancellationToken.None));
        // The relay's count is cumulative and still two: the answered flow sends from the pinned port.
        Assert.Equal(2, await pinnedRelay.PumpAsync(1));
        Assert.Equal(answered, await ReceiveResponseAsync(responses));
        Assert.Equal(2, coordinator.SessionCount);
        Assert.Equal(1, pool.AssociationCount);

        // A flow placed after the flip gets its own association: a second ASSOCIATE, its own relay,
        // and a datagram that is delivered.
        var placed = CreateFlow("192.0.2.55");
        Assert.True(await coordinator.TrySendSpanAsync(placed, serverKey, payload, default, CancellationToken.None));
        Assert.Equal(placed, await ReceiveResponseAsync(responses));
        Assert.Equal(3, coordinator.SessionCount);
        Assert.Equal(3, pool.LeasedFlowCount);
        Assert.Equal(2, pool.AssociationCount);
        Assert.Equal(2, server.AssociateReplyCount);
    }

    [Fact]
    public async Task AReleasedLeasesEvidenceLeavesTheLiveSet()
    {
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto);
        var association = new UdpControlAssociation(
            pool,
            new Socks5Server("scripted", "127.0.0.1", 1080, Username: null, Password: null),
            isPrivate: false);
        var released = new UdpAssociationEvidence();
        var attached = new UdpAssociationEvidence();
        association.StartLease(released);
        association.StartLease(attached);
        Assert.Equal(2, association.SnapshotEvidence().Attached);

        // A release takes its record out of the sample, so no later sampling tick can read a lease
        // that is gone — the ring's reused slots are what produced the check's mis-reads.
        await association.ReleaseLeaseAsync(released);
        var (evidence, stillAttached) = association.SnapshotEvidence();
        Assert.Equal(1, stillAttached);
        Assert.Same(attached, Assert.Single(evidence));

        await association.ReleaseLeaseAsync(attached);
        Assert.Equal(0, association.SnapshotEvidence().Attached);
        await association.DisposeAsync();
    }

    /// <summary>
    /// Attaches and releases <see cref="UdpAssociationPool.DefaultFlowsPerAssociation"/> + 1 leases on the
    /// server's warm shared association, so the pair that attaches next sits on an association that
    /// has already served more leases than it can hold at once.
    /// </summary>
    private static async Task RecycleLeasesAsync(UdpAssociationPool pool, ScriptedSocks5UdpServer server)
    {
        for (var cycle = 0; cycle <= UdpAssociationPool.DefaultFlowsPerAssociation; cycle++)
        {
            var lease = await pool.RentAsync(server.Server, CancellationToken.None);
            await lease.DisposeAsync();
        }
    }

    /// <summary>Reads one injected response, failing rather than hanging when none arrives.</summary>
    private static async Task<FlowKey> ReceiveResponseAsync(FakeResponseSink sink)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (flow, _, _, _) = await sink.Responses.Reader.ReadAsync(budget.Token);
        return flow;
    }
}
