using System.Globalization;
using WinForward.Configuration;
using WinForward.Runtime.Socks5;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class ConfigurationLimitsTests
{
    [Fact]
    public void ConfigurationDefaultsTcpFlowCapacityWhenOmitted()
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
        Assert.Equal(ConfigurationLoader.DefaultTcpFlowCapacity, configuration!.TcpFlowCapacity);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void ConfigurationTreatsExplicitNullTcpFlowCapacityAsDefault()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          "tcpFlowCapacity": null
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(ConfigurationLoader.DefaultTcpFlowCapacity, configuration!.TcpFlowCapacity);
        Assert.Empty(configuration.Warnings);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public void ConfigurationAcceptsTcpFlowCapacityAtOrBelowDefaultWithoutWarning(int value)
    {
        var json = string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          "tcpFlowCapacity": {{value}}
        }
        """);

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(value, configuration!.TcpFlowCapacity);
        Assert.Empty(configuration.Warnings);
    }

    [Theory]
    [InlineData(4097)]
    [InlineData(8192)]
    public void ConfigurationWarnsAboveDefaultTcpFlowCapacityWithoutBlocking(int value)
    {
        var json = string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          "tcpFlowCapacity": {{value}}
        }
        """);

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(value, configuration!.TcpFlowCapacity);
        Assert.Empty(diagnostics);
        var warning = Assert.Single(configuration.Warnings);
        Assert.Equal("tcpFlowCapacity", warning.Path);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8193)]
    public void ConfigurationRejectsTcpFlowCapacityOutsideSupportedRange(int value)
    {
        var json = string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          "tcpFlowCapacity": {{value}}
        }
        """);

        ConfigurationAssert.Invalid(json, "tcpFlowCapacity");
    }

    [Theory]
    [InlineData("\"4096\"")]
    [InlineData("4096.5")]
    public void ConfigurationRejectsNonIntegerTcpFlowCapacityAtParseTime(string value)
    {
        var json = $$"""
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          "tcpFlowCapacity": {{value}}
        }
        """;

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path.Contains("tcpFlowCapacity", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationDefaultsUdpBudgetWhenOmitted()
    {
        Assert.True(ConfigurationLoader.TryParse(Config(""), out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionCapacity, configuration!.UdpSessionCapacity);
        Assert.Equal(ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes, configuration.UdpRelayReceiveBufferBytes);
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionIdleTimeout, configuration.UdpSessionIdleTimeout);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void ConfigurationTreatsExplicitNullUdpBudgetAsDefault()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          "udpSessionCapacity": null,
          "udpRelayReceiveBufferKb": null,
          "udpSessionIdleSeconds": null
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionCapacity, configuration!.UdpSessionCapacity);
        Assert.Equal(ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes, configuration.UdpRelayReceiveBufferBytes);
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionIdleTimeout, configuration.UdpSessionIdleTimeout);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void ConfigurationParsesUdpBudgetValues()
    {
        var json = Config("""
          "udpSessionCapacity": 512,
          "udpRelayReceiveBufferKb": 512,
          "udpSessionIdleSeconds": 45
        """);

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(512, configuration!.UdpSessionCapacity);
        Assert.Equal(512 * 1024, configuration.UdpRelayReceiveBufferBytes);
        Assert.Equal(TimeSpan.FromSeconds(45), configuration.UdpSessionIdleTimeout);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void ConfigurationOverrideRestoresThePreviousRelayReceiveBufferDefault()
    {
        // The configuration key is the documented way back to the historical 128 KiB per-session
        // relay buffer; the default itself is asserted by ConfigurationDefaultsUdpBudgetWhenOmitted.
        var json = Config("\"udpRelayReceiveBufferKb\": 128");

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(128 * 1024, configuration!.UdpRelayReceiveBufferBytes);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void TheRelayReceiveBufferDefaultConstantsAgree()
    {
        // Two constants describe one default: the configuration key's KiB (what composition feeds
        // every relay socket) and the transport's bytes (what a caller taking the transport default
        // directly gets). A drift would size two different sockets from one named default.
        Assert.Equal(ConfigurationLoader.DefaultUdpRelayReceiveBufferKb * 1024, Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize);
        Assert.Equal(Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize, ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes);
    }

    [Theory]
    [InlineData(4097, 256)]
    [InlineData(16384, 1_024)]
    public void ConfigurationWarnsAboveDefaultUdpSessionCapacityWithoutBlocking(int value, int aggregateMiB)
    {
        var json = Config(string.Create(CultureInfo.InvariantCulture, $"\"udpSessionCapacity\": {value}"));

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(value, configuration!.UdpSessionCapacity);
        Assert.Empty(diagnostics);
        var warning = Assert.Single(configuration.Warnings);
        Assert.Equal("udpSessionCapacity", warning.Path);
        // The sentence names the aggregate kernel receive buffer the validated per-session default
        // multiplies into, so a raised capacity running the default buffer is not silent. The
        // expected MiB below are derived from that default and move with it.
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"up to {ConfigurationLoader.DefaultUdpRelayReceiveBufferKb} KiB of kernel receive buffer ({aggregateMiB} MiB at this capacity)"),
            warning.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationAcceptsUdpSessionCapacityAtTheWarningBoundary()
    {
        var json = Config("\"udpSessionCapacity\": 4096");

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(4096, configuration!.UdpSessionCapacity);
        Assert.Empty(configuration.Warnings);
    }

    [Theory]
    [InlineData(2049, 257, true)]
    [InlineData(2048, 257, false)]
    [InlineData(2049, 256, false)]
    public void ConfigurationWarnsOnALargeRelayBufferOnlyAboveBothBoundaries(int capacity, int bufferKb, bool warns)
    {
        // The warning is an aggregate one: the per-session buffer only matters multiplied by the
        // session budget, so it fires exactly when BOTH thresholds are crossed (>256 KiB and
        // >2048 sessions).
        var json = Config(string.Create(CultureInfo.InvariantCulture, $"\"udpSessionCapacity\": {capacity}, \"udpRelayReceiveBufferKb\": {bufferKb}"));

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(bufferKb * 1024, configuration!.UdpRelayReceiveBufferBytes);
        if (warns)
        {
            var warning = Assert.Single(configuration.Warnings);
            Assert.Equal("udpRelayReceiveBufferKb", warning.Path);
        }
        else
        {
            Assert.Empty(configuration.Warnings);
        }
    }

    [Theory]
    [InlineData("udpSessionCapacity", 1)]
    [InlineData("udpSessionCapacity", 16384)]
    [InlineData("udpRelayReceiveBufferKb", 16)]
    [InlineData("udpRelayReceiveBufferKb", 1024)]
    [InlineData("udpSessionIdleSeconds", 5)]
    [InlineData("udpSessionIdleSeconds", 600)]
    [InlineData("udpAssociationMaxPerServer", 1)]
    [InlineData("udpAssociationMaxPerServer", 16384)]
    [InlineData("udpAssociationFlowsPerAssociation", 1)]
    [InlineData("udpAssociationFlowsPerAssociation", 256)]
    public void ConfigurationAcceptsUdpBudgetAtTheSupportedRangeBoundaries(string key, int value)
    {
        var json = Config(string.Create(CultureInfo.InvariantCulture, $"\"{key}\": {value}"));

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(value, key switch
        {
            "udpSessionCapacity" => configuration!.UdpSessionCapacity,
            "udpRelayReceiveBufferKb" => configuration!.UdpRelayReceiveBufferBytes / 1024,
            "udpAssociationMaxPerServer" => configuration!.UdpAssociationMaxPerServer,
            "udpAssociationFlowsPerAssociation" => configuration!.UdpAssociationFlowsPerAssociation,
            _ => (int)configuration!.UdpSessionIdleTimeout.TotalSeconds,
        });
    }

    [Theory]
    [InlineData("udpSessionCapacity", 0)]
    [InlineData("udpSessionCapacity", 16385)]
    [InlineData("udpRelayReceiveBufferKb", 15)]
    [InlineData("udpRelayReceiveBufferKb", 1025)]
    [InlineData("udpSessionIdleSeconds", 4)]
    [InlineData("udpSessionIdleSeconds", 601)]
    [InlineData("udpAssociationMaxPerServer", 0)]
    [InlineData("udpAssociationMaxPerServer", -1)]
    [InlineData("udpAssociationMaxPerServer", 16385)]
    [InlineData("udpAssociationFlowsPerAssociation", 0)]
    [InlineData("udpAssociationFlowsPerAssociation", -1)]
    [InlineData("udpAssociationFlowsPerAssociation", 257)]
    public void ConfigurationRejectsUdpBudgetOutsideSupportedRange(string key, int value)
    {
        // The parsers fall back to their defaults internally, but an out-of-range value is an
        // error: validation rejects the whole configuration (the tcpFlowCapacity contract), so a
        // caller never observes a partially applied budget. The diagnostic path is the contract.
        var json = Config(string.Create(CultureInfo.InvariantCulture, $"\"{key}\": {value}"));

        ConfigurationAssert.Invalid(json, key);
    }

    [Theory]
    [InlineData("udpSessionCapacity", "\"4096\"")]
    [InlineData("udpRelayReceiveBufferKb", "128.5")]
    [InlineData("udpSessionIdleSeconds", "\"30\"")]
    [InlineData("udpAssociationMaxPerServer", "\"1024\"")]
    [InlineData("udpAssociationMaxPerServer", "\"many\"")]
    [InlineData("udpAssociationFlowsPerAssociation", "16.5")]
    [InlineData("udpAssociationFlowsPerAssociation", "\"\"")]
    public void ConfigurationRejectsNonIntegerUdpBudgetAtParseTime(string key, string value)
    {
        var json = Config($"\"{key}\": {value}");

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationDefaultsUdpAssociationHeadWhenOmitted()
    {
        Assert.True(ConfigurationLoader.TryParse(Config(""), out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(ConfigurationLoader.DefaultUdpAssociationMaxPerServer, configuration!.UdpAssociationMaxPerServer);
        Assert.Equal(ConfigurationLoader.DefaultUdpAssociationFlowsPerAssociation, configuration.UdpAssociationFlowsPerAssociation);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void ConfigurationTreatsExplicitNullUdpAssociationHeadAsDefault()
    {
        var json = Config("""
          "udpAssociationMaxPerServer": null,
          "udpAssociationFlowsPerAssociation": null
        """);

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(ConfigurationLoader.DefaultUdpAssociationMaxPerServer, configuration!.UdpAssociationMaxPerServer);
        Assert.Equal(ConfigurationLoader.DefaultUdpAssociationFlowsPerAssociation, configuration.UdpAssociationFlowsPerAssociation);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void ConfigurationParsesUdpAssociationHeadValues()
    {
        var json = Config("""
          "udpAssociationMaxPerServer": 2048,
          "udpAssociationFlowsPerAssociation": 32
        """);

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(2048, configuration!.UdpAssociationMaxPerServer);
        Assert.Equal(32, configuration.UdpAssociationFlowsPerAssociation);
        Assert.Empty(configuration.Warnings);
    }

    /// <summary>The minimal valid configuration with <paramref name="body"/> appended as extra members.</summary>
    private static string Config(string body) => string.IsNullOrEmpty(body)
        ? """
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass"
        }
        """
        : $$"""
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          {{body}}
        }
        """;

    [Fact]
    public void ConfigurationDefaultsUdpAssociationReuseToAutoWhenOmitted()
    {
        Assert.True(ConfigurationLoader.TryParse(Config("\"udpAssociationReuse\": null"), out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(UdpAssociationReuseMode.Auto, configuration!.UdpAssociationReuse);
        Assert.Empty(configuration.Warnings);
    }

    [Theory]
    [InlineData("auto", UdpAssociationReuseMode.Auto)]
    [InlineData("always", UdpAssociationReuseMode.Always)]
    [InlineData("off", UdpAssociationReuseMode.Off)]
    [InlineData("  Always  ", UdpAssociationReuseMode.Always)]
    [InlineData("OFF", UdpAssociationReuseMode.Off)]
    public void ConfigurationParsesUdpAssociationReuseCaseInsensitively(string value, UdpAssociationReuseMode expected)
    {
        var json = Config($"\"udpAssociationReuse\": \"{value}\"");
        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(expected, configuration!.UdpAssociationReuse);
        Assert.Empty(configuration.Warnings);
    }

    [Theory]
    [InlineData("sometimes")]
    [InlineData("")]
    public void ConfigurationRejectsUnknownUdpAssociationReuse(string value)
    {
        ConfigurationAssert.Invalid(Config($"\"udpAssociationReuse\": \"{value}\""), "udpAssociationReuse");
    }

    [Fact]
    public void ExampleConfigurationsAllValidate()
    {
        // The examples/ directory is published documentation; every example must be a valid
        // configuration so operators can copy them without surprises.
        var exampleDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "examples");
        if (!Directory.Exists(exampleDir)) return; // Source tree layout differs in some build hosts.
        var files = Directory.GetFiles(exampleDir, "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            Assert.True(ConfigurationLoader.TryParse(json, out var dto, out var parseErrors), $"{Path.GetFileName(file)} parse: {string.Join("; ", parseErrors)}");
            Assert.True(ConfigurationLoader.TryValidate(dto!, out _, out var validationErrors), $"{Path.GetFileName(file)} validate: {string.Join("; ", validationErrors)}");
        }
    }
}
