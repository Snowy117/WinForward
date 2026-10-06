using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FlowBuilders;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The observability promise end to end: the <c>udp.session.created</c> event the real setup
/// pipeline emits over the real transport factory names the UDP carriage the resolved target
/// selects — <c>uot</c> for a SOCKS5 server with <c>udpOverTcp</c>, <c>native</c> for every other
/// SOCKS5 server — beside an unchanged <c>targetKind=socks5</c>. The batch-1 unit test pins the
/// field's formatting in isolation; these facts pin that the field survives the target → factory →
/// transport → setup pipeline path that produces it in production.
/// </summary>
public sealed class UdpSessionTransportObservabilityTests
{
    [Fact]
    public async Task AUotTargetsSessionCreatedEventNamesTheUotCarriageEndToEnd()
    {
        var logger = new RecordingLogger();
        await using var server = new ScriptedSocks5UotServer();
        await using var coordinator = CreateCoordinator(new NoopResponseSink(), logger);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(server.Server), [1], default, CancellationToken.None));

        var created = await NextSessionCreatedAsync(logger);
        Assert.Equal("scripted", Field(created, "Target"));
        Assert.Equal("socks5", Field(created, "TargetKind"));
        Assert.Equal("uot", Field(created, "UdpTransport"));
    }

    [Fact]
    public async Task ANativeTargetsSessionCreatedEventStillNamesTheNativeCarriage()
    {
        using var relay = NewRelaySocket();
        var logger = new RecordingLogger();
        await using var server = new ScriptedSocks5UdpServer((IPEndPoint)relay.LocalEndPoint!);
        await using var coordinator = CreateCoordinator(new NoopResponseSink(), logger);
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(server.Server), [1], default, CancellationToken.None));

        var created = await NextSessionCreatedAsync(logger);
        Assert.Equal("scripted", Field(created, "Target"));
        Assert.Equal("socks5", Field(created, "TargetKind"));
        Assert.Equal("native", Field(created, "UdpTransport"));
    }

    private static UdpProxyCoordinator CreateCoordinator(IUdpResponseSink sink, RecordingLogger logger) =>
        UdpCoordinatorFakes.CreateCoordinator(
            new Socks5UdpTransportFactory(new SelfTrafficRegistry(), UdpFrameBuilder.DefaultMaximumEthernetFrame, logger: logger),
            sink,
            new UdpProxyOptions { Capacity = 16, Logger = logger });

    private static async Task<RecordedEvent> NextSessionCreatedAsync(RecordingLogger logger)
    {
        await WaitForAsync(() => logger.Events.Any(recorded => string.Equals(recorded.Name, "udp.session.created", StringComparison.Ordinal)));
        return Assert.Single(logger.Events, recorded => string.Equals(recorded.Name, "udp.session.created", StringComparison.Ordinal));
    }

    private static Socket NewRelaySocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static string? Field(RecordedEvent recorded, string key)
    {
        foreach (var field in recorded.Fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal)) return field.Value is null ? null : Convert.ToString(field.Value, CultureInfo.InvariantCulture);
        }

        return null;
    }
}
