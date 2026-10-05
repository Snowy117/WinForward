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
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [{ "remotePort": [], "action": "proxy" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "host.rules[0].remotePort", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "host.rules[0].target", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationRejectsWhitespaceProcessSelector()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [{ "process": ["   "], "action": "pass" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "host.rules[0].process[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationRejectsDuplicateServerNamesCaseInsensitively()
    {
        const string json = """
        {
          "socks5Servers": [
            { "name": "Main", "host": "127.0.0.1", "port": 1080 },
            { "name": "main", "host": "127.0.0.2", "port": 1081 }
          ],
          "host": { "fallbackAction": "pass", "rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "socks5Servers[1].name");
    }

    [Fact]
    public void ConfigurationRejectsFallbackProxyAction()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "proxy", "rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "host.fallbackAction");
    }

    [Fact]
    public void ConfigurationDefaultsLogLevelToInfo()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(RuntimeLogLevel.Info, configuration!.LogLevel);
    }

    [Theory]
    [InlineData("error", RuntimeLogLevel.Error)]
    [InlineData("WARN", RuntimeLogLevel.Warn)]
    [InlineData(" Info ", RuntimeLogLevel.Info)]
    [InlineData("DeBuG", RuntimeLogLevel.Debug)]
    [InlineData(" trace ", RuntimeLogLevel.Trace)]
    public void ConfigurationNormalizesLogLevel(string value, RuntimeLogLevel expected)
    {
        var json = $$"""
        {
          "logLevel": "{{value}}",
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(expected, configuration!.LogLevel);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"verbose\"")]
    public void ConfigurationRejectsInvalidLogLevel(string value)
    {
        var json = $$"""
        {
          "logLevel": {{value}},
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "logLevel");
    }

    [Fact]
    public void ConfigurationRejectsWrongLogLevelJsonTypeAtFieldPath()
    {
        const string json = """
        {
          "logLevel": 3,
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [] }
        }
        """;

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path.Contains("logLevel", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("browser.exe", false)]
    [InlineData(@"C:\\Apps\\browser.exe", true)]
    public void ConfigurationComputesProcessPathDisclosureNeed(string selector, bool expected)
    {
        var json = $$"""
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [{ "process": ["{{selector}}"], "action": "pass" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(expected, configuration!.IncludeProcessPathInLogs);
    }

    [Fact]
    public void ConfigurationRejectsUnknownJsonFields()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [] },
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
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [] },
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
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 0 } ],
          "host": { "fallbackAction": "pass", "rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "socks5Servers[0].port");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationRejectsTheRenamedProxyServerKey(bool alsoDeclaresTarget)
    {
        var replacement = alsoDeclaresTarget ? ", \"target\": \"Main\"" : string.Empty;
        var json = $$"""
        {
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 1080 } ],
          "host": { "fallbackAction": "pass", "rules": [ { "action": "proxy", "proxyServer": "Main"{{replacement}} } ] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        var renamed = Assert.Single(diagnostics, diagnostic => string.Equals(diagnostic.Path, "host.rules[0].proxyServer", StringComparison.Ordinal));
        Assert.Contains("'target'", renamed.Message, StringComparison.Ordinal);
        if (alsoDeclaresTarget) Assert.DoesNotContain(diagnostics, diagnostic => string.Equals(diagnostic.Path, "host.rules[0].target", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationRejectsTargetOnNonProxyRule()
    {
        const string json = """
        {
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 1080 } ],
          "host": { "fallbackAction": "pass", "rules": [ { "action": "pass", "target": "Main" } ] }
        }
        """;

        ConfigurationAssert.Invalid(json, "host.rules[0].target");
    }

    [Fact]
    public void ConfigurationRejectsNonBlockFailureAction()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [] },
          "proxyUnavailableAction": "pass"
        }
        """;

        ConfigurationAssert.Invalid(json, "proxyUnavailableAction");
    }

    [Fact]
    public void ConfigurationRejectsUnpairedUsername()
    {
        const string json = """
        {
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 1080, "username": "user" } ],
          "host": { "fallbackAction": "pass", "rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "socks5Servers[0]");
    }

    [Fact]
    public void ConfigurationEnforcesUtf8CredentialLengthWithoutDisclosingPassword()
    {
        var maximumPassword = new string('a', 255);
        var maximumJson = $$"""
        {
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 1080, "username": "user", "password": "{{maximumPassword}}" } ],
          "host": { "fallbackAction": "pass", "rules": [] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(maximumJson, out var maximumDto, out _));
        Assert.NotNull(maximumDto);
        Assert.True(ConfigurationLoader.TryValidate(maximumDto, out _, out var maximumDiagnostics), string.Join("; ", maximumDiagnostics));

        var password = new string('\u00e9', 128);
        var json = $$"""
        {
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 1080, "username": "user", "password": "{{password}}" } ],
          "host": { "fallbackAction": "pass", "rules": [] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("socks5Servers[0].password", diagnostic.Path);
        Assert.DoesNotContain(password, diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationRejectsInvalidHost()
    {
        const string json = """
        {
          "socks5Servers": [ { "name": "Main", "host": "not a host name!", "port": 1080 } ],
          "host": { "fallbackAction": "pass", "rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "socks5Servers[0].host");
    }

    [Fact]
    public void ConfigurationRequiresFallbackAndSectionFields()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "host.fallbackAction");

        const string missingSections = "{}";
        Assert.True(ConfigurationLoader.TryParse(missingSections, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "socks5Servers", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "host", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("adapterId")]
    [InlineData("adapterName")]
    [InlineData("protocol")]
    [InlineData("addressFamily")]
    [InlineData("remoteCidr")]
    [InlineData("remotePort")]
    public void ConfigurationRejectsNullRuleMatchValuesWithoutThrowing(string field)
    {
        var json = $$"""
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [{ "{{field}}": [null], "action": "pass" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, $"host.rules[0].{field}[0]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("socks5Servers")]
    [InlineData("host.rules")]
    public void ConfigurationRejectsNullSectionEntriesWithoutThrowing(string section)
    {
        var json = $$"""
        {
          "socks5Servers": {{(string.Equals(section, "socks5Servers", StringComparison.Ordinal) ? "[null]" : "[]")}},
          "host": { "fallbackAction": "pass", "rules": {{(string.Equals(section, "host.rules", StringComparison.Ordinal) ? "[null]" : "[]")}} }
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
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Empty(configuration!.Policy.ForwardedRules);
        Assert.Equal(FlowAction.Pass, configuration.Policy.ForwardedFallbackAction);
    }

    [Fact]
    public void ConfigurationAcceptsForwardedRulesWithoutAnAdapterSelector()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [] },
          "forwarded": { "rules": [{ "remoteCidr": ["10.0.0.0/8"], "action": "pass" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        var rule = Assert.Single(configuration!.Policy.ForwardedRules);
        Assert.Null(rule.Matcher.AdapterIds);
        Assert.Null(rule.Matcher.AdapterNames);
    }

    [Fact]
    public void ConfigurationReadsAConfiguredForwardedFallbackAction()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [] },
          "forwarded": { "fallbackAction": "block", "rules": [] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(FlowAction.Block, configuration!.Policy.ForwardedFallbackAction);
    }

    [Fact]
    public void ConfigurationRejectsFallbackProxyInTheForwardedDomain()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [] },
          "forwarded": { "fallbackAction": "proxy", "rules": [] }
        }
        """;

        ConfigurationAssert.Invalid(json, "forwarded.fallbackAction");
    }

    [Fact]
    public void ConfigurationRejectsProcessSelectorsInTheForwardedDomain()
    {
        const string forwarded = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [] },
          "forwarded": { "rules": [{ "process": ["browser.exe"], "action": "pass" }] }
        }
        """;
        const string host = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [{ "process": ["browser.exe"], "action": "pass" }] }
        }
        """;

        ConfigurationAssert.Invalid(forwarded, "forwarded.rules[0].process");

        Assert.True(ConfigurationLoader.TryParse(host, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics), string.Join("; ", diagnostics));
    }

    [Fact]
    public void ConfigurationQualifiesRuleDiagnosticsWithTheirDomain()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [{ "remotePort": ["nope"], "action": "pass" }] },
          "forwarded": { "rules": [{ "remotePort": ["nope"], "action": "pass" }] }
        }
        """;

        ConfigurationAssert.Invalid(json, "host.rules[0].remotePort[0]", "forwarded.rules[0].remotePort[0]");
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
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [{ "remoteCidr": ["192.0.2.0/24", "not-a-cidr"], "action": "pass" }] }
        }
        """;

        var diagnostic = SingleInvalidElement(json, "host.rules[0].remoteCidr[1]");
        Assert.Equal("Invalid CIDR 'not-a-cidr'.", diagnostic.Message);
    }

    [Fact]
    public void ConfigurationIndexesUnsupportedSetElements()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [{ "protocol": ["tcp", "sctp"], "action": "pass" }] }
        }
        """;

        var diagnostic = SingleInvalidElement(json, "host.rules[0].protocol[1]");
        Assert.Equal("Unsupported value 'sctp'.", diagnostic.Message);
    }

    [Fact]
    public void ConfigurationIndexesEachInvalidPortElementIndependently()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [{ "remotePort": ["0", "443", "500-100"], "action": "pass" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(new ConfigDiagnostic("host.rules[0].remotePort[0]", "Invalid port or range '0'."), diagnostics[0]);
        Assert.Equal(new ConfigDiagnostic("host.rules[0].remotePort[2]", "Invalid port or range '500-100'."), diagnostics[1]);
    }

    [Fact]
    public void ConfigurationNormalizesAndMergesRemotePortRanges()
    {
        const string json = """
        {
          "socks5Servers": [],
          "host": { "fallbackAction": "pass", "rules": [{ "remotePort": ["443", "100-200", "80-150", "201-250", "443"], "action": "pass" }] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.NotNull(configuration);
        Assert.Equal([(80, 250), (443, 443)], configuration.Policy.HostRules[0].Matcher.RemotePorts);
    }
}
