using System.Globalization;
using WinForward.Runtime.Socks5;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Configuration.Tests;

public sealed class ConfigurationLimitsTests
{
    [Fact]
    public void ConfigurationDefaultsTcpFlowCapacityWhenOmitted()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] }
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(ConfigurationLoader.DefaultTcpFlowCapacity, configuration.TcpFlowCapacity);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void ConfigurationTreatsExplicitNullTcpFlowCapacityAsDefault()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "TcpFlowCapacity": null
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(ConfigurationLoader.DefaultTcpFlowCapacity, configuration.TcpFlowCapacity);
        Assert.Empty(configuration.Warnings);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public void ConfigurationAcceptsTcpFlowCapacityAtOrBelowDefaultWithoutWarning(int value)
    {
        var json = string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "TcpFlowCapacity": {{value}}
        }
        """);

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(value, configuration.TcpFlowCapacity);
        Assert.Empty(configuration.Warnings);
    }

    [Theory]
    [InlineData(4097)]
    [InlineData(8192)]
    public void ConfigurationWarnsAboveDefaultTcpFlowCapacityWithoutBlocking(int value)
    {
        var json = string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "TcpFlowCapacity": {{value}}
        }
        """);

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(value, configuration.TcpFlowCapacity);
        Assert.Empty(diagnostics);
        var warning = Assert.Single(configuration.Warnings);
        Assert.Equal("WinForward.TcpFlowCapacity", warning.Path);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8193)]
    public void ConfigurationRejectsTcpFlowCapacityOutsideSupportedRange(int value)
    {
        var json = string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "TcpFlowCapacity": {{value}}
        }
        """);

        ConfigurationAssert.Invalid(json, "WinForward.TcpFlowCapacity");
    }

    [Theory]
    [InlineData("\"4096\"")]
    [InlineData("4096.5")]
    public void ConfigurationRejectsNonIntegerTcpFlowCapacityAtParseTime(string value)
    {
        var json = $$"""
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "TcpFlowCapacity": {{value}}
        }
        """;

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path.Contains("WinForward.TcpFlowCapacity", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationDefaultsUdpBudgetWhenOmitted()
    {
        Assert.True(ConfigurationLoader.TryParse(Config(""), out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionCapacity, configuration.UdpSessionCapacity);
        Assert.Equal(ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes, configuration.UdpRelayReceiveBufferBytes);
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionIdleTimeout, configuration.UdpSessionIdleTimeout);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void ConfigurationTreatsExplicitNullUdpBudgetAsDefault()
    {
        const string json = """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          "UdpSessionCapacity": null,
          "UdpRelayReceiveBufferKb": null,
          "UdpSessionIdleSeconds": null
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionCapacity, configuration.UdpSessionCapacity);
        Assert.Equal(ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes, configuration.UdpRelayReceiveBufferBytes);
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionIdleTimeout, configuration.UdpSessionIdleTimeout);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void ConfigurationParsesUdpBudgetValues()
    {
        var json = Config("""
          "UdpSessionCapacity": 512,
          "UdpRelayReceiveBufferKb": 512,
          "UdpSessionIdleSeconds": 45
        """);

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(512, configuration.UdpSessionCapacity);
        Assert.Equal(512 * 1024, configuration.UdpRelayReceiveBufferBytes);
        Assert.Equal(TimeSpan.FromSeconds(45), configuration.UdpSessionIdleTimeout);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void ConfigurationOverrideRestoresThePreviousRelayReceiveBufferDefault()
    {
        // The configuration key is the documented way back to the historical 128 KiB per-session
        // relay buffer; the default itself is asserted by ConfigurationDefaultsUdpBudgetWhenOmitted.
        var json = Config("\"UdpRelayReceiveBufferKb\": 128");

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(128 * 1024, configuration.UdpRelayReceiveBufferBytes);
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
        var json = Config(string.Create(CultureInfo.InvariantCulture, $"\"UdpSessionCapacity\": {value}"));

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(value, configuration.UdpSessionCapacity);
        Assert.Empty(diagnostics);
        var warning = Assert.Single(configuration.Warnings);
        Assert.Equal("WinForward.UdpSessionCapacity", warning.Path);
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
        var json = Config("\"UdpSessionCapacity\": 4096");

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(4096, configuration.UdpSessionCapacity);
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
        var json = Config(string.Create(CultureInfo.InvariantCulture, $"\"UdpSessionCapacity\": {capacity}, \"UdpRelayReceiveBufferKb\": {bufferKb}"));

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(bufferKb * 1024, configuration.UdpRelayReceiveBufferBytes);
        if (warns)
        {
            var warning = Assert.Single(configuration.Warnings);
            Assert.Equal("WinForward.UdpRelayReceiveBufferKb", warning.Path);
        }
        else
        {
            Assert.Empty(configuration.Warnings);
        }
    }

    [Theory]
    [InlineData("UdpSessionCapacity", 1)]
    [InlineData("UdpSessionCapacity", 16384)]
    [InlineData("UdpRelayReceiveBufferKb", 16)]
    [InlineData("UdpRelayReceiveBufferKb", 1024)]
    [InlineData("UdpSessionIdleSeconds", 5)]
    [InlineData("UdpSessionIdleSeconds", 600)]
    public void ConfigurationAcceptsUdpBudgetAtTheSupportedRangeBoundaries(string key, int value)
    {
        var json = Config(string.Create(CultureInfo.InvariantCulture, $"\"{key}\": {value}"));

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.Equal(value, key switch
        {
            "UdpSessionCapacity" => configuration.UdpSessionCapacity,
            "UdpRelayReceiveBufferKb" => configuration.UdpRelayReceiveBufferBytes / 1024,
            _ => (int)configuration.UdpSessionIdleTimeout.TotalSeconds,
        });
    }

    [Theory]
    [InlineData("UdpSessionCapacity", 0)]
    [InlineData("UdpSessionCapacity", 16385)]
    [InlineData("UdpRelayReceiveBufferKb", 15)]
    [InlineData("UdpRelayReceiveBufferKb", 1025)]
    [InlineData("UdpSessionIdleSeconds", 4)]
    [InlineData("UdpSessionIdleSeconds", 601)]
    public void ConfigurationRejectsUdpBudgetOutsideSupportedRange(string key, int value)
    {
        // The parsers fall back to their defaults internally, but an out-of-range value is an
        // error: validation rejects the whole configuration (the tcpFlowCapacity contract), so a
        // caller never observes a partially applied budget. The diagnostic path is the contract.
        var json = Config(string.Create(CultureInfo.InvariantCulture, $"\"{key}\": {value}"));

        ConfigurationAssert.Invalid(json, $"WinForward.{key}");
    }

    [Theory]
    [InlineData("UdpSessionCapacity", "\"4096\"")]
    [InlineData("UdpRelayReceiveBufferKb", "128.5")]
    [InlineData("UdpSessionIdleSeconds", "\"30\"")]
    public void ConfigurationRejectsNonIntegerUdpBudgetAtParseTime(string key, string value)
    {
        var json = Config($"\"{key}\": {value}");

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path.Contains(key, StringComparison.Ordinal));
    }

    /// <summary>The minimal valid configuration with <paramref name="body"/> appended as extra members.</summary>
    private static string Config(string body) => string.IsNullOrEmpty(body)
        ? """
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] }
        }
        """
        : $$"""
        {
          "Socks5Servers": [],
          "Host": { "FallbackAction": "pass", "Rules": [] },
          {{body}}
        }
        """;

    [Fact]
    public void ExampleConfigurationsAllValidate()
    {
        // The examples/ directory is published documentation; every example must be a complete
        // configuration an operator can copy without surprises, so it is loaded exactly as a run
        // loads it — wrapper section, layering and strict validation included.
        // A missing source tree means nothing was validated, so it is fatal rather than tolerated.
        var exampleDir = FindRepositoryExamplesDirectory()
            ?? throw new InvalidOperationException($"No WinForward.slnx was found above {AppContext.BaseDirectory}, so the examples were not validated.");
        var files = Directory.GetFiles(exampleDir, "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var outcome = ConfigurationLoader.TryLoad(exampleDir, file, out var loaded, out var diagnostics);
            Assert.True(outcome == ConfigurationLoadOutcome.Loaded, $"{Path.GetFileName(file)}: {outcome}: {string.Join("; ", diagnostics)}");
            Assert.Empty(loaded!.Validated.Warnings);
        }
    }

    /// <summary>
    /// Locates the repository's examples directory from the test host's own location, walking up to
    /// the directory that holds the solution file, so the result never depends on how deep this
    /// project's output directory sits. Null only when no source tree encloses the test host.
    /// </summary>
    private static string? FindRepositoryExamplesDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "WinForward.slnx"))) continue;
            var examples = Path.Combine(directory.FullName, "examples");
            Assert.True(Directory.Exists(examples), $"The repository root at '{directory.FullName}' has no examples directory.");
            return examples;
        }

        return null;
    }
}
