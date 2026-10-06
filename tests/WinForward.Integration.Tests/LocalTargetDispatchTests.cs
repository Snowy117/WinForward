using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using WinForward.Cli;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using WinForward.Windows;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FrameBuilders;

namespace WinForward.Integration.Tests;

/// <summary>
/// End to end through the production layers: a real configuration document selects a local target
/// for UDP/53, and the flow's datagram travels configuration → flow table → dispatcher → executor →
/// coordinator → local transport → a loopback endpoint, whose reply is reinjected toward the client
/// with the flow's original destination as its source. The declared SOCKS5 server is a live,
/// counting server that must see nothing: no control connection, no UDP ASSOCIATE, no association in
/// the pool.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalTargetDispatchTests
{
    private static readonly IPAddress s_client = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_dnsServer = IPAddress.Parse("192.0.2.53");
    private static readonly byte[] s_hostMac = [0x02, 0x00, 0x00, 0x00, 0x00, 0x01];
    private const nint AdapterHandle = 0x2345;

    [Fact]
    public async Task ALocalTargetUdp53FlowIsAnsweredWithoutAnySocks5Association()
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var responder = new LoopbackUdpResponder(static payload => [payload.Span[0], 0x7f]);
        await using var socksServer = new ScriptedSocks5UdpServer(new IPEndPoint(IPAddress.Loopback, 0));
        var configuration = Load(responder.Endpoint, socksServer.ControlEndpoint);
        var logger = new RecordingLogger();
        var selfTraffic = new SelfTrafficRegistry();
        var slots = FlowBuilders.Slots;
        var adapter = new WindowsAdapter("id-a", "Ethernet", "internal-a", AdapterHandle, 1);
        var slot = FlowBuilders.SlotOf(adapter.StableId, adapter.Generation);
        var targets = new UdpAdapterTargetSource(slots, new UdpAdapterTarget(AdapterHandle, s_hostMac), new Dictionary<ushort, UdpAdapterTarget> { [slot] = new(AdapterHandle, s_hostMac) });
        using var setupExecutor = new SetupExecutor();
        var reinjector = new FakeReinjector();
        var composition = new UdpProxyComposition(
            targets,
            slots,
            UdpFrameBuilder.DefaultMaximumEthernetFrame,
            TestPools.UdpSetupQueuePool,
            TestPools.UdpReceiveWindowPool,
            setupExecutor,
            new Socks5AddressCache(),
            SessionCapacity: configuration.UdpSessionCapacity,
            RelayReceiveBufferBytes: configuration.UdpRelayReceiveBufferBytes);
        await using var coordinator = UdpProxyComposer.Create(reinjector, selfTraffic, logger, healthSignal: null, composition);
        var executor = new NdisPacketActionExecutor(reinjector, logger, udpProxy: coordinator);
        var dispatcher = new FlowDispatcher(configuration, selfTraffic, executor, logger: logger, setupExecutor: setupExecutor);
        var processor = new CapturePacketProcessor(dispatcher, slots);
        var query = "dns-query"u8.ToArray();
        var localTargetFlowsBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFlows);

        using var buffer = new NdisPacketBuffer();
        buffer.SetFrame(BuildIpv4UdpFrame(s_client, s_dnsServer, 53_000, 53, query), NdisApiAbi.PacketFlagOnSend, AdapterHandle);
        await processor.ProcessAsync(NdisCapturedPacket.FromCapture(buffer, AdapterHandle), adapter, slot, shutdown.Token);

        await WaitForAsync(() => reinjector.ToMstcpCount == 1);

        // The client's own datagram reached the local endpoint verbatim, and the endpoint answered.
        await WaitForAsync(() => responder.ReceivedCount == 1);
        Assert.Equal(query, responder.LastPayload);

        // The reply is a rebuilt frame whose source is the flow's original destination and whose
        // payload is the endpoint's answer: the client sees the answer as coming from the resolver it
        // addressed, and the local endpoint stays invisible.
        var frame = reinjector.LastFrame;
        Assert.NotNull(frame);
        Assert.True(IPUdpPacket.TryParseSpan(frame, out var response));
        Assert.Equal((IPAddressValue?)s_dnsServer, response.SourceAddress);
        Assert.Equal((ushort)53, response.SourcePort);
        Assert.Equal((IPAddressValue?)s_client, response.DestinationAddress);
        Assert.Equal((ushort)53_000, response.DestinationPort);
        Assert.Equal(new byte[] { query[0], 0x7f }, response.Payload(frame).ToArray());
        Assert.Equal(0, reinjector.ToAdapterCount);

        // No SOCKS5 control connection and no UDP ASSOCIATE happened: the declared server never saw
        // this flow, and no association was dialed for it.
        Assert.Equal(0, socksServer.ConnectionCount);
        Assert.Equal(0, socksServer.AssociateReplyCount);
        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpLocalTargetFlows) - localTargetFlowsBefore);

        // The trace decides the target kind from the same decision the packet path used.
        var created = Assert.Single(logger.Events, recorded => string.Equals(recorded.Name, "udp.session.created", StringComparison.Ordinal));
        Assert.Equal("dns-in", Field(created, "Target"));
        Assert.Equal("local", Field(created, "TargetKind"));
    }

    [Fact]
    public void ATcpRuleCannotBePointedAtALocalTargetThroughTheSameDocumentShape()
    {
        // The dispatch suite's half of the rule guard: the document shape that serves UDP above is
        // refused outright when its rule could match TCP, so no TCP flow reaches a dispatcher with a
        // local target in hand.
        const string json = """
        {
          "Socks5Servers": [],
          "LocalTargets": [{ "Name": "dns-in", "Host": "127.0.0.1", "Port": 5353 }],
          "Host": {
            "FallbackAction": "pass",
            "Rules": [{ "Protocol": ["tcp"], "RemotePort": ["53"], "Action": "proxy", "Target": "dns-in" }]
          }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "WinForward.Host.Rules[0].Target", StringComparison.Ordinal));
    }

    private static ValidatedConfiguration Load(Endpoint localEndpoint, IPEndPoint controlEndpoint)
    {
        // The document is the shipped shape: the values are interpolated with an invariant culture so
        // the numeric fields are formatted identically on every host locale.
        var json = string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "Socks5Servers": [{ "Name": "remote", "Host": "127.0.0.1", "Port": {{controlEndpoint.Port}} }],
          "LocalTargets": [{ "Name": "dns-in", "Host": "{{localEndpoint.Address}}", "Port": {{localEndpoint.Port}} }],
          "Host": {
            "FallbackAction": "pass",
            "Rules": [{ "Protocol": ["udp"], "RemotePort": ["53"], "Action": "proxy", "Target": "dns-in" }]
          }
        }
        """);

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out var parseDiagnostics), string.Join("; ", parseDiagnostics));
        Assert.NotNull(dto);
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.NotNull(configuration);
        Assert.Equal("dns-in", Assert.Single(configuration.Policy.HostRules).Decision.TargetName);
        return configuration;
    }

    private static string? Field(RecordedEvent recorded, string key)
    {
        foreach (var field in recorded.Fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal)) return Convert.ToString(field.Value, CultureInfo.InvariantCulture);
        }

        return null;
    }
}
