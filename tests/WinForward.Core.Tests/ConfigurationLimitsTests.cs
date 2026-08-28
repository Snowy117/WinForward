using WinForward.Configuration;
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
        var json = $$"""
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          "tcpFlowCapacity": {{value}}
        }
        """;

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
        var json = $$"""
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          "tcpFlowCapacity": {{value}}
        }
        """;

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
        var json = $$"""
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          "tcpFlowCapacity": {{value}}
        }
        """;

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
