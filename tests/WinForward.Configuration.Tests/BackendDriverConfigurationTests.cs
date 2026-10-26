using System.Globalization;
using WinForward.Core;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Configuration.Tests;

/// <summary>
/// The <c>WinForward.BackendDriver</c> section and the WinForward.Kernel capability gate: provider
/// selection, the provider-scoped objects, the legacy top-level <c>KernelPassCache</c> alias, and the
/// diagnostic path of every configuration shape the Kernel backend cannot serve in this milestone.
/// </summary>
public sealed class BackendDriverConfigurationTests
{
    private const string Kernel = """{ "Provider": "WinForward.Kernel" }""";
    private const string WinpkFilter = """{ "Provider": "WinpkFilter" }""";
    private const string PassHost = """{ "FallbackAction": "pass", "Rules": [] }""";
    private const string CatchAllProxy = """{ "Action": "proxy", "Target": "main" }""";

    /// <summary>The host rule blob's process-selector limit, restated here as the contract under test.</summary>
    private const int ProcessSelectorLimitBytes = 511;

    [Fact]
    public void AnOmittedSectionSelectsWinpkFilterWithTheCodeDefaults()
    {
        var configuration = Validated(Configuration(PassHost));

        Assert.Equal(BackendProvider.WinpkFilter, configuration.BackendDriver.Provider);
        Assert.Equal("WinpkFilter", configuration.BackendDriver.ProviderName);
        Assert.False(configuration.BackendDriver.KernelPassCache);
        Assert.Equal("wfprobe", configuration.BackendDriver.DriverServiceName);
        Assert.False(configuration.KernelPassCache);
        Assert.Empty(configuration.Warnings);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "Provider": null }""")]
    [InlineData("""{ "WinpkFilter": {} }""")]
    public void AnOmittedProviderMeansWinpkFilter(string backendDriver)
    {
        var configuration = Validated(Configuration(PassHost, backendDriver: backendDriver));

        Assert.Equal(BackendDriverConfiguration.Default, configuration.BackendDriver);
    }

    [Fact]
    public void AnOmittedSectionMatchesAnExplicitWinpkFilterSelection()
    {
        var omitted = Validated(Configuration(PassHost));
        var selected = Validated(Configuration(PassHost, backendDriver: WinpkFilter));

        Assert.Equal(omitted.BackendDriver, selected.BackendDriver);
        Assert.Equal(omitted.KernelPassCache, selected.KernelPassCache);
    }

    [Theory]
    [InlineData("WinpkFilter", BackendProvider.WinpkFilter, "WinpkFilter")]
    [InlineData("winpkfilter", BackendProvider.WinpkFilter, "WinpkFilter")]
    [InlineData("WINPKFILTER", BackendProvider.WinpkFilter, "WinpkFilter")]
    [InlineData("WinForward.Kernel", BackendProvider.WinForwardKernel, "WinForward.Kernel")]
    [InlineData("winforward.kernel", BackendProvider.WinForwardKernel, "WinForward.Kernel")]
    [InlineData("WinForward.KERNEL", BackendProvider.WinForwardKernel, "WinForward.Kernel")]
    public void ProviderValuesAreMatchedCaseInsensitivelyAndReportTheirCanonicalName(string raw, BackendProvider expected, string canonicalName)
    {
        var configuration = Validated(Configuration(PassHost, backendDriver: $$"""{ "Provider": "{{raw}}" }"""));

        Assert.Equal(expected, configuration.BackendDriver.Provider);
        Assert.Equal(canonicalName, configuration.BackendDriver.ProviderName);
    }

    [Theory]
    [InlineData("Kernel")]
    [InlineData("WinForwardKernel")]
    [InlineData("winforward-kernel")]
    [InlineData(" WinForward . Kernel ")]
    [InlineData("")]
    public void AnUnknownProviderValueFailsAtItsPath(string raw)
    {
        ConfigurationAssert.Invalid(Configuration(PassHost, backendDriver: $$"""{ "Provider": "{{raw}}" }"""), "WinForward.BackendDriver.Provider");
    }

    [Theory]
    [InlineData("""{ "Provider": "WinpkFilter", "Unexpected": true }""", "WinForward.BackendDriver.Unexpected")]
    [InlineData("""{ "WinpkFilter": { "KernelPassCache": false, "Unexpected": true } }""", "WinForward.BackendDriver.WinpkFilter.Unexpected")]
    [InlineData("""{ "Provider": "WinForward.Kernel", "WinForwardKernel": { "DriverServiceName": "wfprobe", "Unexpected": true } }""", "WinForward.BackendDriver.WinForwardKernel.Unexpected")]
    public void AnUnknownNestedKeyFailsClosedAtParse(string backendDriver, string expectedPath)
    {
        Assert.False(ConfigurationLoader.TryParse(Configuration(PassHost, backendDriver: backendDriver), out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(expectedPath, diagnostic.Path);
    }

    [Fact]
    public void TheKernelObjectIsRejectedUnderTheWinpkFilterProvider()
    {
        SingleError(
            Configuration(PassHost, backendDriver: """{ "WinForwardKernel": { "DriverServiceName": "wfprobe" } }"""),
            "WinForward.BackendDriver.WinForwardKernel");
    }

    [Fact]
    public void TheWinpkFilterObjectIsRejectedUnderTheKernelProvider()
    {
        SingleError(
            Configuration(PassHost, backendDriver: """{ "Provider": "WinForward.Kernel", "WinpkFilter": { "KernelPassCache": false } }"""),
            "WinForward.BackendDriver.WinpkFilter");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheNestedKernelPassCacheIsUsedWithoutAWarning(bool value)
    {
        var json = Configuration(PassHost, backendDriver: $$"""{ "WinpkFilter": { "KernelPassCache": {{(value ? "true" : "false")}} } }""");

        var configuration = Validated(json);

        Assert.Equal(value, configuration.KernelPassCache);
        Assert.Equal(value, configuration.BackendDriver.KernelPassCache);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void TheLegacyTopLevelKernelPassCacheIsUsedWithAMigrationWarning()
    {
        var configuration = Validated(Configuration(PassHost, extras: "\"KernelPassCache\": true"));

        Assert.True(configuration.KernelPassCache);
        Assert.True(configuration.BackendDriver.KernelPassCache);
        var warning = Assert.Single(configuration.Warnings);
        Assert.Equal("WinForward.KernelPassCache", warning.Path);
        Assert.Contains("WinForward.BackendDriver.WinpkFilter.KernelPassCache", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingBothKernelPassCacheLocationsIsAnAmbiguityError()
    {
        SingleError(
            Configuration(PassHost, backendDriver: """{ "WinpkFilter": { "KernelPassCache": true } }""", extras: "\"KernelPassCache\": false"),
            "WinForward.KernelPassCache");
    }

    [Fact]
    public void TheLegacyKernelPassCacheIsRejectedUnderTheKernelProvider()
    {
        SingleError(
            Configuration(PassHost, backendDriver: Kernel, extras: "\"KernelPassCache\": false"),
            "WinForward.KernelPassCache");
    }

    [Fact]
    public void NestedAndLegacyKernelPassCacheResolveToTheSameRuntimeValue()
    {
        var nested = Validated(Configuration(PassHost, backendDriver: """{ "WinpkFilter": { "KernelPassCache": true } }"""));
        var legacy = Validated(Configuration(PassHost, extras: "\"KernelPassCache\": true"));

        Assert.True(nested.KernelPassCache);
        Assert.Equal(nested.KernelPassCache, legacy.KernelPassCache);
        Assert.Equal(nested.BackendDriver.KernelPassCache, legacy.BackendDriver.KernelPassCache);
    }

    [Fact]
    public void ADirectlyConstructedConfigurationDefaultsToWinpkFilter()
    {
        var configuration = new ValidatedConfiguration(
            new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));

        Assert.Equal(BackendDriverConfiguration.Default, configuration.BackendDriver);
        Assert.Equal(BackendProvider.WinpkFilter, configuration.BackendDriver.Provider);
    }

    [Fact]
    public void TheKernelProviderDefaultsTheDriverServiceNameToWfprobe()
    {
        Assert.Equal("wfprobe", Validated(Configuration(PassHost, backendDriver: Kernel)).BackendDriver.DriverServiceName);
    }

    [Fact]
    public void TheKernelProviderReadsAndTrimsTheDriverServiceName()
    {
        var configuration = Validated(Configuration(
            PassHost,
            backendDriver: """{ "Provider": "WinForward.Kernel", "WinForwardKernel": { "DriverServiceName": "  wfprobe-test  " } }"""));

        Assert.Equal("wfprobe-test", configuration.BackendDriver.DriverServiceName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyDriverServiceNameFailsAtItsPath(string value)
    {
        SingleError(
            Configuration(PassHost, backendDriver: $$"""{ "Provider": "WinForward.Kernel", "WinForwardKernel": { "DriverServiceName": "{{value}}" } }"""),
            "WinForward.BackendDriver.WinForwardKernel.DriverServiceName");
    }

    [Fact]
    public void AHostAdapterIdSelectorIsRejectedUnderTheKernelProvider()
    {
        var diagnostic = SingleError(
            Configuration(Host("""{ "AdapterId": ["{11111111-2222-3333-4444-555555555555}"], "Action": "pass" }"""), backendDriver: Kernel),
            "WinForward.Host.Rules[0].AdapterId");

        Assert.Contains("WinForward.Kernel", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostAdapterNameSelectorIsRejectedUnderTheKernelProvider()
    {
        SingleError(
            Configuration(Host("""{ "AdapterName": ["vEthernet (MyVM)"], "Action": "pass" }"""), backendDriver: Kernel),
            "WinForward.Host.Rules[0].AdapterName");
    }

    [Fact]
    public void ACatchAllProxyRuleIsRejectedUnderTheKernelProvider()
    {
        var diagnostic = SingleError(Configuration(Host(CatchAllProxy), backendDriver: Kernel), "WinForward.Host.Rules[0].Protocol");

        Assert.Contains("host UDP proxy is not available yet", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"udp\"")]
    [InlineData("\"tcp\", \"udp\"")]
    public void AProxyRuleThatSelectsUdpIsRejectedUnderTheKernelProvider(string protocols)
    {
        var rule = $$"""{ "Protocol": [{{protocols}}], "Action": "proxy", "Target": "main" }""";

        var diagnostic = SingleError(Configuration(Host(rule), backendDriver: Kernel), "WinForward.Host.Rules[0].Protocol");

        Assert.Contains("cannot preserve the original destination as its source", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("unconnected client would see the proxy listener", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("the driver-side reply injection is pending", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Provider = WinpkFilter for UDP", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATcpOnlyProxyRuleValidatesUnderTheKernelProvider()
    {
        var configuration = Validated(Configuration(
            Host("""{ "Protocol": ["tcp"], "Action": "proxy", "Target": "main" }"""),
            backendDriver: Kernel));

        Assert.Equal(BackendProvider.WinForwardKernel, configuration.BackendDriver.Provider);
        Assert.Equal(FlowAction.Proxy, Assert.Single(configuration.Policy.HostRules).Decision.Action);
        Assert.Empty(configuration.Warnings);
    }

    [Fact]
    public void PassAndBlockRulesMayMatchUdpUnderTheKernelProvider()
    {
        var configuration = Validated(Configuration(
            Host("""{ "Protocol": ["udp"], "Action": "pass" }, { "Protocol": ["udp", "tcp"], "Action": "block" }"""),
            backendDriver: Kernel));

        Assert.Equal(2, configuration.Policy.HostRules.Count);
    }

    [Fact]
    public void CapabilityDiagnosticsNameTheOffendingRuleIndex()
    {
        SingleError(
            Configuration(
                Host("""{ "RemotePort": ["80"], "Action": "pass" }, { "Protocol": ["udp"], "Action": "proxy", "Target": "main" }"""),
                backendDriver: Kernel),
            "WinForward.Host.Rules[1].Protocol");
    }

    [Fact]
    public void ForwardedRulesAreRejectedUnderTheKernelProvider()
    {
        const string forwarded = """
            "Forwarded": { "Rules": [{ "Action": "pass" }, { "Action": "block" }] }
            """;

        var diagnostic = SingleError(Configuration(PassHost, backendDriver: Kernel, extras: forwarded), "WinForward.Forwarded");

        Assert.Contains("2 forwarded rule(s)", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonDefaultForwardedFallbackIsRejectedUnderTheKernelProvider()
    {
        const string forwarded = """
            "Forwarded": { "FallbackAction": "block", "Rules": [] }
            """;

        SingleError(Configuration(PassHost, backendDriver: Kernel, extras: forwarded), "WinForward.Forwarded");
    }

    [Fact]
    public void AProcessSelectorOfExactly511Utf8BytesIsAcceptedUnderTheKernelProvider()
    {
        var selector = new string('a', ProcessSelectorLimitBytes);

        Assert.Empty(Validated(Configuration(Host(ProcessPass(selector)), backendDriver: Kernel)).Warnings);
    }

    [Fact]
    public void AProcessSelectorOver511Utf8BytesIsRejectedWithItsTextUnderTheKernelProvider()
    {
        var selector = new string('a', ProcessSelectorLimitBytes + 1);

        var diagnostic = SingleError(Configuration(Host(ProcessPass(selector)), backendDriver: Kernel), "WinForward.Host.Rules[0].Process");

        Assert.Contains(selector, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProcessSelectorLimitCountsUtf8BytesNotCharacters()
    {
        // 256 two-byte characters: well under 511 characters, 512 UTF-8 bytes.
        var selector = new string('\u00e9', 256);

        SingleError(Configuration(Host(ProcessPass(selector)), backendDriver: Kernel), "WinForward.Host.Rules[0].Process");
    }

    /// <summary>
    /// The host UDP coordinator is not started while the host UDP proxy is disabled, so a non-default
    /// UDP budget is still inert under the Kernel provider and must be reported as such rather than
    /// silently ignored; all three settings keep their meaning under WinpkFilter.
    /// </summary>
    [Fact]
    public void NonDefaultUdpSettingsWarnAsInertUnderTheKernelProvider()
    {
        const string overrides = """
            "UdpSessionCapacity": 512, "UdpRelayReceiveBufferKb": 128, "UdpSessionIdleSeconds": 45
            """;

        var configuration = Validated(Configuration(PassHost, backendDriver: Kernel, extras: overrides));

        Assert.Equal(3, configuration.Warnings.Count);
        Assert.Contains(configuration.Warnings, warning => string.Equals(warning.Path, "WinForward.UdpSessionCapacity", StringComparison.Ordinal));
        Assert.Contains(configuration.Warnings, warning => string.Equals(warning.Path, "WinForward.UdpRelayReceiveBufferKb", StringComparison.Ordinal));
        Assert.Contains(configuration.Warnings, warning => string.Equals(warning.Path, "WinForward.UdpSessionIdleSeconds", StringComparison.Ordinal));
        Assert.All(configuration.Warnings, warning => Assert.Contains("inert", warning.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void UdpSettingsWrittenAtTheirDefaultsDoNotWarnUnderTheKernelProvider()
    {
        var overrides = string.Create(
            CultureInfo.InvariantCulture,
            $"\"UdpRelayReceiveBufferKb\": {ConfigurationLoader.DefaultUdpRelayReceiveBufferKb}, \"UdpSessionIdleSeconds\": {(int)ConfigurationLoader.DefaultUdpSessionIdleTimeout.TotalSeconds}");

        Assert.Empty(Validated(Configuration(PassHost, backendDriver: Kernel, extras: overrides)).Warnings);
    }

    /// <summary>
    /// The default capacity is already above the WinpkFilter headroom threshold, so writing it
    /// explicitly keeps that pre-existing warning; the point here is that it is not also called inert.
    /// </summary>
    [Fact]
    public void AnExplicitlyDefaultUdpSessionCapacityKeepsOnlyTheCapacityHeadroomWarning()
    {
        var overrides = string.Create(
            CultureInfo.InvariantCulture,
            $"\"UdpSessionCapacity\": {ConfigurationLoader.DefaultUdpSessionCapacity}");

        var configuration = Validated(Configuration(PassHost, backendDriver: Kernel, extras: overrides));

        var warning = Assert.Single(configuration.Warnings);
        Assert.Equal("WinForward.UdpSessionCapacity", warning.Path);
        Assert.DoesNotContain("inert", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCapabilityGateLeavesWinpkFilterConfigurationsAlone()
    {
        var host = Host("""{ "AdapterName": ["vEthernet (MyVM)"], "RemoteCidr": ["10.0.0.0/8"], "Action": "proxy", "Target": "main" }""");
        const string forwarded = """
            "Forwarded": { "Rules": [{ "AdapterName": ["vEthernet (MyVM)"], "Action": "proxy", "Target": "main" }] }
            """;

        var configuration = Validated(Configuration(host, extras: forwarded));

        Assert.Equal(BackendProvider.WinpkFilter, configuration.BackendDriver.Provider);
        Assert.Single(configuration.Policy.ForwardedRules);
        Assert.Empty(configuration.Warnings);
    }

    private static string ProcessPass(string selector) => $$"""{ "Process": ["{{selector}}"], "Action": "pass" }""";

    private static string Host(string rules) => $$"""{ "FallbackAction": "pass", "Rules": [{{rules}}] }""";

    /// <summary>
    /// The minimal valid section: one SOCKS5 target for the proxy rules, an optional
    /// <c>BackendDriver</c> object, the host domain, and optional extra members appended last.
    /// </summary>
    private static string Configuration(string host, string? backendDriver = null, string? extras = null)
    {
        var driver = backendDriver is null ? string.Empty : $"  \"BackendDriver\": {backendDriver},\n";
        var tail = extras is null ? string.Empty : $",\n  {extras}";
        return $$"""
            {
              "Socks5Servers": [{ "Name": "main", "Host": "127.0.0.1", "Port": 1080 }],
              {{driver}}  "Host": {{host}}{{tail}}
            }
            """;
    }

    private static ValidatedConfiguration Validated(string json)
    {
        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out var parseDiagnostics), Describe(parseDiagnostics));
        Assert.NotNull(dto);
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), Describe(diagnostics));
        Assert.NotNull(configuration);
        return configuration;
    }

    /// <summary>Asserts the document fails validation with exactly one diagnostic at <paramref name="expectedPath"/>.</summary>
    private static ConfigDiagnostic SingleError(string json, string expectedPath)
    {
        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out var parseDiagnostics), Describe(parseDiagnostics));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(expectedPath, diagnostic.Path);
        return diagnostic;
    }

    private static string Describe(IEnumerable<ConfigDiagnostic> diagnostics) => string.Join("; ", diagnostics);
}
