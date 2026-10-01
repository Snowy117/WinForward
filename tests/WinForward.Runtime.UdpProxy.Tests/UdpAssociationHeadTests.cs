using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.UdpAssociationFakes;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The shared head — the pool's per-server association ceiling multiplied by its per-association
/// flow bound — and the two validated keys behind it (<c>udpAssociationMaxPerServer</c>,
/// <c>udpAssociationFlowsPerAssociation</c>). The defaults multiply to the default
/// <c>udpSessionCapacity</c>, so a population the coordinator admits can be shared instead of
/// falling back to a control connection per flow; the configured values drive placement, and the
/// configured flow bound is simultaneously the capability sampler's live-evidence set.
/// </summary>
public sealed class UdpAssociationHeadTests
{
    /// <summary>The live session population the PRD acceptance load reaches (100 new flows/s against the retention window).</summary>
    private const int AcceptanceLoadFlows = 4_500;

    private const int FirstRelayPort = 42_000;

    private const int PayloadLength = 16;

    [Fact]
    public void TheDefaultHeadCoversTheDefaultSessionCapacity()
    {
        // The decision this pins: 1,024 associations x 16 flows = 16,384 flows per server, exactly
        // the default UDP session capacity — every flow the coordinator admits can be shared.
        Assert.Equal(16_384, UdpAssociationPool.DefaultMaxAssociationsPerServer * UdpAssociationPool.DefaultFlowsPerAssociation);
        Assert.True(
            UdpAssociationPool.DefaultMaxAssociationsPerServer * UdpAssociationPool.DefaultFlowsPerAssociation >= ConfigurationLoader.DefaultUdpSessionCapacity,
            "the default shared head must cover the default session capacity, or flows fall back to private associations");
    }

    [Theory]
    [InlineData(UdpAssociationPool.DefaultMaxAssociationsPerServer, UdpAssociationPool.DefaultFlowsPerAssociation, true)]
    [InlineData(2_048, 32, true)]
    [InlineData(16, 16, false)]
    public void TheSharedHeadCoversTheSessionCapacityOnlyWhenTheProductIsLargeEnough(int maxAssociationsPerServer, int flowsPerAssociation, bool covers)
    {
        // 16 x 16 = 256 is the head the default used to be: too small to cover the population, which
        // is why most flows at the acceptance load opened a private control connection. Raising the
        // ceiling buys the head back; the per-association bound stays small on purpose, because it
        // is the blast radius of an association death and the sampler's evidence set.
        var head = maxAssociationsPerServer * flowsPerAssociation;
        Assert.Equal(covers, head >= ConfigurationLoader.DefaultUdpSessionCapacity);
    }

    [Fact]
    public async Task TheConfiguredBoundsReachPlacementDistinctly()
    {
        // Transposition guard for the two placement bounds, mirroring UdpProxyCompositionTests:
        // swapping the constructor arguments would silently install a ceiling of three associations
        // of seven flows, which places three associations here instead of seven.
        await using var server = CreateServer();
        await using var pool = new UdpAssociationPool(
            new SelfTrafficRegistry(),
            UdpAssociationReuseMode.Always,
            maxAssociationsPerServer: 7,
            flowsPerAssociation: 3);
        var leases = new List<UdpAssociationLease>();
        try
        {
            Assert.Equal(7, pool.MaxAssociationsPerServerLimit);
            Assert.Equal(3, pool.FlowsPerAssociationLimit);

            // Seven associations of three flows: 21 flows fill the configured ceiling exactly.
            for (var index = 0; index < 7 * 3; index++) leases.Add(await pool.RentAsync(server.Server, CancellationToken.None));
            Assert.Equal(7, pool.AssociationCount);

            // The twenty-second flow finds every shared association full and the ceiling reached.
            var overCeiling = await pool.RentAsync(server.Server, CancellationToken.None);
            leases.Add(overCeiling);
            Assert.Equal(8, pool.AssociationCount);
        }
        finally
        {
            foreach (var lease in leases) await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheDefaultHeadKeepsTheAcceptanceLoadShared()
    {
        // The PRD acceptance steady state (100 new flows/s against the retention window) is ~4,500
        // live sessions. With the default head every one of them is shared: ceil(4,500 / 16) = 282
        // associations and not one more — a single private association would show up as a 283rd —
        // so the descriptor shape is one relay socket plus one control connection per 16 sessions.
        await using var server = CreateServer();
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto);
        var leases = new List<UdpAssociationLease>();
        try
        {
            for (var index = 0; index < AcceptanceLoadFlows; index++)
            {
                leases.Add(await pool.RentAsync(server.Server, CancellationToken.None));
            }

            const int expectedSharedAssociations = (AcceptanceLoadFlows + UdpAssociationPool.DefaultFlowsPerAssociation - 1) / UdpAssociationPool.DefaultFlowsPerAssociation;
            Assert.Equal(282, expectedSharedAssociations);
            Assert.Equal(282, pool.AssociationCount);
            Assert.Equal(AcceptanceLoadFlows, pool.LeasedFlowCount);
            // One authenticated control connection and one ASSOCIATE per shared association; the
            // pre-change head (16 x 16) would have capped sharing at 256 flows and opened ~4,244
            // private associations, i.e. one control connection per flow.
            Assert.Equal(282, server.ConnectionCount);
            Assert.Equal(282, server.AssociateReplyCount);
            Assert.Equal(282, leases.Select(static lease => lease.RelayEndpoint.Port).Distinct().Count());
        }
        finally
        {
            foreach (var lease in leases) await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task AConfiguredBoundCapsTheFlowsOneAssociationServesAndWhatTheSamplerObserves()
    {
        // The configured flow bound is also the sampler's live-evidence set: with three flows per
        // association the fourth flow opens a second shared association, so an answered flow outside
        // the bounded set cannot turn three unanswered siblings into a pinning verdict. Reading a
        // fixed bound instead would put all four flows on one association and flip this server.
        await using var relay = new PinningRelay();
        await using var server = new ScriptedSocks5UdpServer(relay.Endpoint);
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto, flowsPerAssociation: 3);
        var registry = new SelfTrafficRegistry();
        await using var first = CreateTransport(pool, registry, server);
        await using var second = CreateTransport(pool, registry, server);
        await using var third = CreateTransport(pool, registry, server);
        Assert.Equal(1, pool.AssociationCount);
        await using var answered = CreateTransport(pool, registry, server);

        // The fourth flow did not overfill the bounded association; it opened a second one — two
        // associations for four flows, and two authenticated ASSOCIATE exchanges on the server.
        Assert.Equal(2, pool.AssociationCount);
        Assert.Equal(4, pool.LeasedFlowCount);
        Assert.Equal(2, server.AssociateReplyCount);

        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];
        await answered.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(1, await relay.PumpAsync(1));
        Assert.True(await ReceiveDatagramAsync(answered.Transport));
        for (var index = 0; index < UdpAssociationCapabilitySampler.PinningSuspicionThreshold; index++)
        {
            await first.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
            await second.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
            await third.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        }

        // The three unanswered flows really did reach the relay: their three client ports join the
        // answered flow's, so the sample below reads evidence rather than missing sends.
        Assert.Equal(4, await relay.PumpAsync(3 * UdpAssociationCapabilitySampler.PinningSuspicionThreshold));

        // No verdict: the sampled association holds exactly the three unanswered flows.
        Assert.Equal(0, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.Unknown, pool.CapabilityOf(server.Server));
    }

    [Fact]
    public void TheDefaultAssociationHeadCoversTheDefaultCapacitySilently()
    {
        // 1,024 x 16 = 16,384, exactly the default udpSessionCapacity: the warning has nothing to
        // report, so omitted keys validate without one.
        var configuration = Validate("");
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionCapacity, (long)configuration.UdpAssociationMaxPerServer * configuration.UdpAssociationFlowsPerAssociation);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void AnAssociationHeadBelowTheAdmittedPopulationWarnsWithoutBlocking()
    {
        // 8 x 4 = 32 flows per server against the default 16,384-flow capacity: the flows beyond the
        // head are not refused — each is served from a private association and holds its own control
        // connection, which is the shape no row names unless validation warns about it.
        var configuration = Validate("""
          "udpAssociationMaxPerServer": 8,
          "udpAssociationFlowsPerAssociation": 4
        """);
        Assert.Equal(32, configuration.UdpAssociationMaxPerServer * configuration.UdpAssociationFlowsPerAssociation);
        var warning = Assert.Single(configuration.Warnings);
        Assert.Equal("udpAssociationMaxPerServer", warning.Path);
        Assert.Contains("32 flows per server", warning.Message, StringComparison.Ordinal);
        Assert.Contains("each hold their own control connection", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RaisingTheCapacityAloneTripsTheHeadWarningOnceItPassesTheHead()
    {
        // The caps stay at 8 x 4 = 32; the validated capacity alone decides whether the head covers
        // the admitted population.
        var covered = Validate("""
          "udpAssociationMaxPerServer": 8,
          "udpAssociationFlowsPerAssociation": 4,
          "udpSessionCapacity": 32
        """);
        Assert.Empty(covered.Warnings);

        var uncovered = Validate("""
          "udpAssociationMaxPerServer": 8,
          "udpAssociationFlowsPerAssociation": 4,
          "udpSessionCapacity": 4096
        """);
        var warning = Assert.Single(uncovered.Warnings);
        Assert.Equal("udpAssociationMaxPerServer", warning.Path);
        Assert.Contains("below the 4096-flow UDP session capacity", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>The minimal valid configuration with <paramref name="body"/> appended, validated and asserted to have no errors.</summary>
    private static ValidatedConfiguration Validate(string body)
    {
        var json = string.IsNullOrEmpty(body)
            ? """
            {
              "socks5Servers": [],
              "rules": [],
              "fallbackAction": "pass"
            }
            """
            : $$"""
            {
              "socks5Servers": [],
              "rules": [],
              "fallbackAction": "pass",
              {{body}}
            }
            """;
        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out var parseErrors), string.Join("; ", parseErrors));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        return configuration!;
    }

    private static ScriptedSocks5UdpServer CreateServer() =>
        new(new IPEndPoint(IPAddress.Loopback, FirstRelayPort), ordinal => new IPEndPoint(IPAddress.Loopback, FirstRelayPort + ordinal));
}
