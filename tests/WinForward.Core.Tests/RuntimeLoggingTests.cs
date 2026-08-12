using System.Globalization;
using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class RuntimeLoggingTests
{
    [Theory]
    [InlineData(RuntimeLogLevel.Error, true, false, false, false, false)]
    [InlineData(RuntimeLogLevel.Warn, true, true, false, false, false)]
    [InlineData(RuntimeLogLevel.Info, true, true, true, false, false)]
    [InlineData(RuntimeLogLevel.Debug, true, true, true, true, false)]
    [InlineData(RuntimeLogLevel.Trace, true, true, true, true, true)]
    public void ConsoleLoggerFiltersByOrderedThreshold(RuntimeLogLevel threshold, bool error, bool warn, bool info, bool debug, bool trace)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var logger = new ConsoleRuntimeLogger(threshold, writer);

        logger.Error("error");
        logger.Warn("warn");
        logger.Info("info");
        logger.Debug("debug");
        logger.Trace("trace");

        var output = writer.ToString();
        Assert.Equal(error, output.Contains("[error] error", StringComparison.Ordinal));
        Assert.Equal(warn, output.Contains("[warn] warn", StringComparison.Ordinal));
        Assert.Equal(info, output.Contains("[info] info", StringComparison.Ordinal));
        Assert.Equal(debug, output.Contains("[debug] debug", StringComparison.Ordinal));
        Assert.Equal(trace, output.Contains("[trace] trace", StringComparison.Ordinal));
    }

    [Fact]
    public void StructuredFormatterEscapesHostileValuesAndFormatsIpv6()
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var logger = new ConsoleRuntimeLogger(RuntimeLogLevel.Trace, writer);

        logger.Event(RuntimeLogLevel.Trace, "packet.completed",
            new("packet", 42),
            new("endpoint", Endpoint.From(IPAddress.Parse("2001:db8::1"), 443)),
            new("value", "a b=\"c\"\r\n"),
            new("missing", null));

        Assert.Equal("[trace] packet.completed packet=42 endpoint=[2001:db8::1]:443 value=\"a b=\\\"c\\\"\\r\\n\"" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public async Task DispatcherPropagatesFlowGenerationAndEmitsTerminalTrace()
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var logger = new ConsoleRuntimeLogger(RuntimeLogLevel.Trace, writer);
        var executor = new RecordingExecutor();
        var configuration = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass),
            RuntimeLogLevel.Trace);
        var dispatcher = new FlowDispatcher(configuration, new NoSelfTraffic(), executor, logger: logger);
        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 50000),
            Endpoint.From(IPAddress.Parse("198.51.100.20"), 443),
            TransportProtocol.Tcp,
            FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), new FlowContext(key, "browser.exe", "C:\\Users\\test\\browser.exe", "adapter-id", "Ethernet", 443), PacketSequence: 17);

        await dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.NotNull(executor.LastPacket);
        Assert.Equal(17, executor.LastPacket.PacketSequence);
        Assert.True(executor.LastPacket.FlowGeneration > 0);
        var output = writer.ToString();
        Assert.Contains("flow.created flow=1", output, StringComparison.Ordinal);
        Assert.Contains("packet.completed packet=17 flow=1 disposition=pass", output, StringComparison.Ordinal);
        Assert.Contains("process=browser.exe", output, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\test", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatcherIncludesProcessPathOnlyWhenPathPolicyRequiresIt()
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var logger = new ConsoleRuntimeLogger(RuntimeLogLevel.Debug, writer);
        var configuration = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass),
            RuntimeLogLevel.Debug,
            IncludeProcessPathInLogs: true);
        var dispatcher = new FlowDispatcher(configuration, new NoSelfTraffic(), new RecordingExecutor(), logger: logger);
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 50000), Endpoint.From(IPAddress.Parse("198.51.100.20"), 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), new FlowContext(key, "browser.exe", "C:\\Apps\\browser.exe", null, null, 443));

        await dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.Contains("processPath=C:\\Apps\\browser.exe", writer.ToString(), StringComparison.Ordinal);
    }

    private sealed class NoSelfTraffic : ISelfTrafficGuard
    {
        public bool IsOwned(FlowContext context) => false;
    }

    private sealed class RecordingExecutor : IPacketActionExecutor
    {
        public CapturedFlowPacket? LastPacket { get; private set; }
        public ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) { LastPacket = packet; return ValueTask.CompletedTask; }
        public ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) { LastPacket = packet; return ValueTask.CompletedTask; }
        public ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken) { LastPacket = packet; return ValueTask.CompletedTask; }
    }
}
