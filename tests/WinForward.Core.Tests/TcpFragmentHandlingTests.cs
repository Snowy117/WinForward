using System.Buffers.Binary;
using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.TcpRedirect;
using WinForward.Windows;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.FrameBuilders;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// S1: an IP fragment whose address pair belongs to an active TCP redirect association is
/// consumed (never passed toward the real server), tears the association down client-visibly
/// (RST|ACK via the tracked sequences; a warned silent teardown when they were never observed),
/// emits a reason=fragment trace, and arms the grace tombstone. Unattributable fragments keep
/// the unconditional non-flow pass. These tests replaced the pre-fix characterization suite
/// that pinned the old behavior (every fragment passed: host forward leg reinjected toward the
/// real server, reverse leg and forwarded fragments delivered toward MSTCP).
/// </summary>
public sealed class TcpFragmentHandlingTests
{
    private static readonly IPAddress s_clientIpv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destIpv4 = IPAddress.Parse("192.0.2.53");
    private static readonly IPAddress s_clientIpv6 = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress s_destIpv6 = IPAddress.Parse("2001:db8::53");
    private static readonly IPAddress s_forwardLocal = IPAddress.Parse("192.0.2.1");
    private static readonly Socks5Server s_server = new("primary", "127.0.0.1", 1080, null, null);

    [Fact]
    public async Task HostFragmentIsConsumedWithClientResetTraceAndTombstone()
    {
        var harness = FragmentHarness.CreateHost();
        await harness.EstablishRelayingWithSynAckAsync();

        var fragment = MakeNonFlowPacket(BuildIpv4Fragment(s_clientIpv4, s_destIpv4, 53000, 443), isOnSend: true);
        await harness.Dispatcher.DispatchNonFlowAsync(fragment, CancellationToken.None);

        // Consumed, never reinjected toward the real server.
        Assert.Equal(PacketDisposition.ProxyConsumed, fragment.Lease.Disposition);
        Assert.Equal(0, harness.Reinjector.SendToAdapterCount);
        Assert.Equal(0, harness.Reinjector.SendToMstcpCount);

        // The teardown injected one client-visible RST|ACK (seq/ack = 2: ISN+1 on both legs).
        var reset = Assert.Single(harness.Injector.InjectedFrames, frame => frame.Frame[47] == 0x14);
        Assert.True(reset.TowardMstcp);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(reset.Frame.AsSpan(38, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(reset.Frame.AsSpan(42, 4)));

        AssertFragmentTeardownCompleted(harness);
    }

    [Fact]
    public async Task ReverseLegFragmentIsConsumed()
    {
        var harness = FragmentHarness.CreateHost();
        await harness.EstablishRelayingWithSynAckAsync();

        var fragment = MakeNonFlowPacket(BuildIpv4Fragment(s_destIpv4, s_clientIpv4, 443, 53000), isOnSend: false);
        await harness.Dispatcher.DispatchNonFlowAsync(fragment, CancellationToken.None);

        Assert.Equal(PacketDisposition.ProxyConsumed, fragment.Lease.Disposition);
        Assert.Equal(0, harness.Reinjector.SendToMstcpCount);
        AssertFragmentTeardownCompleted(harness);
    }

    [Fact]
    public async Task ForwardedFragmentTeardownResetsTowardOriginAdapter()
    {
        var harness = FragmentHarness.CreateForwarded();
        await harness.EstablishRelayingWithSynAckAsync();

        var fragment = MakeNonFlowPacket(BuildIpv4Fragment(s_clientIpv4, s_destIpv4, 53000, 443), isOnSend: false, adapterId: "veth-1");
        await harness.Dispatcher.DispatchNonFlowAsync(fragment, CancellationToken.None);

        Assert.Equal(PacketDisposition.ProxyConsumed, fragment.Lease.Disposition);
        Assert.Equal(0, harness.Reinjector.SendToMstcpCount);
        var reset = Assert.Single(harness.Injector.InjectedFrames, frame => frame.Frame[47] == 0x14);
        Assert.False(reset.TowardMstcp);
        Assert.Equal((nint)0x1234, reset.AdapterHandle);
        AssertFragmentTeardownCompleted(harness);
    }

    [Fact]
    public async Task NonFirstFragmentWithoutPortsIsConsumedViaAddressPairMatch()
    {
        // A non-first fragment carries no transport header; the address pair is still enough to
        // attribute it and consume it.
        var harness = FragmentHarness.CreateHost();
        await harness.EstablishRelayingWithSynAckAsync();

        var fragment = MakeNonFlowPacket(BuildIpv4NonFirstFragment(s_clientIpv4, s_destIpv4), isOnSend: true);
        await harness.Dispatcher.DispatchNonFlowAsync(fragment, CancellationToken.None);

        Assert.Equal(PacketDisposition.ProxyConsumed, fragment.Lease.Disposition);
        Assert.Equal(0, harness.Reinjector.SendToAdapterCount);
        AssertFragmentTeardownCompleted(harness);
    }

    [Fact]
    public async Task Ipv6FragmentHeaderFrameIsConsumedWithReset()
    {
        var harness = FragmentHarness.CreateHost(addressFamily: AddressFamilyKind.IPv6);
        await harness.EstablishRelayingWithSynAckAsync();

        var fragment = MakeNonFlowPacket(BuildIpv6Fragment(s_clientIpv6, s_destIpv6, 53000, 443), isOnSend: true);
        await harness.Dispatcher.DispatchNonFlowAsync(fragment, CancellationToken.None);

        Assert.Equal(PacketDisposition.ProxyConsumed, fragment.Lease.Disposition);
        Assert.Equal(0, harness.Reinjector.SendToAdapterCount);
        var reset = Assert.Single(harness.Injector.InjectedFrames, frame => frame.Frame.Length == 14 + 40 + 20);
        Assert.Equal(0x14, reset.Frame[67]);
        AssertFragmentTeardownCompleted(harness);
    }

    [Fact]
    public async Task FragmentWithoutAssociationStillPasses()
    {
        // The retained fallback: an unattributable fragment is pre-existing/non-flow traffic and
        // keeps the unconditional pass.
        var harness = FragmentHarness.CreateHost();

        var fragment = MakeNonFlowPacket(BuildIpv4Fragment(s_clientIpv4, s_destIpv4, 53000, 443), isOnSend: true);
        await harness.Dispatcher.DispatchNonFlowAsync(fragment, CancellationToken.None);

        Assert.Equal(PacketDisposition.Pass, fragment.Lease.Disposition);
        Assert.Equal(1, harness.Reinjector.SendToAdapterCount);
        Assert.DoesNotContain(harness.Logger.Events, e => string.Equals(e.Name, "tcp.redirect.fragment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FragmentTeardownWithoutObservedSequencesWarnsAndSkipsReset()
    {
        // No SYN-ACK ever passed the reverse hook, so no in-window reset is possible: the
        // teardown degrades to a warned silent release and still arms the tombstone.
        var harness = FragmentHarness.CreateHost();
        await harness.EstablishRelayingAsync();

        var fragment = MakeNonFlowPacket(BuildIpv4Fragment(s_clientIpv4, s_destIpv4, 53000, 443), isOnSend: true);
        await harness.Dispatcher.DispatchNonFlowAsync(fragment, CancellationToken.None);

        Assert.Equal(PacketDisposition.ProxyConsumed, fragment.Lease.Disposition);
        Assert.DoesNotContain(harness.Injector.InjectedFrames, frame => frame.Frame[47] == 0x14);
        Assert.Contains(harness.Logger.Lines, line => line.Message.Contains("without observed sequences", StringComparison.Ordinal));
        AssertFragmentTeardownCompleted(harness);
    }

    private static void AssertFragmentTeardownCompleted(FragmentHarness harness)
    {
        // The trace names the fragment reason; the association is gone (single tombstone write
        // point), the relay and listener are released, and the flow key hits the grace tombstone.
        Assert.Contains(harness.Logger.Events, e => string.Equals(e.Name, "tcp.redirect.fragment", StringComparison.Ordinal)
            && e.Fields.Any(field => string.Equals(field.Key, "reason", StringComparison.Ordinal) && field.Value is "fragment"));
        Assert.Equal(0, harness.Table.Count);
        Assert.True(harness.RelayFactory.Relay!.IsDisposed);
        Assert.True(harness.ListenerFactory.Listeners[0].IsDisposed);
        Assert.True(harness.Coordinator.Tombstones.TryHit(harness.HostFlowKey, DateTimeOffset.UtcNow));
    }

    internal static byte[] BuildIpv4Fragment(IPAddress source, IPAddress destination, ushort sourcePort, ushort destinationPort)
    {
        var frame = BuildIpv4TcpFrame(source, destination, sourcePort, destinationPort);
        // Flags/fragment-offset field (frame 20..21): MF set makes the frame unparseable as a
        // flow, so the capture path routes it as non-flow. The stale checksum is irrelevant —
        // the fragment path never rewrites the frame.
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(20, 2), 0x2000);
        return frame;
    }

    internal static byte[] BuildIpv4NonFirstFragment(IPAddress source, IPAddress destination)
    {
        // A payload-only continuation fragment: 20-byte IPv4 header + 8 payload bytes, fragment
        // offset 1 (MF clear) — no transport header at all.
        var frame = new byte[14 + 20 + 8];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 28);
        frame[18] = 0x40;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(20, 2), 0x0001);
        frame[23] = 6;
        source.TryWriteBytes(frame.AsSpan(26, 4), out _);
        destination.TryWriteBytes(frame.AsSpan(30, 4), out _);
        return frame;
    }

    internal static byte[] BuildIpv6Fragment(IPAddress source, IPAddress destination, ushort sourcePort, ushort destinationPort)
    {
        // IPv6(40) + fragment header(8) + TCP(20): nextHeader 44, the fragment header carries
        // TCP with a zero offset.
        const int tcpLength = 20;
        var frame = new byte[14 + 40 + 8 + tcpLength];
        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), (ushort)(8 + tcpLength));
        frame[20] = 44;
        source.TryWriteBytes(frame.AsSpan(22, 16), out _);
        destination.TryWriteBytes(frame.AsSpan(38, 16), out _);
        const int fragment = 54;
        frame[fragment] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(fragment + 4, 4), 1);
        const int tcp = 62;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 2, 2), destinationPort);
        frame[tcp + 13] = 0x18;
        return frame;
    }

    internal static CapturedFlowPacket MakeNonFlowPacket(byte[] frame, bool isOnSend, string adapterId = "eth0")
    {
        var adapter = new WindowsAdapter(adapterId, string.Equals(adapterId, "veth-1", StringComparison.Ordinal) ? "vEthernet 1" : "Ethernet", adapterId, 0x1234, 1);
        var context = PacketFlowClassifier.ClassifyNonFlow(adapter, isOnSend);
        return new CapturedFlowPacket(new PacketLease(frame), context, new PacketCaptureMetadata(
            isOnSend ? NdisApiAbi.PacketFlagOnSend : NdisApiAbi.PacketFlagOnReceive, 0x1234));
    }

    /// <summary>A dispatcher wired like the runtime composition, including the fragment handler.</summary>
    internal sealed class FragmentHarness(
        TcpProxyCoordinator coordinator,
        FakeListenerFactory listenerFactory,
        CompletableRelayFactory relayFactory,
        FakeInjector injector,
        CountingReinjector reinjector,
        RecordingRuntimeLogger logger,
        FlowDispatcher dispatcher,
        bool forwarded,
        FlowKey hostFlowKey)
    {
        public FlowDispatcher Dispatcher => dispatcher;
        public CountingReinjector Reinjector => reinjector;
        public FakeInjector Injector => injector;
        public RecordingRuntimeLogger Logger => logger;
        public TcpRedirectTable Table => coordinator.Table;
        public TcpProxyCoordinator Coordinator => coordinator;
        public FakeListenerFactory ListenerFactory => listenerFactory;
        public CompletableRelayFactory RelayFactory => relayFactory;
        public FlowKey HostFlowKey => hostFlowKey;

        public static FragmentHarness CreateHost(AddressFamilyKind addressFamily = AddressFamilyKind.IPv4) => Create(forwarded: false, addressFamily);

        public static FragmentHarness CreateForwarded() => Create(forwarded: true, AddressFamilyKind.IPv4);

        private static FragmentHarness Create(bool forwarded, AddressFamilyKind addressFamily)
        {
            var client = addressFamily == AddressFamilyKind.IPv6 ? s_clientIpv6 : s_clientIpv4;
            var destination = addressFamily == AddressFamilyKind.IPv6 ? s_destIpv6 : s_destIpv4;
            var listenerFactory = new FakeListenerFactory();
            var relayFactory = new CompletableRelayFactory();
            var injector = new FakeInjector();
            var selfTraffic = new SelfTrafficRegistry();
            var table = new TcpRedirectTable();
            var localAddresses = forwarded
                ? new FakeLocalAddressProvider(s_forwardLocal)
                : new FakeLocalAddressProvider();
            var logger = new RecordingRuntimeLogger();
            var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, localAddresses, logger);
            var reinjector = new CountingReinjector();
            var executor = new NdisPacketActionExecutor(reinjector, logger, tcpProxy: coordinator);
            // Forwarded flows only evaluate adapter-qualified rules, so each shape needs its own
            // proxy rule; host flows match the catch-all.
            var matcher = forwarded
                ? new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "veth-1" })
                : new RuleMatcher();
            var rules = new[] { new PolicyRule(matcher, new FlowDecision(FlowAction.Proxy, 0, s_server.Name)) };
            var servers = new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase) { [s_server.Name] = s_server };
            var config = new ValidatedConfiguration(servers, new PolicySnapshot(rules, FlowAction.Block));
            var dispatcher = new FlowDispatcher(config, selfTraffic, executor, reverseHandler: coordinator, fragmentHandler: coordinator.HandleFragmentAsync);
            // The tombstone is keyed by the association's original key, which carries the
            // forwarded origin and its adapter context.
            var adapter = new AdapterContext("veth-1", "vEthernet 1", 7);
            var key = forwarded
                ? FlowKey.Create(Endpoint.From(s_clientIpv4, 53000), Endpoint.From(s_destIpv4, 443), TransportProtocol.Tcp, FlowOriginKind.Forwarded, adapter)
                : FlowKey.Create(Endpoint.From(client, 53000), Endpoint.From(destination, 443), TransportProtocol.Tcp, FlowOriginKind.Host);
            return new FragmentHarness(coordinator, listenerFactory, relayFactory, injector, reinjector, logger, dispatcher, forwarded, key);
        }

        public async Task EstablishRelayingAsync()
        {
            var syn = forwarded
                ? MakeForwardedSynPacket(s_clientIpv4, s_destIpv4, 53000, 443)
                : MakeSynPacket(Client, Destination, 53000, 443);
            await Dispatcher.DispatchAsync(syn, CancellationToken.None);
            var listener = listenerFactory.Listeners[0];
            // Host shape: the accepted peer is the server-address:client-port tuple the IP-swap
            // SYN presented; forwarded shape: the client itself (DNAT keeps its tuple).
            var peer = forwarded ? Endpoint.From(s_clientIpv4, 53000) : Endpoint.From(Destination, 53000);
            await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(peer), CancellationToken.None);
            await WaitForAsync(() => relayFactory.Relay is not null);
        }

        /// <summary>Also walks the reverse SYN-ACK through the dispatcher so the server ISN is
        /// recorded and a client-visible reset becomes possible.</summary>
        public async Task EstablishRelayingWithSynAckAsync()
        {
            await EstablishRelayingAsync();
            var listenerPort = listenerFactory.Listeners[0].TranslatedTuple.Port;
            var reverseSource = forwarded ? s_forwardLocal : Client;
            var reverseDestination = forwarded ? s_clientIpv4 : Destination;
            // The option-less TCP flags byte sits 7 bytes from the frame end for both families
            // (IPv4: 47, IPv6: 67), so the SYN|ACK mutation is family-agnostic.
            var synAck = MakeReversePacketClassifierOrientation(reverseSource, listenerPort, reverseDestination, 53000, mutateFrame: f => f[f.Length - 7] = 0x12);
            await Dispatcher.DispatchAsync(synAck, CancellationToken.None);
            injector.InjectedFrames.Clear();
        }

        private IPAddress Client => forwarded ? s_clientIpv4 : HostFlowKey.Local.Address.ToIPAddress();

        private IPAddress Destination => forwarded ? s_destIpv4 : HostFlowKey.Remote.Address.ToIPAddress();
    }
}
