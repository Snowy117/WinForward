using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// S6b/S6c warn diagnostics of the packet action executor: the per-datagram UDP failure warn is
/// rate-limited (5 s window), and every blocked proxy flow reports an accurate reason — the
/// "not initialized in this build" sentence stays reserved for the genuinely uninitialized case.
/// </summary>
public sealed class NdisPacketActionExecutorLoggingTests
{
    private static readonly Socks5Server s_server = new("p", "127.0.0.1", 1080, null, null);
    private static readonly IPAddress s_client = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destination = IPAddress.Parse("192.0.2.53");

    [Fact]
    public async Task UninitializedTcpProxyWarnKeepsNotInitializedText()
    {
        var logger = new RecordingRuntimeLogger();
        var executor = new NdisPacketActionExecutor(new FakeReinjector(), logger);

        await executor.ProxyAsync(TcpPacket(), s_server, CancellationToken.None);

        var warn = Assert.Single(logger.Lines, line => line.Level == RuntimeLogLevel.Warn);
        Assert.Contains("not initialized in this build", warn.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoordinatorBlockedWarnCarriesRedirectReasonInsteadOfUninitializedText()
    {
        var logger = new RecordingRuntimeLogger();
        await using var coordinator = new TcpProxyCoordinator(
            new ThrowingRedirectListenerFactory(),
            new ThrowingRelayFactory(),
            new ThrowingRedirectInjector(),
            new TcpRedirectTable(),
            new SelfTrafficRegistry(),
            new FakeLocalAddressProvider());
        var executor = new NdisPacketActionExecutor(new FakeReinjector(), logger, tcpProxy: coordinator);

        // The listener-allocation failure makes the coordinator return Blocked without ever
        // touching the (throwing) relay or injector.
        await executor.ProxyAsync(MakeSynPacket(s_client, s_destination, 53000, 443), s_server, CancellationToken.None);

        var warn = Assert.Single(logger.Lines, line => line.Level == RuntimeLogLevel.Warn && line.Message.Contains("reason=redirect", StringComparison.Ordinal));
        Assert.DoesNotContain("not initialized", warn.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UdpParseFailureWarnCarriesParseReason()
    {
        var logger = new RecordingRuntimeLogger();
        var factory = new FakeTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var executor = new NdisPacketActionExecutor(new FakeReinjector(), logger, udpProxy: coordinator);

        // A TCP frame on a UDP-decided flow cannot be parsed as a UDP datagram.
        var packet = new CapturedFlowPacket(
            new PacketLease(FrameBuilders.CreateIpv4TcpFrame()),
            FlowContext(FlowKey.Create(Endpoint.From(s_client, 53000), Endpoint.From(s_destination, 53), TransportProtocol.Udp, FlowOriginKind.Host)),
            new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 7));

        await executor.ProxyAsync(packet, s_server, CancellationToken.None);

        Assert.Contains(logger.Lines, line => line.Level == RuntimeLogLevel.Warn && line.Message.Contains("reason=parse", StringComparison.Ordinal));
        Assert.Empty(factory.Transports);
    }

    [Fact]
    public async Task UdpHandlingFailureWarnIsRateLimitedPerWindow()
    {
        var logger = new RecordingRuntimeLogger();
        var coordinator = new UdpProxyCoordinator(new FakeTransportFactory(), new FakeResponseSink());
        await coordinator.DisposeAsync();
        var executor = new NdisPacketActionExecutor(new FakeReinjector(), logger, udpProxy: coordinator);

        // A disposed coordinator fails every send with ObjectDisposedException; all three land
        // inside one 5 s window, so exactly one warn may escape.
        for (var i = 0; i < 3; i++)
        {
            await executor.ProxyAsync(UdpPacket(), s_server, CancellationToken.None);
        }

        Assert.Equal(1, logger.Lines.Count(line => line.Level == RuntimeLogLevel.Warn && line.Message.Contains("UDP proxy handling failed", StringComparison.Ordinal)));
    }

    private static CapturedFlowPacket TcpPacket() =>
        new(
            new PacketLease(new byte[] { 1 }),
            FlowContext(FlowKey.Create(Endpoint.From(s_client, 53000), Endpoint.From(s_destination, 443), TransportProtocol.Tcp, FlowOriginKind.Host)),
            new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 7));

    private static CapturedFlowPacket UdpPacket() =>
        new(
            new PacketLease(FrameBuilders.CreateIpv4UdpFrame()),
            FlowContext(FlowKey.Create(Endpoint.From(s_client, 53000), Endpoint.From(s_destination, 53), TransportProtocol.Udp, FlowOriginKind.Host)),
            new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 7));

    private static FlowContext FlowContext(FlowKey key) => new(key, null, null, null, null, key.Remote.Port);
}
