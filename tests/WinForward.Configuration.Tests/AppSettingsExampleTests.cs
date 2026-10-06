using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using WinForward.Runtime.Logging;
using Xunit;

namespace WinForward.Configuration.Tests;

/// <summary>
/// The shipped <c>appsettings.example.json</c>: it is loaded by nothing, so these tests are what
/// keeps it honest. It must parse and validate as a whole configuration, and the defaults it shows
/// must be the ones the code applies.
/// </summary>
public sealed class AppSettingsExampleTests
{
    private const string ExampleFileName = "appsettings.example.json";

    [Fact]
    public void TheShippedExampleValidatesUnderTheStrictReader()
    {
        var outcome = ConfigurationLoader.TryLoad(ExampleDirectory(), ExamplePath(), out var loaded, out var diagnostics);

        Assert.True(outcome == ConfigurationLoadOutcome.Loaded, $"{outcome}: {string.Join("; ", diagnostics)}");
        Assert.Empty(loaded!.Validated.Warnings);
        // Nothing loads the example on its own: it is a source only when an operator points at it.
        var source = Assert.Single(loaded.Sources);
        Assert.Equal(ConfigurationLoader.ConfigOptionName, source.Name);
        Assert.Equal(ExamplePath(), source.Path);
    }

    /// <summary>
    /// Every value the example writes produces exactly the default the code would apply without it,
    /// so copying the file changes nothing an operator did not ask for.
    /// </summary>
    [Fact]
    public void TheShippedExampleShowsTheCodeDefaults()
    {
        Assert.Equal(ConfigurationLoadOutcome.Loaded, ConfigurationLoader.TryLoad(ExampleDirectory(), ExamplePath(), out var loaded, out _));
        var validated = loaded!.Validated;

        Assert.Equal(ConfigurationLoader.DefaultTcpFlowCapacity, validated.TcpFlowCapacity);
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionCapacity, validated.UdpSessionCapacity);
        Assert.Equal(ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes, validated.UdpRelayReceiveBufferBytes);
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionIdleTimeout, validated.UdpSessionIdleTimeout);
        // 0 is the auto sentinel: the example documents the key as null rather than naming a worker
        // count the operator would then be stuck with.
        Assert.Equal(0, validated.SetupWorkerCount);
    }

    /// <summary>
    /// The logging section of the example is the default posture: the standard level name, and no
    /// formatter, which is what leaves the automatic rule in charge.
    /// </summary>
    [Fact]
    public void TheShippedExampleDocumentsTheDefaultLoggingPosture()
    {
        Assert.Equal(ConfigurationLoadOutcome.Loaded, ConfigurationLoader.TryLoad(ExampleDirectory(), ExamplePath(), out var loaded, out _));
        var logging = loaded!.Configuration.GetSection(RuntimeLogging.LoggingSectionName);

        Assert.Equal(nameof(LogLevel.Information), logging.GetSection(RuntimeLogging.LogLevelSectionName)["Default"]);
        Assert.Null(logging.GetSection(RuntimeLogging.ConsoleSectionName)[RuntimeLogging.FormatterNameKey]);
        Assert.Equal(LogLevel.Information, RuntimeLogging.ResolveLogLevel(loaded.Configuration));
        Assert.Equal(ConsoleFormatterNames.Json, RuntimeLogging.ResolveFormatterName(loaded.Configuration, errorRedirected: true));
        Assert.Equal(ConsoleFormatterNames.Simple, RuntimeLogging.ResolveFormatterName(loaded.Configuration, errorRedirected: false));
    }

    private static string ExampleDirectory() => Path.GetDirectoryName(ExamplePath())!;

    private static string ExamplePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "WinForward.slnx"))) continue;
            var example = Path.Combine(directory.FullName, "src", "WinForward.Cli", ExampleFileName);
            Assert.True(File.Exists(example), $"The repository root at '{directory.FullName}' has no {ExampleFileName}.");
            return example;
        }

        throw new InvalidOperationException($"No WinForward.slnx was found above {AppContext.BaseDirectory}, so the example was not validated.");
    }
}
