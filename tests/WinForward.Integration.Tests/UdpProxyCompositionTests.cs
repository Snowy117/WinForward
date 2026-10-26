using System.Net;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using WinForward.Cli;
using WinForward.Cli.Backends.Kernel;
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
/// The composition seam between the validated UDP budget and the coordinator: the two adjacent
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
            NullLogger.Instance,
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
            NullLogger.Instance,
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

    [Fact]
    public async Task TheHostOverloadComposesTheSameEngineOverAHostSinkAndTheHostFrameCap()
    {
        // The host path composes the shared engine directly from a sink instead of the packet
        // reinjector. It must carry the same capacity/relay-buffer budget and build the coordinator
        // over the host datagram cap, not the packet path's Ethernet frame.
        using var setupQueuePool = new NativeBufferPool(KernelHostUdpDatagram.MaximumDatagramBytes);
        using var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(KernelHostUdpDatagram.MaximumDatagramBytes));
        using var setupExecutor = new SetupExecutor();
        const int sessionCapacity = 11;
        const int relayReceiveBufferBytes = 32 * 1024;

        await using var coordinator = UdpProxyComposer.Create(
            new FakeResponseSink(),
            new SelfTrafficRegistry(),
            NullLogger.Instance,
            KernelHostUdpDatagram.MaximumDatagramBytes,
            setupQueuePool,
            receiveWindowPool,
            setupExecutor,
            new Socks5AddressCache(),
            sessionCapacity,
            relayReceiveBufferBytes,
            activityClock: null);

        Assert.Equal(sessionCapacity, coordinator.Capacity);
        Assert.Equal(relayReceiveBufferBytes, coordinator.RelayReceiveBufferBytes);
    }

    private static FlowKey Flow(ushort localPort) => FlowKey.Create(
        Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(IPAddress.Parse("192.0.2.53"), 53),
        TransportProtocol.Udp,
        FlowOriginKind.Host);
}
