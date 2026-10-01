using System.Runtime.Versioning;
using WinForward.Cli;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The composition seam between the validated UDP budget and the coordinator (R4): the two adjacent
/// int members of <see cref="UdpProxyComposition"/> — session capacity and relay receive buffer
/// bytes — must reach the coordinator as distinct values, so a positional transposition (which
/// would silently install a 16 KiB relay buffer) fails here instead of in the relay.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UdpProxyCompositionTests
{
    [Fact]
    public async Task CreateCarriesSessionCapacityAndRelayReceiveBufferAsDistinctValues()
    {
        const int sessionCapacity = 37;
        const int relayReceiveBufferBytes = 48 * 1024;
        using var setupExecutor = new SetupExecutor();
        await using var associations = new UdpAssociationPool(new SelfTrafficRegistry(), UdpAssociationReuseMode.Off);
        var composition = new UdpProxyComposition(
            new UdpAdapterTargetSource(),
            FlowBuilders.Slots,
            UdpFrameBuilder.DefaultMaximumEthernetFrame,
            TestPools.UdpSetupQueuePool,
            TestPools.UdpReceiveWindowPool,
            setupExecutor,
            new Socks5AddressCache(),
            associations,
            SessionCapacity: sessionCapacity,
            RelayReceiveBufferBytes: relayReceiveBufferBytes);

        await using var coordinator = UdpProxyComposer.Create(
            new FakeReinjector(),
            new SelfTrafficRegistry(),
            NullRuntimeLogger.Instance,
            healthSignal: null,
            composition);

        Assert.Equal(sessionCapacity, coordinator.Capacity);
        Assert.Equal(relayReceiveBufferBytes, coordinator.RelayReceiveBufferBytes);
    }

    [Fact]
    public async Task CreateAssociationPoolCarriesBothPlacementBoundsAsDistinctValues()
    {
        // The two UDP placement bounds are adjacent int members of the validated configuration, and
        // a transposition (a ceiling of 31 associations of 7 flows) is a valid pool shape that no
        // later assertion would catch, so the seam is pinned here as the capacity/buffer seam above.
        var configuration = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass))
        {
            UdpAssociationReuse = UdpAssociationReuseMode.Off,
            UdpAssociationMaxPerServer = 31,
            UdpAssociationFlowsPerAssociation = 7,
        };

        await using var pool = UdpProxyComposer.CreateAssociationPool(
            configuration,
            new SelfTrafficRegistry(),
            new Socks5AddressCache(),
            NullRuntimeLogger.Instance);

        Assert.Equal(31, pool.MaxAssociationsPerServerLimit);
        Assert.Equal(7, pool.FlowsPerAssociationLimit);
    }
}
