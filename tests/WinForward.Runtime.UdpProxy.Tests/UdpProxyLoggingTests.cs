using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FlowBuilders;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The session-lifecycle event's target attribution: <c>targetKind</c> stays <c>local|socks5</c>,
/// because UoT is a mode of the SOCKS5 kind rather than a third kind, and the sibling
/// <c>udpTransport</c> field names the UDP carriage the resolved target selects. Each fact asserts
/// on the event the resolved target produces through the real setup pipeline.
/// </summary>
public sealed class UdpProxyLoggingTests
{
    [Theory]
    [InlineData(false, "native")]
    [InlineData(true, "uot")]
    public async Task UdpTransportNamesTheSocks5UdpCarriageBesideAnUnchangedTargetKind(bool udpOverTcp, string expectedTransport)
    {
        var server = new Socks5Server("remote", "127.0.0.1", 1080, Username: null, Password: null, UdpOverTcp: udpOverTcp);
        var created = await LogSessionCreatedAsync(ProxyTarget.FromServer(server));

        Assert.Equal(LogLevel.Debug, created.Level);
        Assert.Equal("remote", Field(created, "Target"));
        Assert.Equal("socks5", Field(created, "TargetKind"));
        Assert.Equal(expectedTransport, Field(created, "UdpTransport"));
    }

    [Fact]
    public async Task UdpTransportIsAbsentForALocalTarget()
    {
        var local = new ProxyTarget("dns-in", Socks5: null, new LocalTarget("dns-in", Endpoint.From(IPAddress.Loopback, 5353)));
        var created = await LogSessionCreatedAsync(local);

        Assert.Equal(LogLevel.Debug, created.Level);
        Assert.Equal("dns-in", Field(created, "Target"));
        Assert.Equal("local", Field(created, "TargetKind"));
        Assert.Null(Field(created, "UdpTransport"));
    }

    private static async Task<RecordedEvent> LogSessionCreatedAsync(ProxyTarget target)
    {
        var logger = new RecordingLogger();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            new FakeTransportFactory(),
            new FakeResponseSink(),
            new UdpProxyOptions { Capacity = 16, Logger = logger });
        var flow = CreateFlow("192.0.2.53");

        Assert.True(await coordinator.TrySendSpanAsync(flow, target, [1], default, CancellationToken.None));
        await WaitForAsync(() => logger.Events.Any(recorded => string.Equals(recorded.Name, "udp.session.created", StringComparison.Ordinal)));
        return Assert.Single(logger.Events, recorded => string.Equals(recorded.Name, "udp.session.created", StringComparison.Ordinal));
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
