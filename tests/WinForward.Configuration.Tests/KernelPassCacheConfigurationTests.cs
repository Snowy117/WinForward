using Xunit;

namespace WinForward.Configuration.Tests;

/// <summary>
/// The <c>KernelPassCache</c> key: a plain opt-in boolean that defaults to off, is spelled in
/// PascalCase under the strict reader, and is a boolean rather than a truthy string.
/// </summary>
public sealed class KernelPassCacheConfigurationTests
{
    private const string MinimalRules = """ "Host": { "FallbackAction": "pass", "Rules": [] } """;

    [Fact]
    public void AnOmittedKeyMeansOff() => Assert.False(Validated(""" { "Socks5Servers": [], %RULES% } """).KernelPassCache);

    [Fact]
    public void AnExplicitTrueEnablesTheCache() => Assert.True(Validated(""" { "Socks5Servers": [], "KernelPassCache": true, %RULES% } """).KernelPassCache);

    [Fact]
    public void AnExplicitFalseKeepsTheCacheOff() => Assert.False(Validated(""" { "Socks5Servers": [], "KernelPassCache": false, %RULES% } """).KernelPassCache);

    [Theory]
    [InlineData("kernelPassCache")]
    [InlineData("kernelpasscache")]
    [InlineData("KernelPasscache")]
    public void AWronglyCasedKeyFailsClosedAtParse(string misspelled)
    {
        var json = $$"""{ "Socks5Servers": [], "{{misspelled}}": true, {{MinimalRules}} }""";

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal($"WinForward.{misspelled}", diagnostic.Path);
    }

    [Fact]
    public void AStringValueFailsClosedAtParse()
    {
        Assert.False(ConfigurationLoader.TryParse($$"""{ "Socks5Servers": [], "KernelPassCache": "true", {{MinimalRules}} }""", out _, out var diagnostics));
        Assert.Single(diagnostics);
    }

    private static ValidatedConfiguration Validated(string jsonTemplate)
    {
        var json = jsonTemplate.Replace("%RULES%", MinimalRules, StringComparison.Ordinal);
        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out var parseDiagnostics), string.Join("; ", parseDiagnostics));
        Assert.NotNull(dto);
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.NotNull(configuration);
        return configuration;
    }
}
