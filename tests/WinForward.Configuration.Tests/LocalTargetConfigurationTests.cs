using System.Globalization;
using System.Net;
using WinForward.Core;
using Xunit;

namespace WinForward.Configuration.Tests;

/// <summary>
/// The <c>localTargets</c> declaration surface: a local endpoint is an IP literal in its own list,
/// shares one name namespace with <c>socks5Servers</c>, and can only be selected by a rule whose
/// protocol selector is exactly <c>udp</c> (a rule with no selector matches every protocol, TCP
/// included). A non-loopback address is accepted and warned about, never rejected.
/// </summary>
public sealed class LocalTargetConfigurationTests
{
    private const string HostFallbackOnly = "\"host\": { \"fallbackAction\": \"pass\", \"rules\": [] }";

    [Fact]
    public void ALocalTargetNameCollidingWithASocks5ServerIsRejected()
    {
        const string json = $$"""
        {
          "socks5Servers": [{ "name": "Main", "host": "127.0.0.1", "port": 1080 }],
          "localTargets": [{ "name": "main", "host": "127.0.0.1", "port": 5353 }],
          {{HostFallbackOnly}}
        }
        """;

        AssertValidationFails(json, "localTargets[0].name");
    }

    [Fact]
    public void ALocalTargetNameCollidingWithAnotherLocalTargetIsRejected()
    {
        const string json = $$"""
        {
          "socks5Servers": [],
          "localTargets": [
            { "name": "dns-in", "host": "127.0.0.1", "port": 5353 },
            { "name": "DNS-IN", "host": "127.0.0.1", "port": 5354 }
          ],
          {{HostFallbackOnly}}
        }
        """;

        AssertValidationFails(json, "localTargets[1].name");
    }

    [Theory]
    [InlineData("dns.example.com")]
    [InlineData("localhost")]
    public void ALocalTargetHostMustBeAnIpLiteral(string host)
    {
        var json = $$"""
        {
          "socks5Servers": [],
          "localTargets": [{ "name": "dns-in", "host": "{{host}}", "port": 5353 }],
          {{HostFallbackOnly}}
        }
        """;

        AssertValidationFails(json, "localTargets[0].host");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65_536)]
    public void ALocalTargetPortMustBeInRange(int port)
    {
        var json = $$"""
        {
          "socks5Servers": [],
          "localTargets": [{ "name": "dns-in", "host": "127.0.0.1", "port": {{port.ToString(CultureInfo.InvariantCulture)}} }],
          {{HostFallbackOnly}}
        }
        """;

        AssertValidationFails(json, "localTargets[0].port");
    }

    [Fact]
    public void ATcpRuleNamingALocalTargetIsRejected()
    {
        const string json = """
        {
          "socks5Servers": [],
          "localTargets": [{ "name": "dns-in", "host": "127.0.0.1", "port": 5353 }],
          "host": {
            "fallbackAction": "pass",
            "rules": [{ "protocol": ["tcp"], "remotePort": ["53"], "action": "proxy", "target": "dns-in" }]
          }
        }
        """;

        AssertValidationFails(json, "host.rules[0].target");
    }

    [Fact]
    public void ARuleWithoutAProtocolSelectorNamingALocalTargetIsRejected()
    {
        // No selector matches every protocol, so such a rule could match TCP and is refused with the
        // same diagnostic a tcp-listed rule gets.
        const string json = """
        {
          "socks5Servers": [],
          "localTargets": [{ "name": "dns-in", "host": "127.0.0.1", "port": 5353 }],
          "host": {
            "fallbackAction": "pass",
            "rules": [{ "remotePort": ["53"], "action": "proxy", "target": "dns-in" }]
          }
        }
        """;

        AssertValidationFails(json, "host.rules[0].target");
    }

    [Fact]
    public void ARuleListingBothProtocolsForALocalTargetIsRejected()
    {
        const string json = """
        {
          "socks5Servers": [],
          "localTargets": [{ "name": "dns-in", "host": "127.0.0.1", "port": 5353 }],
          "host": {
            "fallbackAction": "pass",
            "rules": [{ "protocol": ["udp", "tcp"], "remotePort": ["53"], "action": "proxy", "target": "dns-in" }]
          }
        }
        """;

        AssertValidationFails(json, "host.rules[0].target");
    }

    [Fact]
    public void AUdpRuleNamingALocalTargetIsAcceptedAndResolvesToTheLocalEndpoint()
    {
        const string json = """
        {
          "socks5Servers": [{ "name": "remote", "host": "127.0.0.1", "port": 1080 }],
          "localTargets": [{ "name": "dns-in", "host": "127.0.0.1", "port": 5353 }],
          "host": {
            "fallbackAction": "pass",
            "rules": [{ "protocol": ["udp"], "remotePort": ["53"], "action": "proxy", "target": "dns-in" }]
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
          "socks5Servers": [{ "name": "remote", "host": "127.0.0.1", "port": 1080 }],
          "host": {
            "fallbackAction": "pass",
            "rules": [{ "remotePort": ["53"], "action": "proxy", "target": "remote" }]
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
          "socks5Servers": [],
          "localTargets": [{ "name": "resolver", "host": "192.0.2.53", "port": 53 }],
          {{HostFallbackOnly}}
        }
        """;

        var configuration = Validate(json);

        var warning = Assert.Single(configuration.Warnings);
        Assert.Equal("localTargets[0].host", warning.Path);
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
          "socks5Servers": [],
          "localTargets": [{ "name": "dns-in", "host": "{{host}}", "port": 5353 }],
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
          "socks5Servers": [{ "name": "remote", "host": "127.0.0.1", "port": 1080 }],
          "localTargets": [{ "name": "dns-in", "host": "127.0.0.1", "port": 5353 }],
          "host": {
            "fallbackAction": "pass",
            "rules": [{ "protocol": ["udp"], "action": "proxy", "target": "missing" }]
          }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics, entry => string.Equals(entry.Path, "host.rules[0].target", StringComparison.Ordinal));
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
