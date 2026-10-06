using System.Globalization;
using System.Net;
using WinForward.Core;
using Xunit;

namespace WinForward.Configuration.Tests;

/// <summary>
/// The <c>LocalTargets</c> declaration surface: a local endpoint is an IP literal in its own list,
/// shares one name namespace with <c>Socks5Servers</c>, and can only be selected by a rule whose
/// protocol selector is exactly <c>udp</c> (a rule with no selector matches every protocol, TCP
/// included). A non-loopback address is accepted and warned about, never rejected.
/// </summary>
public sealed class LocalTargetConfigurationTests
{
    private const string HostFallbackOnly = "\"Host\": { \"FallbackAction\": \"pass\", \"Rules\": [] }";

    [Fact]
    public void ALocalTargetNameCollidingWithASocks5ServerIsRejected()
    {
        const string json = $$"""
        {
          "Socks5Servers": [{ "Name": "Main", "Host": "127.0.0.1", "Port": 1080 }],
          "LocalTargets": [{ "Name": "main", "Host": "127.0.0.1", "Port": 5353 }],
          {{HostFallbackOnly}}
        }
        """;

        AssertValidationFails(json, "WinForward.LocalTargets[0].Name");
    }

    [Fact]
    public void ALocalTargetNameCollidingWithAnotherLocalTargetIsRejected()
    {
        const string json = $$"""
        {
          "Socks5Servers": [],
          "LocalTargets": [
            { "Name": "dns-in", "Host": "127.0.0.1", "Port": 5353 },
            { "Name": "DNS-IN", "Host": "127.0.0.1", "Port": 5354 }
          ],
          {{HostFallbackOnly}}
        }
        """;

        AssertValidationFails(json, "WinForward.LocalTargets[1].Name");
    }

    [Theory]
    [InlineData("dns.example.com")]
    [InlineData("localhost")]
    public void ALocalTargetHostMustBeAnIpLiteral(string host)
    {
        var json = $$"""
        {
          "Socks5Servers": [],
          "LocalTargets": [{ "Name": "dns-in", "Host": "{{host}}", "Port": 5353 }],
          {{HostFallbackOnly}}
        }
        """;

        AssertValidationFails(json, "WinForward.LocalTargets[0].Host");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65_536)]
    public void ALocalTargetPortMustBeInRange(int port)
    {
        var json = $$"""
        {
          "Socks5Servers": [],
          "LocalTargets": [{ "Name": "dns-in", "Host": "127.0.0.1", "Port": {{port.ToString(CultureInfo.InvariantCulture)}} }],
          {{HostFallbackOnly}}
        }
        """;

        AssertValidationFails(json, "WinForward.LocalTargets[0].Port");
    }

    [Fact]
    public void ATcpRuleNamingALocalTargetIsRejected()
    {
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

        AssertValidationFails(json, "WinForward.Host.Rules[0].Target");
    }

    [Fact]
    public void ARuleWithoutAProtocolSelectorNamingALocalTargetIsRejected()
    {
        // No selector matches every protocol, so such a rule could match TCP and is refused with the
        // same diagnostic a tcp-listed rule gets.
        const string json = """
        {
          "Socks5Servers": [],
          "LocalTargets": [{ "Name": "dns-in", "Host": "127.0.0.1", "Port": 5353 }],
          "Host": {
            "FallbackAction": "pass",
            "Rules": [{ "RemotePort": ["53"], "Action": "proxy", "Target": "dns-in" }]
          }
        }
        """;

        AssertValidationFails(json, "WinForward.Host.Rules[0].Target");
    }

    [Fact]
    public void ARuleListingBothProtocolsForALocalTargetIsRejected()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "LocalTargets": [{ "Name": "dns-in", "Host": "127.0.0.1", "Port": 5353 }],
          "Host": {
            "FallbackAction": "pass",
            "Rules": [{ "Protocol": ["udp", "tcp"], "RemotePort": ["53"], "Action": "proxy", "Target": "dns-in" }]
          }
        }
        """;

        AssertValidationFails(json, "WinForward.Host.Rules[0].Target");
    }

    [Fact]
    public void AUdpRuleNamingALocalTargetIsAcceptedAndResolvesToTheLocalEndpoint()
    {
        const string json = """
        {
          "Socks5Servers": [{ "Name": "remote", "Host": "127.0.0.1", "Port": 1080 }],
          "LocalTargets": [{ "Name": "dns-in", "Host": "127.0.0.1", "Port": 5353 }],
          "Host": {
            "FallbackAction": "pass",
            "Rules": [{ "Protocol": ["udp"], "RemotePort": ["53"], "Action": "proxy", "Target": "dns-in" }]
          }
        }
        """;

        var configuration = Validate(json);

        Assert.True(configuration.Targets.TryGetValue("dns-in", out var target));
        Assert.True(target.IsLocal);
        Assert.Null(target.Socks5);
        Assert.Equal("dns-in", target.Local!.Name);
        Assert.Equal(Endpoint.From(IPAddress.Loopback, 5353), target.Local.Endpoint);

        var rule = Assert.Single(configuration.Policy.HostRules);
        Assert.Equal("dns-in", rule.Decision.TargetName);
        Assert.Equal(FlowAction.Proxy, rule.Decision.Action);

        // The SOCKS5 entry keeps its own resolution shape beside the local one.
        Assert.True(configuration.Targets.TryGetValue("remote", out var socks5));
        Assert.False(socks5.IsLocal);
        Assert.Equal("127.0.0.1", socks5.Socks5!.Host);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void ASocks5TargetStillAcceptsAProtocolSelectorFreeRule()
    {
        const string json = """
        {
          "Socks5Servers": [{ "Name": "remote", "Host": "127.0.0.1", "Port": 1080 }],
          "Host": {
            "FallbackAction": "pass",
            "Rules": [{ "RemotePort": ["53"], "Action": "proxy", "Target": "remote" }]
          }
        }
        """;

        var configuration = Validate(json);

        Assert.Equal("remote", Assert.Single(configuration.Policy.HostRules).Decision.TargetName);
    }

    [Fact]
    public void ANonLoopbackLocalTargetWarnsWithTheAddressAndBothConsequences()
    {
        const string json = $$"""
        {
          "Socks5Servers": [],
          "LocalTargets": [{ "Name": "resolver", "Host": "192.0.2.53", "Port": 53 }],
          {{HostFallbackOnly}}
        }
        """;

        var configuration = Validate(json);

        var warning = Assert.Single(configuration.Warnings);
        Assert.Equal("WinForward.LocalTargets[0].Host", warning.Path);
        Assert.Contains("192.0.2.53", warning.Message, StringComparison.Ordinal);
        Assert.Contains("in the clear", warning.Message, StringComparison.Ordinal);
        Assert.Contains("original destination", warning.Message, StringComparison.Ordinal);
        // Accepted, not rejected: the warning is the operator's signal, not a guard.
        Assert.True(configuration.Targets.ContainsKey("resolver"));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    public void ALoopbackLocalTargetDoesNotWarn(string host)
    {
        var json = $$"""
        {
          "Socks5Servers": [],
          "LocalTargets": [{ "Name": "dns-in", "Host": "{{host}}", "Port": 5353 }],
          {{HostFallbackOnly}}
        }
        """;

        var configuration = Validate(json);

        Assert.Empty(configuration.Warnings);
        Assert.True(configuration.Targets.TryGetValue("dns-in", out var target));
        Assert.Equal(IPAddress.Parse(host).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? AddressFamilyKind.IPv4 : AddressFamilyKind.IPv6, target.Local!.Endpoint.AddressFamily);
    }

    [Fact]
    public void AProxyRuleNamingNoDeclaredTargetIsRejectedWithTheTargetMessage()
    {
        const string json = """
        {
          "Socks5Servers": [{ "Name": "remote", "Host": "127.0.0.1", "Port": 1080 }],
          "LocalTargets": [{ "Name": "dns-in", "Host": "127.0.0.1", "Port": 5353 }],
          "Host": {
            "FallbackAction": "pass",
            "Rules": [{ "Protocol": ["udp"], "Action": "proxy", "Target": "missing" }]
          }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics, entry => string.Equals(entry.Path, "WinForward.Host.Rules[0].Target", StringComparison.Ordinal));
        Assert.Contains("configured target", diagnostic.Message, StringComparison.Ordinal);
    }

    private static void AssertValidationFails(string json, params string[] expectedPaths)
    {
        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out var parseDiagnostics), string.Join("; ", parseDiagnostics));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        foreach (var path in expectedPaths) Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, path, StringComparison.Ordinal));
    }

    private static ValidatedConfiguration Validate(string json)
    {
        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out var parseDiagnostics), string.Join("; ", parseDiagnostics));
        Assert.NotNull(dto);
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.NotNull(configuration);
        return configuration;
    }
}
