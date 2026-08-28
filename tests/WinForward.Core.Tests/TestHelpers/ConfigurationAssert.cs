using WinForward.Configuration;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// Shared assertion for configuration documents that must parse successfully but fail
/// validation, asserting the exact diagnostic paths produced by <see cref="ConfigurationLoader"/>.
/// </summary>
internal static class ConfigurationAssert
{
    internal static void Invalid(string json, params string[] expectedPaths)
    {
        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        foreach (var path in expectedPaths) Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, path, StringComparison.Ordinal));
    }
}
