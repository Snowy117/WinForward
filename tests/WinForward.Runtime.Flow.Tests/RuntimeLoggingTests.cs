using System.Net;
using Microsoft.Extensions.Logging;
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
        var output = new StringWriter();
        var error = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            using var factory = RuntimeLogging.CreateLoggerFactory(LogLevel.Trace, LogFormat.Simple);
            LoggerThresholdProbe.Warning(factory.CreateLogger<FlowDispatcher>());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Equal(string.Empty, output.ToString());
        // One record, two lines: the timestamped prefix, then the indented message (SingleLine is
        // left at MEL's default so an exception keeps its stack trace).
        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\S+ \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\s+warn: \S+\[\d+\]$", lines[0]);
        Assert.Contains("Probe at warning.", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AutoFollowsWhetherStderrIsRedirectedAndAForcedFormatIgnoresIt()
    {
        Assert.Equal(LogFormat.Json, RuntimeLogging.ResolveLogFormat(LogFormat.Auto, errorRedirected: true));
        Assert.Equal(LogFormat.Simple, RuntimeLogging.ResolveLogFormat(LogFormat.Auto, errorRedirected: false));
        Assert.Equal(LogFormat.Json, RuntimeLogging.ResolveLogFormat(LogFormat.Json, errorRedirected: false));
        Assert.Equal(LogFormat.Simple, RuntimeLogging.ResolveLogFormat(LogFormat.Simple, errorRedirected: true));
    }
}
