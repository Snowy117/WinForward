using System.Net;
using System.Runtime.Versioning;
using WinForward.Cli;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;

namespace WinForward.Integration.Tests;

/// <summary>
/// The composition seam between the validated UDP budget and the coordinator (R4): the two adjacent
/// int members of <see cref="UdpProxyComposition"/> — session capacity and relay receive buffer
/// bytes — must reach the coordinator as distinct values, so a positional transposition (which
/// would silently install a 16 KiB relay buffer) fails here instead of in the relay. Every flow's
/// association belongs to its transport, so the composition carries no association owner.
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
        var composition = new UdpProxyComposition(
            new UdpAdapterTargetSource(),
            FlowBuilders.Slots,
            UdpFrameBuilder.DefaultMaximumEthernetFrame,
            TestPools.UdpSetupQueuePool,
            TestPools.UdpReceiveWindowPool,
            setupExecutor,
            new Socks5AddressCache(),
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
    public async Task CreateWiresOneAuthenticatedAssociationPerFlow()
    {
        await using var server = new ScriptedSocks5UdpServer(new IPEndPoint(IPAddress.Loopback, 43_500));
        using var setupExecutor = new SetupExecutor();
        var composition = new UdpProxyComposition(
            new UdpAdapterTargetSource(),
            FlowBuilders.Slots,
            UdpFrameBuilder.DefaultMaximumEthernetFrame,
            TestPools.UdpSetupQueuePool,
            TestPools.UdpReceiveWindowPool,
            setupExecutor,
            new Socks5AddressCache(),
            SessionCapacity: 16,
            RelayReceiveBufferBytes: ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes);

        await using var coordinator = UdpProxyComposer.Create(
            new FakeReinjector(),
            new SelfTrafficRegistry(),
            NullRuntimeLogger.Instance,
            healthSignal: null,
            composition);
        var target = ProxyTarget.FromServer(server.Server);
        var payload = new byte[] { 1, 2, 3 };

        Assert.True(await coordinator.TrySendSpanAsync(Flow(53_001), target, payload, default, CancellationToken.None));
        Assert.True(await coordinator.TrySendSpanAsync(Flow(53_002), target, payload, default, CancellationToken.None));

        // The composition's SOCKS5 transport factory serves every flow with its own authenticated
        // association: two flows, two control connections, two UDP ASSOCIATE exchanges.
        await WaitForAsync(() => server.AssociateReplyCount == 2);
        Assert.Equal(2, server.ConnectionCount);
    }

    private static FlowKey Flow(ushort localPort) => FlowKey.Create(
        Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(IPAddress.Parse("192.0.2.53"), 53),
        TransportProtocol.Udp,
        FlowOriginKind.Host);
}
