using WinForward.Configuration;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class ConfigurationValidationTests
{
    [Fact]
    public void ConfigurationRejectsEmptyMatchAndProxyWithoutServer()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [{ "remotePort": [], "action": "proxy" }],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "rules[0].remotePort", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "rules[0].proxyServer", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationRejectsWhitespaceProcessSelector()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [{ "process": ["   "], "action": "pass" }],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "rules[0].process[0]", StringComparison.Ordinal));
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
          "rules": [],
          "fallbackAction": "pass"
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
          "rules": [],
          "fallbackAction": "proxy"
        }
        """;

        ConfigurationAssert.Invalid(json, "fallbackAction");
    }

    [Fact]
    public void ConfigurationDefaultsLogLevelToInfo()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass"
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
          "rules": [],
          "fallbackAction": "pass"
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
          "rules": [],
          "fallbackAction": "pass"
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
          "rules": [],
          "fallbackAction": "pass"
        }
        """;

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path.Contains("logLevel", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("browser.exe", false)]
    [InlineData("C:\\\\Apps\\\\browser.exe", true)]
    public void ConfigurationComputesProcessPathDisclosureNeed(string selector, bool expected)
    {
        var json = $$"""
        {
          "socks5Servers": [],
          "rules": [{ "process": ["{{selector}}"], "action": "pass" }],
          "fallbackAction": "pass"
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
          "rules": [],
          "fallbackAction": "pass",
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
          "rules": [],
          "fallbackAction": "pass",
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
          "rules": [],
          "fallbackAction": "pass"
        }
        """;

        ConfigurationAssert.Invalid(json, "socks5Servers[0].port");
    }

    [Fact]
    public void ConfigurationRejectsProxyServerOnNonProxyRule()
    {
        const string json = """
        {
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 1080 } ],
          "rules": [ { "action": "pass", "proxyServer": "Main" } ],
          "fallbackAction": "pass"
        }
        """;

        ConfigurationAssert.Invalid(json, "rules[0].proxyServer");
    }

    [Fact]
    public void ConfigurationRejectsNonBlockFailureAction()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
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
          "rules": [],
          "fallbackAction": "pass"
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
          "rules": [],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(maximumJson, out var maximumDto, out _));
        Assert.NotNull(maximumDto);
        Assert.True(ConfigurationLoader.TryValidate(maximumDto!, out _, out var maximumDiagnostics), string.Join("; ", maximumDiagnostics));

        var password = new string('\u00e9', 128);
        var json = $$"""
        {
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 1080, "username": "user", "password": "{{password}}" } ],
          "rules": [],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
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
          "rules": [],
          "fallbackAction": "pass"
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
          "rules": []
        }
        """;

        ConfigurationAssert.Invalid(json, "fallbackAction");

        const string missingSections = """
        {}
        """;
        Assert.True(ConfigurationLoader.TryParse(missingSections, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "socks5Servers", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "rules", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "fallbackAction", StringComparison.Ordinal));
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
          "rules": [{ "{{field}}": [null], "action": "pass" }],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, $"rules[0].{field}[0]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("socks5Servers")]
    [InlineData("rules")]
    public void ConfigurationRejectsNullSectionEntriesWithoutThrowing(string section)
    {
        var json = $$"""
        {
          "socks5Servers": {{(string.Equals(section, "socks5Servers", StringComparison.Ordinal) ? "[null]" : "[]")}},
          "rules": {{(string.Equals(section, "rules", StringComparison.Ordinal) ? "[null]" : "[]")}},
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, $"{section}[0]", StringComparison.Ordinal));
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
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
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
          "rules": [{ "remoteCidr": ["192.0.2.0/24", "not-a-cidr"], "action": "pass" }],
          "fallbackAction": "pass"
        }
        """;

        var diagnostic = SingleInvalidElement(json, "rules[0].remoteCidr[1]");
        Assert.Equal("Invalid CIDR 'not-a-cidr'.", diagnostic.Message);
    }

    [Fact]
    public void ConfigurationIndexesUnsupportedSetElements()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [{ "protocol": ["tcp", "sctp"], "action": "pass" }],
          "fallbackAction": "pass"
        }
        """;

        var diagnostic = SingleInvalidElement(json, "rules[0].protocol[1]");
        Assert.Equal("Unsupported value 'sctp'.", diagnostic.Message);
    }

    [Fact]
    public void ConfigurationIndexesEachInvalidPortElementIndependently()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [{ "remotePort": ["0", "443", "500-100"], "action": "pass" }],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(new ConfigDiagnostic("rules[0].remotePort[0]", "Invalid port or range '0'."), diagnostics[0]);
        Assert.Equal(new ConfigDiagnostic("rules[0].remotePort[2]", "Invalid port or range '500-100'."), diagnostics[1]);
    }

    [Fact]
    public void ConfigurationNormalizesAndMergesRemotePortRanges()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [{ "remotePort": ["443", "100-200", "80-150", "201-250", "443"], "action": "pass" }],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.NotNull(configuration);
        Assert.Equal(new[] { ((ushort)80, (ushort)250), ((ushort)443, (ushort)443) }, configuration!.Policy.Rules[0].Matcher.RemotePorts);
    }
}
