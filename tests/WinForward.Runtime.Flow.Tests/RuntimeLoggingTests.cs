using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.Logging;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Runtime.Flow.Tests;

public sealed class RuntimeLoggingTests
{
    [Theory]
    [InlineData(LogLevel.Trace, true, true, true, true, true)]
    [InlineData(LogLevel.Debug, false, true, true, true, true)]
    [InlineData(LogLevel.Information, false, false, true, true, true)]
    [InlineData(LogLevel.Warning, false, false, false, true, true)]
    [InlineData(LogLevel.Error, false, false, false, false, true)]
    public void LoggerFactoryThresholdAdmitsEveryLevelAtOrAboveIt(LogLevel threshold, bool trace, bool debug, bool information, bool warning, bool error)
    {
        var logger = new RecordingLogger();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(threshold);
            builder.AddProvider(new RecordingLoggerProvider(logger));
        });
        var product = factory.CreateLogger<FlowDispatcher>();

        LoggerThresholdProbe.Trace(product);
        LoggerThresholdProbe.Debug(product);
        LoggerThresholdProbe.Information(product);
        LoggerThresholdProbe.Warning(product);
        LoggerThresholdProbe.Error(product);

        Assert.Equal(trace, Recorded(logger, "probe.trace"));
        Assert.Equal(debug, Recorded(logger, "probe.debug"));
        Assert.Equal(information, Recorded(logger, "probe.information"));
        Assert.Equal(warning, Recorded(logger, "probe.warning"));
        Assert.Equal(error, Recorded(logger, "probe.error"));
    }

    [Fact]
    public void FlowTableInitializationReportsItsCapacitiesAtInformation()
    {
        var logger = new RecordingLogger();
        var configuration = new ValidatedConfiguration(
            new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass),
            SetupWorkerCount: 4);

        _ = new FlowDispatcher(configuration, new FakeGuard(), new RecordingExecutor(), flowCapacity: 4_096, logger: logger);

        var (level, name, fields) = Assert.Single(logger.Events);
        Assert.Equal(LogLevel.Information, level);
        Assert.Equal("flow.table.initialized", name);
        Assert.Equal(4_096, fields.Single(field => string.Equals(field.Key, "FlowCapacity", StringComparison.Ordinal)).Value);
        Assert.Equal(4, fields.Single(field => string.Equals(field.Key, "SetupWorkers", StringComparison.Ordinal)).Value);
    }

    [Fact]
    public async Task DispatcherPropagatesFlowGenerationAndEmitsTerminalTrace()
    {
        var logger = new RecordingLogger();
        var executor = new RecordingExecutor();
        var configuration = new ValidatedConfiguration(
            new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        var dispatcher = new FlowDispatcher(configuration, new FakeGuard(), executor, logger: logger);
        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 50000),
            Endpoint.From(IPAddress.Parse("198.51.100.20"), 443),
            TransportProtocol.Tcp,
            FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), FlowBuilders.Context(key, "browser.exe", @"C:\Users\test\browser.exe", "adapter-id", "Ethernet"), PacketSequence: 17);

        await dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.NotNull(executor.LastPacket);
        Assert.Equal(17, executor.LastPacket.Value.PacketSequence);
        Assert.True(executor.LastPacket.Value.FlowGeneration > 0);
        var created = Assert.Single(logger.Events, entry => string.Equals(entry.Name, "flow.created", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, created.Level);
        Assert.Equal(1L, created.Field("Flow"));
        Assert.Equal("browser.exe", created.Field("Process"));
        Assert.Null(created.Field("ProcessPath"));
        var completed = Assert.Single(logger.Events, entry => string.Equals(entry.Name, "packet.completed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Trace, completed.Level);
        Assert.Equal(17L, completed.Field("Packet"));
        Assert.Equal(1L, completed.Field("Flow"));
        Assert.Equal(PacketDisposition.Pass, completed.Field("Disposition"));
        // Full process paths are loggable only under a path-based process selector.
        Assert.DoesNotContain(logger.Events, entry => entry.Fields.Any(field => field.Value is string text && text.Contains(@"C:\Users\test", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DispatcherIncludesProcessPathOnlyWhenPathPolicyRequiresIt()
    {
        var logger = new RecordingLogger();
        var configuration = new ValidatedConfiguration(
            new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass),
            IncludeProcessPathInLogs: true);
        var dispatcher = new FlowDispatcher(configuration, new FakeGuard(), new RecordingExecutor(), logger: logger);
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 50000), Endpoint.From(IPAddress.Parse("198.51.100.20"), 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), FlowBuilders.Context(key, "browser.exe", @"C:\Apps\browser.exe"));

        await dispatcher.DispatchAsync(packet, CancellationToken.None);

        var created = Assert.Single(logger.Events, entry => string.Equals(entry.Name, "flow.created", StringComparison.Ordinal));
        Assert.Equal("browser.exe", created.Field("Process"));
        Assert.Equal(@"C:\Apps\browser.exe", created.Field("ProcessPath"));
    }

    [Fact]
    public void FormatterDefaultsToJsonWhenStderrIsRedirectedAndToSimpleOnATerminal()
    {
        var unconfigured = TestLoggingConfiguration.From();

        Assert.Equal(ConsoleFormatterNames.Json, RuntimeLogging.ResolveFormatterName(unconfigured, errorRedirected: true));
        Assert.Equal(ConsoleFormatterNames.Simple, RuntimeLogging.ResolveFormatterName(unconfigured, errorRedirected: false));

        var configured = TestLoggingConfiguration.From(("Logging:Console:FormatterName", ConsoleFormatterNames.Simple));

        Assert.Equal(ConsoleFormatterNames.Simple, RuntimeLogging.ResolveFormatterName(configured, errorRedirected: true));
    }

    [Fact]
    public void TheConfigurationFiltersTheDefaultLevelAndAFullyQualifiedCategory()
    {
        using var factory = RuntimeLogging.CreateLoggerFactory(TestLoggingConfiguration.From(
            ("Logging:LogLevel:Default", "Error"),
            ("Logging:LogLevel:WinForward.Runtime.FlowDispatcher", "Warning")));

        Assert.True(factory.CreateLogger<FlowDispatcher>().IsEnabled(LogLevel.Warning));
        Assert.False(factory.CreateLogger<FlowDispatcher>().IsEnabled(LogLevel.Information));
        Assert.False(factory.CreateLogger<RuntimeLoggingTests>().IsEnabled(LogLevel.Warning));
        Assert.True(factory.CreateLogger<RuntimeLoggingTests>().IsEnabled(LogLevel.Error));
    }

    [Fact]
    public void AnAbsentLevelOrFormatterNameIsNotAnError()
    {
        Assert.True(RuntimeLogging.TryValidate(TestLoggingConfiguration.From(), out var diagnostics));
        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// The retired <c>logLevel</c> tokens are the likeliest migration mistake, and MEL reports them
    /// when the factory is built rather than as a diagnostic. They stay rejected, with the standard
    /// spelling named.
    /// </summary>
    [Theory]
    [InlineData("info")]
    [InlineData("warn")]
    [InlineData("INFO")]
    [InlineData("Verbose")]
    [InlineData("")]
    public void TheRetiredLevelVocabularyIsRejectedWithTheAcceptedNames(string value)
    {
        Assert.False(RuntimeLogging.TryValidate(TestLoggingConfiguration.From(("Logging:LogLevel:Default", value)), out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("Logging.LogLevel.Default", diagnostic.Path);
        Assert.Contains($"'{value}'", diagnostic.Message, StringComparison.Ordinal);
        foreach (var accepted in RuntimeLogging.AcceptedLogLevelNames) Assert.Contains(accepted, diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Trace")]
    [InlineData("Debug")]
    [InlineData("Information")]
    [InlineData("information")]
    [InlineData("Warning")]
    [InlineData("Error")]
    [InlineData("Critical")]
    [InlineData("None")]
    public void EveryAcceptedLevelNamePasses(string value)
    {
        Assert.Contains(value, RuntimeLogging.AcceptedLogLevelNames, StringComparer.OrdinalIgnoreCase);
        Assert.True(RuntimeLogging.TryValidate(TestLoggingConfiguration.From(("Logging:LogLevel:Default", value)), out var diagnostics));
        Assert.Empty(diagnostics);
    }

    /// <summary>A name MEL cannot resolve is dropped in silence, so it is reported here instead.</summary>
    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    public void AnUnknownFormatterNameIsRejectedWithTheRegisteredNames(string value)
    {
        Assert.False(RuntimeLogging.TryValidate(TestLoggingConfiguration.From(("Logging:Console:FormatterName", value)), out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("Logging.Console.FormatterName", diagnostic.Path);
        foreach (var accepted in RuntimeLogging.AcceptedFormatterNames) Assert.Contains(accepted, diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The accepted list names the formatters this build registers: a name dropped from it makes a
    /// shipped formatter unselectable, which the shape theory alone would only lose a case over.
    /// </summary>
    [Fact]
    public void EveryFormatterTheConsoleRegistrationProvidesIsAccepted()
    {
        var services = new ServiceCollection();
        services.AddLogging(static builder => builder.AddConsole());
        using var provider = services.BuildServiceProvider();

        var registered = provider.GetServices<ConsoleFormatter>()
            .Select(static formatter => formatter.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(RuntimeLogging.AcceptedFormatterNames.Order(StringComparer.Ordinal), registered);
    }

    private static bool Recorded(RecordingLogger logger, string eventName) =>
        logger.Events.Any(entry => string.Equals(entry.Name, eventName, StringComparison.Ordinal));

    private sealed class RecordingExecutor : IPacketActionExecutor
    {
        public CapturedFlowPacket? LastPacket { get; private set; }
        public ValueTask PassAsync(CapturedFlowPacket packet) { LastPacket = packet; return ValueTask.CompletedTask; }
        public ValueTask BlockAsync(CapturedFlowPacket packet) { LastPacket = packet; return ValueTask.CompletedTask; }
        public ValueTask ProxyAsync(CapturedFlowPacket packet, ProxyTarget target, CancellationToken cancellationToken) { LastPacket = packet; return ValueTask.CompletedTask; }
    }
}

internal static class TestLoggingConfiguration
{
    internal static IConfigurationRoot From(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(static setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
}

internal static partial class LoggerThresholdProbe
{
    [LoggerMessage(Level = LogLevel.Trace, EventName = "probe.trace", Message = "Probe at trace.")]
    public static partial void Trace(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, EventName = "probe.debug", Message = "Probe at debug.")]
    public static partial void Debug(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, EventName = "probe.information", Message = "Probe at information.")]
    public static partial void Information(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "probe.warning", Message = "Probe at warning.")]
    public static partial void Warning(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, EventName = "probe.error", Message = "Probe at error.")]
    public static partial void Error(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "probe.escaped", Message = "Probe at warning: 工具+路径.")]
    public static partial void Escaped(ILogger logger);
}

[CollectionDefinition("console-shape", DisableParallelization = true)]
public sealed class ConsoleShapeCollection;

/// <summary>
/// The end-to-end console contract: the factory the product actually builds, writing through the
/// provider the product actually registers, with the process console captured. Serialized into a
/// non-parallel collection because it redirects <see cref="Console.Error"/> and
/// <see cref="Console.Out"/>, which are process-wide.
/// </summary>
[Collection("console-shape")]
public sealed class RuntimeConsoleShapeTests
{
    [Fact]
    public void RealFactoryWritesTheTimestampedRecordToStderrAndNothingToStdout()
    {
        var (output, error) = CaptureRecord(("Logging:LogLevel:Default", "Trace"), ("Logging:Console:FormatterName", ConsoleFormatterNames.Simple));

        Assert.Equal(string.Empty, output);
        // One record, two lines: the timestamped prefix, then the indented message (SingleLine is
        // left at MEL's default so an exception keeps its stack trace).
        var lines = error.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var prefix = lines[0][..lines[0].IndexOf("warn:", StringComparison.Ordinal)];
        AssertCompiledInTimestamp(prefix, RuntimeLogging.SimpleTimestampFormat);
        Assert.Matches(@"^warn: \S+\[\d+\]$", lines[0][prefix.Length..]);
        Assert.Contains("Probe at warning.", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The destination is an invariant, not a setting: a configuration that asks for stdout cannot
    /// take the stream the adapters TSV and the validate confirmation own.
    /// </summary>
    [Fact]
    public void AHostileLogToStandardErrorThresholdStillWritesToStderr()
    {
        var (output, error) = CaptureRecord(("Logging:LogLevel:Default", "Trace"), ("Logging:Console:FormatterName", ConsoleFormatterNames.Simple), ("Logging:Console:LogToStandardErrorThreshold", "None"));

        Assert.Equal(string.Empty, output);
        Assert.Contains("Probe at warning.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOperatorTimestampFormatReachesTheRenderedLine()
    {
        var (_, error) = CaptureRecord(("Logging:LogLevel:Default", "Trace"), ("Logging:Console:FormatterName", ConsoleFormatterNames.Simple), ("Logging:Console:FormatterOptions:TimestampFormat", "yyyy~"));

        Assert.Matches(@"^\d{4}~", error);
    }

    /// <summary>
    /// A configuration that never mentions the timestamp still renders the one this project
    /// compiles in, on both formatters.
    /// </summary>
    [Fact]
    public void AJsonRecordCarriesTheCompiledInTimestampWithoutBeingConfigured()
    {
        var (output, error) = CaptureRecord(("Logging:LogLevel:Default", "Trace"), ("Logging:Console:FormatterName", ConsoleFormatterNames.Json));

        Assert.Equal(string.Empty, output);
        using var record = JsonDocument.Parse(error);
        AssertCompiledInTimestamp(record.RootElement.GetProperty("Timestamp").GetString(), RuntimeLogging.TimestampFormat);
        Assert.Contains("Probe at warning.", record.RootElement.GetProperty("Message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The posture with no <c>Logging</c> section at all: the automatic rule chooses the formatter,
    /// and the record still carries the timestamp this project compiles in.
    /// </summary>
    [Fact]
    public void WithNoLoggingConfigurationTheRecordStillCarriesTheCompiledInTimestamp()
    {
        var (output, error) = CaptureRecord();

        Assert.Equal(string.Empty, output);
        var text = error.Trim();
        if (text.StartsWith('{'))
        {
            using var record = JsonDocument.Parse(text);
            AssertCompiledInTimestamp(record.RootElement.GetProperty("Timestamp").GetString(), RuntimeLogging.TimestampFormat);
        }
        else
        {
            AssertCompiledInTimestamp(text[..text.IndexOf("warn:", StringComparison.Ordinal)], RuntimeLogging.SimpleTimestampFormat);
        }
    }

    /// <summary>
    /// The names the pre-flight accepts, so the shape test below is driven by that list rather than
    /// by a copy of it.
    /// </summary>
    public static TheoryData<string> AcceptedFormatterNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var name in RuntimeLogging.AcceptedFormatterNames) names.Add(name);
            return names;
        }
    }

    /// <summary>
    /// Every accepted formatter name resolves to the formatter it names. The list is only honest if
    /// configuring one renders that formatter's own shape: a name nothing registered falls back to
    /// the default formatter in silence, which is what this asserts against.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptedFormatterNames))]
    public void EveryAcceptedFormatterNameRendersItsOwnShape(string formatterName)
    {
        var (output, error) = CaptureRecord(("Logging:LogLevel:Default", "Trace"), ("Logging:Console:FormatterName", formatterName));

        Assert.Equal(string.Empty, output);
        Assert.Contains("Probe at warning.", error, StringComparison.Ordinal);
        switch (formatterName)
        {
            case ConsoleFormatterNames.Json:
                using (var record = JsonDocument.Parse(error)) Assert.Equal(nameof(LogLevel.Warning), record.RootElement.GetProperty("LogLevel").GetString());
                break;
            case ConsoleFormatterNames.Systemd:
                // Syslog severity 4 is "warning"; neither other formatter writes a severity prefix.
                Assert.StartsWith("<4>", error, StringComparison.Ordinal);
                break;
            case ConsoleFormatterNames.Simple:
                Assert.Contains($"warn: {typeof(FlowDispatcher).FullName}[", error, StringComparison.Ordinal);
                break;
            default:
                // A newly accepted name must have its shape asserted here before the list may name it.
                Assert.Fail($"The accepted formatter name '{formatterName}' has no shape assertion in this test.");
                break;
        }
    }

    /// <summary>
    /// MEL's own encoder escapes every non-ASCII character and the plus sign; the relaxed encoder
    /// this project chose survives a configuration that never mentions the formatter.
    /// </summary>
    [Fact]
    public void AJsonRecordKeepsNonAsciiTextAndPlusUnescaped()
    {
        var (output, error) = CaptureRecord(LoggerThresholdProbe.Escaped, ("Logging:LogLevel:Default", "Trace"), ("Logging:Console:FormatterName", ConsoleFormatterNames.Json));

        Assert.Equal(string.Empty, output);
        Assert.Contains("工具+路径", error, StringComparison.Ordinal);
    }

    private static void AssertCompiledInTimestamp(string? rendered, string format) =>
        Assert.True(
            DateTimeOffset.TryParseExact(rendered, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            $"'{rendered}' does not render the compiled-in timestamp format '{format}'.");

    private static (string Output, string Error) CaptureRecord(params (string Key, string Value)[] settings) =>
        CaptureRecord(LoggerThresholdProbe.Warning, settings);

    private static (string Output, string Error) CaptureRecord(Action<ILogger> log, params (string Key, string Value)[] settings)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            using var factory = RuntimeLogging.CreateLoggerFactory(TestLoggingConfiguration.From(settings));
            log(factory.CreateLogger<FlowDispatcher>());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        return (output.ToString(), error.ToString());
    }

}
