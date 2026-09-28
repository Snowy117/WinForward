using System.Runtime.Versioning;
using WinForward.Cli;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
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
        await using var associations = new UdpAssociationPool(new SelfTrafficRegistry(), Configuration.UdpAssociationReuseMode.Off);
        var composition = new UdpProxyComposition(
            new UdpAdapterTargetSource(),
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
}
