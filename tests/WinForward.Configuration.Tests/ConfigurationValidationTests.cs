using WinForward.Core;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Configuration.Tests;

public sealed class ConfigurationValidationTests
{
    [Fact]
    public void ConfigurationRejectsEmptyMatchAndProxyWithoutServer()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [{ "RemotePort": [], "Action": "proxy" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "WinForward.Host.Rules[0].RemotePort", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "WinForward.Host.Rules[0].Target", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationRejectsWhitespaceProcessSelector()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [{ "Process": ["   "], "Action": "pass" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "WinForward.Host.Rules[0].Process[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationRejectsDuplicateServerNamesCaseInsensitively()
    {
        const string json = """
        {
          "Socks5Servers": [
            { "Name": "Main", "Host": "127.0.0.1", "Port": 1080 },
            { "Name": "main", "Host": "127.0.0.2", "Port": 1081 }
          ],
          "Host": { "FallbackAction": "pass", "Rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "WinForward.Socks5Servers[1].Name");
    }

    [Theory]
    [InlineData(", \"UdpOverTcp\": true", true)]
    [InlineData(", \"UdpOverTcp\": false", false)]
    [InlineData("", false)]
    public void Socks5ServerUdpOverTcpFlagDefaultsOffAndReachesTheTarget(string member, bool expected)
    {
        var json = $$"""
        {
          "Socks5Servers": [ { "Name": "Main", "Host": "127.0.0.1", "Port": 1080{{member}} } ],
          "Host": { "FallbackAction": "pass", "Rules": [] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out var parseDiagnostics), string.Join("; ", parseDiagnostics));
        Assert.NotNull(dto);
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.NotNull(configuration);
        Assert.True(configuration.Targets.TryGetValue("Main", out var target));
        Assert.Equal("Main", target.Name);
        Assert.Equal(expected, target.Socks5!.UdpOverTcp);
    }

    /// <summary>
    /// The opt-in key is spelled exactly <c>udpOverTcp</c>: a different casing is an unknown property
    /// and keeps failing closed at parse with its JSON path.
    /// </summary>
    [Theory]
    [InlineData("udpOverTCP")]
    [InlineData("udp_over_tcp")]
    [InlineData("udp-over-tcp")]
    public void MisspelledUdpOverTcpKeyFailsClosedAtParse(string misspelled)
    {
        var json = $$"""
        {
          "Socks5Servers": [ { "Name": "Main", "Host": "127.0.0.1", "Port": 1080, "{{misspelled}}": true } ],
          "Host": { "FallbackAction": "pass", "Rules": [] }
        }
        """;

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal($"WinForward.Socks5Servers[0].{misspelled}", diagnostic.Path);
    }

    [Fact]
    public void ConfigurationRejectsFallbackProxyAction()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "proxy", "Rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "WinForward.Host.FallbackAction");
    }

    /// <summary>
    /// The retired <c>logLevel</c> and <c>logFormat</c> keys are unknown members: the vocabulary
    /// moved into the standard <c>Logging</c> section, so a pre-migration file fails closed at
    /// parse with the key's JSON path instead of half-applying.
    /// </summary>
    [Theory]
    [InlineData("\"logLevel\": \"info\"", "logLevel")]
    [InlineData("\"logFormat\": \"json\"", "logFormat")]
    public void RetiredLoggingKeysFailClosedAtParseWithTheirJsonPath(string member, string retiredKey)
    {
        var json = $$"""
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          {{member}}
        }
        """;

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal($"WinForward.{retiredKey}", diagnostic.Path);
    }

    [Theory]
    [InlineData("browser.exe", false)]
    [InlineData(@"C:\\Apps\\browser.exe", true)]
    public void ConfigurationComputesProcessPathDisclosureNeed(string selector, bool expected)
    {
        var json = $$"""
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [{ "Process": ["{{selector}}"], "Action": "pass" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(expected, configuration.IncludeProcessPathInLogs);
    }

    [Fact]
    public void ConfigurationRejectsUnknownJsonFields()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "unexpectedField": true
        }
        """;

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path.Contains("unexpectedField", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationParseDiagnosticsNameTheFailingFieldWithoutEchoingCredentials()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "unexpectedField": "credential-that-must-not-appear"
        }
        """;

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("unexpectedField", diagnostic.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-that-must-not-appear", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationRejectsOutOfRangePort()
    {
        const string json = """
        {
          "Socks5Servers": [ { "Name": "Main", "Host": "127.0.0.1", "Port": 0 } ],
          "Host": { "FallbackAction": "pass", "Rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "WinForward.Socks5Servers[0].Port");
    }

    /// <summary>
    /// The removed association keys left the schema outright, so an old configuration fails closed
    /// at parse through the loader's unknown-property rule and the diagnostic names the key's JSON
    /// path. There is no migration property and no removal message.
    /// </summary>
    [Theory]
    [InlineData("\"udpAssociationReuse\": \"auto\"", "udpAssociationReuse")]
    [InlineData("\"udpAssociationMaxPerServer\": 1024", "udpAssociationMaxPerServer")]
    [InlineData("\"udpAssociationFlowsPerAssociation\": 16", "udpAssociationFlowsPerAssociation")]
    public void RemovedAssociationKeysFailClosedAtParseWithTheirJsonPath(string member, string removedKey)
    {
        var json = $$"""
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          {{member}}
        }
        """;

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal($"WinForward.{removedKey}", diagnostic.Path);
    }

    /// <summary>
    /// The pre-rename <c>proxyServer</c> spelling is an unknown property like any other: the rename
    /// is complete, so the old spelling fails at parse rather than at validation with a rename hint.
    /// </summary>
    [Fact]
    public void RemovedProxyServerSpellingFailsClosedAtParseWithItsJsonPath()
    {
        const string json = """
        {
          "Socks5Servers": [ { "Name": "Main", "Host": "127.0.0.1", "Port": 1080 } ],
          "Host": { "FallbackAction": "pass", "Rules": [ { "Action": "proxy", "proxyServer": "Main" } ] }
        }
        """;

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("WinForward.Host.Rules[0].proxyServer", diagnostic.Path);
    }

    [Fact]
    public void ConfigurationWithoutTheRemovedKeysValidatesAsBefore()
    {
        const string json = """
        {
          "Socks5Servers": [ { "Name": "Main", "Host": "127.0.0.1", "Port": 1080 } ],
          "Host": { "FallbackAction": "pass", "Rules": [ { "Action": "proxy", "Target": "Main" } ] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out var parseDiagnostics), string.Join("; ", parseDiagnostics));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Empty(configuration.Warnings);
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionCapacity, configuration.UdpSessionCapacity);
        Assert.Equal(ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes, configuration.UdpRelayReceiveBufferBytes);
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionIdleTimeout, configuration.UdpSessionIdleTimeout);
    }

    [Fact]
    public void ConfigurationRejectsTargetOnNonProxyRule()
    {
        const string json = """
        {
          "Socks5Servers": [ { "Name": "Main", "Host": "127.0.0.1", "Port": 1080 } ],
          "Host": { "FallbackAction": "pass", "Rules": [ { "Action": "pass", "Target": "Main" } ] }
        }
        """;

        ConfigurationAssert.Invalid(json, "WinForward.Host.Rules[0].Target");
    }

    [Fact]
    public void ConfigurationRejectsNonBlockFailureAction()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "ProxyUnavailableAction": "pass"
        }
        """;

        ConfigurationAssert.Invalid(json, "WinForward.ProxyUnavailableAction");
    }

    [Fact]
    public void ConfigurationRejectsUnpairedUsername()
    {
        const string json = """
        {
          "Socks5Servers": [ { "Name": "Main", "Host": "127.0.0.1", "Port": 1080, "Username": "user" } ],
          "Host": { "FallbackAction": "pass", "Rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "WinForward.Socks5Servers[0]");
    }

    [Fact]
    public void ConfigurationEnforcesUtf8CredentialLengthWithoutDisclosingPassword()
    {
        var maximumPassword = new string('a', 255);
        var maximumJson = $$"""
        {
          "Socks5Servers": [ { "Name": "Main", "Host": "127.0.0.1", "Port": 1080, "Username": "user", "Password": "{{maximumPassword}}" } ],
          "Host": { "FallbackAction": "pass", "Rules": [] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(maximumJson, out var maximumDto, out _));
        Assert.NotNull(maximumDto);
        Assert.True(ConfigurationLoader.TryValidate(maximumDto, out _, out var maximumDiagnostics), string.Join("; ", maximumDiagnostics));

        var password = new string('\u00e9', 128);
        var json = $$"""
        {
          "Socks5Servers": [ { "Name": "Main", "Host": "127.0.0.1", "Port": 1080, "Username": "user", "Password": "{{password}}" } ],
          "Host": { "FallbackAction": "pass", "Rules": [] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("WinForward.Socks5Servers[0].Password", diagnostic.Path);
        Assert.DoesNotContain(password, diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationRejectsInvalidHost()
    {
        const string json = """
        {
          "Socks5Servers": [ { "Name": "Main", "Host": "not a host name!", "Port": 1080 } ],
          "Host": { "FallbackAction": "pass", "Rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "WinForward.Socks5Servers[0].Host");
    }

    [Fact]
    public void ConfigurationRequiresFallbackAndSectionFields()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "Rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "WinForward.Host.FallbackAction");

        const string missingSections = "{}";
        Assert.True(ConfigurationLoader.TryParse(missingSections, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "WinForward.Socks5Servers", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "WinForward.Host", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Process")]
    [InlineData("AdapterId")]
    [InlineData("AdapterName")]
    [InlineData("Protocol")]
    [InlineData("AddressFamily")]
    [InlineData("RemoteCidr")]
    [InlineData("RemotePort")]
    public void ConfigurationRejectsNullRuleMatchValuesWithoutThrowing(string field)
    {
        var json = $$"""
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [{ "{{field}}": [null], "Action": "pass" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, $"WinForward.Host.Rules[0].{field}[0]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("WinForward.Socks5Servers")]
    [InlineData("WinForward.Host.Rules")]
    public void ConfigurationRejectsNullSectionEntriesWithoutThrowing(string section)
    {
        var json = $$"""
        {
          "Socks5Servers": {{(string.Equals(section, "WinForward.Socks5Servers", StringComparison.Ordinal) ? "[null]" : "[]")}},
          "Host": { "FallbackAction": "pass", "Rules": {{(string.Equals(section, "WinForward.Host.Rules", StringComparison.Ordinal) ? "[null]" : "[]")}} }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, $"{section}[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationAcceptsOmittedForwardedDomainAndDefaultsItsFallbackToPass()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Empty(configuration.Policy.ForwardedRules);
        Assert.Equal(FlowAction.Pass, configuration.Policy.ForwardedFallbackAction);
    }

    [Fact]
    public void ConfigurationAcceptsForwardedRulesWithoutAnAdapterSelector()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "Forwarded": { "Rules": [{ "RemoteCidr": ["10.0.0.0/8"], "Action": "pass" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        var rule = Assert.Single(configuration.Policy.ForwardedRules);
        Assert.Null(rule.Matcher.AdapterIds);
        Assert.Null(rule.Matcher.AdapterNames);
    }

    [Fact]
    public void ConfigurationReadsAConfiguredForwardedFallbackAction()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "Forwarded": { "FallbackAction": "block", "Rules": [] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(FlowAction.Block, configuration.Policy.ForwardedFallbackAction);
    }

    [Fact]
    public void ConfigurationRejectsFallbackProxyInTheForwardedDomain()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "Forwarded": { "FallbackAction": "proxy", "Rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "WinForward.Forwarded.FallbackAction");
    }

    [Fact]
    public void ConfigurationRejectsProcessSelectorsInTheForwardedDomain()
    {
        const string forwarded = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "Forwarded": { "Rules": [{ "Process": ["browser.exe"], "Action": "pass" }] }
        }
        """;
        const string host = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [{ "Process": ["browser.exe"], "Action": "pass" }] }
        }
        """;

        ConfigurationAssert.Invalid(forwarded, "WinForward.Forwarded.Rules[0].Process");

        Assert.True(ConfigurationLoader.TryParse(host, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics), string.Join("; ", diagnostics));
    }

    [Fact]
    public void ConfigurationQualifiesRuleDiagnosticsWithTheirDomain()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [{ "RemotePort": ["nope"], "Action": "pass" }] },
          "Forwarded": { "Rules": [{ "RemotePort": ["nope"], "Action": "pass" }] }
        }
        """;

        ConfigurationAssert.Invalid(json, "WinForward.Host.Rules[0].RemotePort[0]", "WinForward.Forwarded.Rules[0].RemotePort[0]");
    }

    /// <summary>
    /// Shared helper for the invalid-element tests: the diagnostic must land on the failing
    /// element's indexed field path (matching the null-element behavior) with the offending value
    /// named in the message.
    /// </summary>
    private static ConfigDiagnostic SingleInvalidElement(string json, string expectedPath)
    {
        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(expectedPath, diagnostic.Path);
        return diagnostic;
    }

    [Fact]
    public void ConfigurationIndexesInvalidCidrElements()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [{ "RemoteCidr": ["192.0.2.0/24", "not-a-cidr"], "Action": "pass" }] }
        }
        """;

        var diagnostic = SingleInvalidElement(json, "WinForward.Host.Rules[0].RemoteCidr[1]");
        Assert.Equal("Invalid CIDR 'not-a-cidr'.", diagnostic.Message);
    }

    [Fact]
    public void ConfigurationIndexesUnsupportedSetElements()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [{ "Protocol": ["tcp", "sctp"], "Action": "pass" }] }
        }
        """;

        var diagnostic = SingleInvalidElement(json, "WinForward.Host.Rules[0].Protocol[1]");
        Assert.Equal("Unsupported value 'sctp'.", diagnostic.Message);
    }

    [Fact]
    public void ConfigurationIndexesEachInvalidPortElementIndependently()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [{ "RemotePort": ["0", "443", "500-100"], "Action": "pass" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(new ConfigDiagnostic("WinForward.Host.Rules[0].RemotePort[0]", "Invalid port or range '0'."), diagnostics[0]);
        Assert.Equal(new ConfigDiagnostic("WinForward.Host.Rules[0].RemotePort[2]", "Invalid port or range '500-100'."), diagnostics[1]);
    }

    [Fact]
    public void ConfigurationNormalizesAndMergesRemotePortRanges()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [{ "RemotePort": ["443", "100-200", "80-150", "201-250", "443"], "Action": "pass" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.NotNull(configuration);
        Assert.Equal([(80, 250), (443, 443)], configuration.Policy.HostRules[0].Matcher.RemotePorts);
    }
}
