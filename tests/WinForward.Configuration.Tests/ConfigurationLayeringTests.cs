using System.Globalization;
using Microsoft.Extensions.Logging;
using WinForward.Runtime.Logging;
using Xunit;

namespace WinForward.Configuration.Tests;

/// <summary>
/// The layered-document contract: <c>appsettings.json</c> beside the executable, then
/// <c>--config</c>, merged as JSON documents (objects key by key, anything else replacing), with at
/// least one source required. The WinForward section and the logging section always come from the
/// one merged document, and nothing else is ever registered as a configuration source.
/// </summary>
public sealed class ConfigurationLayeringTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(Path.GetTempPath(), $"winforward-layering-{Guid.NewGuid():N}");

    public ConfigurationLayeringTests() => Directory.CreateDirectory(_baseDirectory);

    public void Dispose() => Directory.Delete(_baseDirectory, recursive: true);

    [Fact]
    public void AppSettingsBesideTheExecutableIsLoadedWhenPresent()
    {
        Write("appsettings.json", Minimal(tcpFlowCapacity: 512));

        Assert.Equal(ConfigurationLoadOutcome.Loaded, Load(configPath: null, out var loaded, out var diagnostics));
        Assert.Empty(diagnostics);
        Assert.Equal(512, loaded!.Validated.TcpFlowCapacity);
        Assert.Equal(ConfigurationLoader.AppSettingsFileName, Assert.Single(loaded.Sources).Name);
    }

    [Fact]
    public void TheConfigLayerIsAppliedAfterTheExeDirectoryLayer()
    {
        Write("appsettings.json", Minimal(tcpFlowCapacity: 512));
        var configPath = Write("second.json", """{"WinForward":{"TcpFlowCapacity":1024}}""");

        Assert.Equal(ConfigurationLoadOutcome.Loaded, Load(configPath, out var loaded, out _));
        Assert.Equal(1024, loaded!.Validated.TcpFlowCapacity);
        Assert.Equal([ConfigurationLoader.AppSettingsFileName, ConfigurationLoader.ConfigOptionName], loaded.Sources.Select(static source => source.Name));
    }

    /// <summary>
    /// A <c>--config</c> file that supplies a few settings merges over the exe-directory file rather
    /// than replacing it: the base file's targets and rules are still in effect.
    /// </summary>
    [Fact]
    public void APartialConfigLayerMergesOverTheOtherLayer()
    {
        Write("appsettings.json", """
        {
          "WinForward": {
            "Socks5Servers": [{ "Name": "main", "Host": "127.0.0.1", "Port": 1080 }],
            "Host": { "FallbackAction": "pass", "Rules": [{ "Protocol": ["udp"], "Action": "pass" }] }
          }
        }
        """);
        var configPath = Write("second.json", """{"WinForward":{"TcpFlowCapacity":2048}}""");

        Assert.Equal(ConfigurationLoadOutcome.Loaded, Load(configPath, out var loaded, out _));
        Assert.Equal(2048, loaded!.Validated.TcpFlowCapacity);
        Assert.True(loaded.Validated.Targets.ContainsKey("main"));
        Assert.Single(loaded.Validated.Policy.HostRules);
    }

    [Fact]
    public void ObjectsMergeKeyByKeyAndArraysReplaceWholesale()
    {
        Write("appsettings.json", """
        {
          "WinForward": {
            "Socks5Servers": [],
            "Host": {
              "FallbackAction": "pass",
              "Rules": [
                { "Protocol": ["udp"], "Action": "pass" },
                { "Protocol": ["tcp"], "Action": "pass" }
              ]
            }
          }
        }
        """);
        var configPath = Write("second.json", """
        {
          "WinForward": {
            "Host": { "Rules": [{ "RemotePort": ["443"], "Action": "block" }] }
          }
        }
        """);

        Assert.Equal(ConfigurationLoadOutcome.Loaded, Load(configPath, out var loaded, out _));
        var rule = Assert.Single(loaded!.Validated.Policy.HostRules);
        Assert.Equal([(443, 443)], rule.Matcher.RemotePorts);
        Assert.Equal("pass", loaded.Configuration["WinForward:Host:FallbackAction"]);
    }

    [Fact]
    public void AnExplicitNullOverridesTheEarlierLayer()
    {
        Write("appsettings.json", Minimal(tcpFlowCapacity: 512));
        var configPath = Write("second.json", """{"WinForward":{"TcpFlowCapacity":null}}""");

        Assert.Equal(ConfigurationLoadOutcome.Loaded, Load(configPath, out var loaded, out _));
        Assert.Equal(ConfigurationLoader.DefaultTcpFlowCapacity, loaded!.Validated.TcpFlowCapacity);
    }

    [Fact]
    public void TheLoggingSectionIsBoundFromTheMergedDocument()
    {
        Write("appsettings.json", """
        {
          "Logging": { "LogLevel": { "Default": "Warning" } },
          "WinForward": { "Socks5Servers": [], "Host": { "FallbackAction": "pass", "Rules": [] } }
        }
        """);
        var configPath = Write("second.json", """{"Logging":{"LogLevel":{"WinForward.Runtime.FlowDispatcher":"Debug"}}}""");

        Assert.Equal(ConfigurationLoadOutcome.Loaded, Load(configPath, out var loaded, out _));
        var logLevel = loaded!.Configuration.GetSection("Logging:LogLevel");
        Assert.Equal("Warning", logLevel["Default"]);
        Assert.Equal("Debug", logLevel["WinForward.Runtime.FlowDispatcher"]);
        Assert.Equal(LogLevel.Warning, RuntimeLogging.ResolveLogLevel(loaded.Configuration));
    }

    /// <summary>
    /// The logging section is never read from a file of its own: the merged document is served from
    /// one in-memory stream, so a name no operator wrote cannot appear in either section.
    /// </summary>
    [Fact]
    public void TheMergedDocumentIsTheOnlyConfigurationSource()
    {
        Write("appsettings.json", Minimal(tcpFlowCapacity: 512));
        var exampleShapedBait = Write("appsettings.example.json", Minimal(tcpFlowCapacity: 1));

        Assert.Equal(ConfigurationLoadOutcome.Loaded, Load(configPath: null, out var loaded, out _));
        Assert.Equal(512, loaded!.Validated.TcpFlowCapacity);
        Assert.DoesNotContain(exampleShapedBait, loaded.Sources.Select(static source => source.Path));
        Assert.Single(loaded.Configuration.Providers);
    }

    [Fact]
    public void NeitherSourceExistingIsItsOwnOutcome()
    {
        Assert.Equal(ConfigurationLoadOutcome.NoSource, Load(configPath: null, out var loaded, out var diagnostics));
        Assert.Null(loaded);
        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// A path the operator named is not optional: loading the other layer anyway would leave them
    /// running rules they did not write, so the missing file is a configuration error naming it
    /// even though the other source loaded, and no configuration is handed to the caller.
    /// </summary>
    [Fact]
    public void AConfigPathThatDoesNotExistIsAConfigurationErrorEvenWhenAnotherSourceExists()
    {
        Write("appsettings.json", Minimal(tcpFlowCapacity: null));
        var missing = Path.Combine(_baseDirectory, "gone.json");

        Assert.Equal(ConfigurationLoadOutcome.Invalid, Load(missing, out var loaded, out var diagnostics));
        Assert.Null(loaded);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ConfigurationLoader.ConfigOptionName, diagnostic.Path);
        Assert.Contains(missing, diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With nothing else to load there is no configuration at all, which the command reports as its
    /// usage error rather than as a fault of the named file.
    /// </summary>
    [Fact]
    public void AConfigPathThatDoesNotExistWithNothingElseToLoadIsNoSource()
    {
        Assert.Equal(ConfigurationLoadOutcome.NoSource, Load(Path.Combine(_baseDirectory, "gone.json"), out var loaded, out var diagnostics));
        Assert.Null(loaded);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ADocumentWithoutTheWinForwardSectionNamesTheConvention()
    {
        Write("appsettings.json", """{"Logging":{"LogLevel":{"Default":"Information"}}}""");

        Assert.Equal(ConfigurationLoadOutcome.Invalid, Load(configPath: null, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ConfigurationLoader.SectionName, diagnostic.Path);
        Assert.Contains("WinForward", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("PascalCase", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>A pre-migration <c>config.json</c> is exactly this document: no wrapper, camelCase keys.</summary>
    [Theory]
    [InlineData("""{"socks5Servers":[],"host":{"fallbackAction":"pass","rules":[]}}""", "WinForward")]
    [InlineData("""{"WinForward":{"socks5Servers":[]}}""", "WinForward.socks5Servers")]
    public void AStaleCamelCaseDocumentFailsClosedWithItsPath(string json, string expectedPath)
    {
        Write("appsettings.json", json);

        Assert.Equal(ConfigurationLoadOutcome.Invalid, Load(configPath: null, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, expectedPath, StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>IConfiguration</c> is a string store, so the merge runs on the parsed JSON documents
    /// themselves: a value that merely looks numeric keeps its exact text.
    /// </summary>
    [Fact]
    public void NumericLookingValuesKeepTheirExactText()
    {
        Write("appsettings.json", """
        {
          "WinForward": {
            "Socks5Servers": [{ "Name": "007", "Host": "127.0.0.1", "Port": 1080 }],
            "Host": { "FallbackAction": "pass", "Rules": [{ "RemotePort": ["0080"], "Action": "pass" }] }
          }
        }
        """);

        Assert.Equal(ConfigurationLoadOutcome.Loaded, Load(configPath: null, out var loaded, out _));
        Assert.True(loaded!.Validated.Targets.ContainsKey("007"));
        Assert.Equal([(80, 80)], Assert.Single(loaded.Validated.Policy.HostRules).Matcher.RemotePorts);
    }

    [Theory]
    [InlineData("""{"WinForward":{""", "appsettings.json")]
    [InlineData("""[{"WinForward":{}}]""", "appsettings.json")]
    [InlineData("""{"WinForward":{"Socks5Servers":[],"Socks5Servers":[]}}""", "appsettings.json")]
    public void AMalformedDocumentFailsClosedAgainstItsLayer(string json, string expectedSource)
    {
        Write("appsettings.json", json);

        Assert.Equal(ConfigurationLoadOutcome.Invalid, Load(configPath: null, out _, out var diagnostics));
        Assert.Equal(expectedSource, Assert.Single(diagnostics).Path);
    }

    private static string Minimal(int? tcpFlowCapacity) => $$"""
        {
          "WinForward": {
            "Socks5Servers": [],
            "Host": { "FallbackAction": "pass", "Rules": [] },
            "TcpFlowCapacity": {{(tcpFlowCapacity is null ? "null" : tcpFlowCapacity.Value.ToString(CultureInfo.InvariantCulture))}}
          }
        }
        """;

    private ConfigurationLoadOutcome Load(string? configPath, out LoadedConfiguration? loaded, out IReadOnlyList<ConfigDiagnostic> diagnostics) =>
        ConfigurationLoader.TryLoad(_baseDirectory, configPath, out loaded, out diagnostics);

    private string Write(string name, string json)
    {
        var path = Path.Combine(_baseDirectory, name);
        File.WriteAllText(path, json);
        return path;
    }
}
