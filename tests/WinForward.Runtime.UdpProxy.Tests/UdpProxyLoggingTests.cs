using System.Globalization;
using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The session-lifecycle event's target attribution: <c>targetKind</c> stays <c>local|socks5</c>,
/// because UoT is a mode of the SOCKS5 kind rather than a third kind, and the sibling
/// <c>udpTransport</c> field names the UDP carriage the resolved target selects.
/// </summary>
public sealed class UdpProxyLoggingTests
{
    [Theory]
    [InlineData(false, "native")]
    [InlineData(true, "uot")]
    public void UdpTransportNamesTheSocks5UdpCarriageBesideAnUnchangedTargetKind(bool udpOverTcp, string expectedTransport)
    {
        var server = new Socks5Server("remote", "127.0.0.1", 1080, Username: null, Password: null, UdpOverTcp: udpOverTcp);
        var fields = LogSessionCreated(ProxyTarget.FromServer(server));

        Assert.Equal("remote", Field(fields, "target"));
        Assert.Equal("socks5", Field(fields, "targetKind"));
        Assert.Equal(expectedTransport, Field(fields, "udpTransport"));
    }

    [Fact]
    public void UdpTransportIsAbsentForALocalTargetAndForNoTargetAtAll()
    {
        var local = new ProxyTarget("dns-in", Socks5: null, new LocalTarget("dns-in", Endpoint.From(IPAddress.Loopback, 5353)));

        Assert.Equal("dns-in", Field(LogSessionCreated(local), "target"));
        Assert.Equal("local", Field(LogSessionCreated(local), "targetKind"));
        Assert.Null(Field(LogSessionCreated(local), "udpTransport"));

        Assert.Null(Field(LogSessionCreated(target: null), "target"));
        Assert.Null(Field(LogSessionCreated(target: null), "targetKind"));
        Assert.Null(Field(LogSessionCreated(target: null), "udpTransport"));
    }

    private static RuntimeLogField[] LogSessionCreated(ProxyTarget? target)
    {
        var logger = new RecordingRuntimeLogger();
        var flow = FlowBuilders.CreateFlow("192.0.2.53");
        var association = new UdpAssociation(flow, new RelayAlias(flow), 1, DateTimeOffset.UnixEpoch);

        UdpProxyLogging.LogDebug(logger, "udp.session.created", flow, 1, association, target);

        var (_, name, fields) = Assert.Single(logger.Events);
        Assert.Equal("udp.session.created", name);
        return fields;
    }

    private static string? Field(RuntimeLogField[] fields, string key)
    {
        foreach (var field in fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal)) return field.Value is null ? null : Convert.ToString(field.Value, CultureInfo.InvariantCulture);
        }

        return null;
    }
}
