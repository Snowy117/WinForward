using System.ComponentModel;
using System.Net;
using WinForward.Configuration;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// Structured diagnostic events on the failure paths that previously logged a fixed sentence or
/// nothing: relay setup failure, unrelated accept peer, flow-table capacity block, attribution
/// miss, and pass-through reinjection native failures. Every test asserts the event name, its
/// warn level, and the diagnostic fields — while the underlying teardown/block/drop behavior is
/// pinned by the existing suites.
/// </summary>
public sealed class RuntimeDiagnosticLoggingTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);
    private static readonly IPAddress s_client = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destination = IPAddress.Parse("192.0.2.53");

    [Fact]
    public async Task RelaySetupFailureWarnCarriesDiagnostics()
    {
        var logger = new RecordingRuntimeLogger();
        var listenerFactory = new FakeListenerFactory();
        await using var coordinator = new TcpProxyCoordinator(
            listenerFactory,
            new FakeRelayFactory(throwOnEstablish: true),
            new FakeInjector(),
            new TcpRedirectTable(),
            new SelfTrafficRegistry(),
            new FakeLocalAddressProvider(),
            new TcpRedirectOptions { Logger = logger });
        var syn = MakeSynPacket(s_client, s_destination, 53000, 443);
        await HandleSynSettledAsync(coordinator, syn, s_server);
        var listener = Assert.Single(listenerFactory.Listeners);

        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(s_destination, 53000)), CancellationToken.None);
        await WaitForAsync(() => logger.Events.Any(e => string.Equals(e.Name, "tcp.redirect.relaySetupFailed", StringComparison.Ordinal)));

        var (level, _, fields) = Assert.Single(logger.Events, e => string.Equals(e.Name, "tcp.redirect.relaySetupFailed", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Warn, level);
        Assert.Equal("IOException", fields.Single(f => string.Equals(f.Key, "error", StringComparison.Ordinal)).Value);
        Assert.Equal("127.0.0.1:1080", fields.Single(f => string.Equals(f.Key, "upstream", StringComparison.Ordinal)).Value);
        Assert.Equal(2, fields.Single(f => string.Equals(f.Key, "attempts", StringComparison.Ordinal)).Value);
        Assert.Equal("test", fields.Single(f => string.Equals(f.Key, "proxy", StringComparison.Ordinal)).Value);
        Assert.IsType<Endpoint>(fields.Single(f => string.Equals(f.Key, "destination", StringComparison.Ordinal)).Value);
    }

    [Fact]
    public async Task UnrelatedPeerWarnCarriesEndpoints()
    {
        var logger = new RecordingRuntimeLogger();
        var listenerFactory = new FakeListenerFactory();
        await using var coordinator = new TcpProxyCoordinator(
            listenerFactory,
            new FakeRelayFactory(),
            new FakeInjector(),
            new TcpRedirectTable(),
            new SelfTrafficRegistry(),
            new FakeLocalAddressProvider(),
            new TcpRedirectOptions { Logger = logger });
        var syn = MakeSynPacket(s_client, s_destination, 53000, 443);
        await HandleSynSettledAsync(coordinator, syn, s_server);
        var listener = Assert.Single(listenerFactory.Listeners);

        // A peer that is not the association's accepted-peer endpoint is closed, never relayed.
        // The accept loop emits the structured event strictly before it disposes the connection
        // (program order), so disposal is that branch's terminal effect: once observed, the
        // event is already recorded and can be asserted synchronously — no event-polling race.
        // The widened budget absorbs thread-pool starvation under full-suite parallel load
        // (queued channel continuations can outlive the default 2 s window).
        var unrelated = new FakeAcceptedConnection(Endpoint.From(s_destination, 53999));
        await listener.AcceptChannel.Writer.WriteAsync(unrelated, CancellationToken.None);
        await WaitForAsync(() => unrelated.IsDisposed, timeoutMs: 15_000);

        var (level, _, fields) = Assert.Single(logger.Events, e => string.Equals(e.Name, "tcp.redirect.unrelatedPeer", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Warn, level);
        Assert.IsType<Endpoint>(fields.Single(f => string.Equals(f.Key, "listener", StringComparison.Ordinal)).Value);
        Assert.Equal(Endpoint.From(s_destination, 53000), fields.Single(f => string.Equals(f.Key, "expected", StringComparison.Ordinal)).Value);
        Assert.Equal(Endpoint.From(s_destination, 53999), fields.Single(f => string.Equals(f.Key, "actual", StringComparison.Ordinal)).Value);
    }

    [Fact]
    public async Task CapacityBlockWarnCarriesTableStateAndIsThrottled()
    {
        var config = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        var logger = new RecordingRuntimeLogger();
        var executor = new FakeExecutor();
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, flowCapacity: 1, logger: logger);

        await dispatcher.DispatchAsync(HostUdpPacket(53000), CancellationToken.None);
        Assert.Equal(1, executor.PassCount);

        await dispatcher.DispatchAsync(HostUdpPacket(53001), CancellationToken.None);
        await dispatcher.DispatchAsync(HostUdpPacket(53002), CancellationToken.None);

        Assert.Equal(2, executor.BlockCount);
        var (level, _, fields) = Assert.Single(logger.Events, e => string.Equals(e.Name, "flow.capacity-block", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Warn, level);
        Assert.Equal(1, fields.Single(f => string.Equals(f.Key, "tableSize", StringComparison.Ordinal)).Value);
        Assert.Equal(1, fields.Single(f => string.Equals(f.Key, "capacity", StringComparison.Ordinal)).Value);
        Assert.Equal(TransportProtocol.Udp, fields.Single(f => string.Equals(f.Key, "protocol", StringComparison.Ordinal)).Value);
    }

    [Fact]
    public async Task AttributionMissWarnCarriesAfterRetryAndSuccessStaysSilent()
    {
        var config = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot(
            [
                new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null)),
            ], FlowAction.Pass));
        var logger = new RecordingRuntimeLogger();
        var missing = new FlowDispatcher(config, new FakeGuard(), new FakeExecutor(), new FakeAttributor(name: null), logger: logger);

        await missing.DispatchAsync(HostUdpPacket(53000), CancellationToken.None);
        await missing.DispatchAsync(HostUdpPacket(53001), CancellationToken.None);

        var (level, _, fields) = Assert.Single(logger.Events, e => string.Equals(e.Name, "flow.attribution-miss", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Warn, level);
        Assert.True((bool)fields.Single(f => string.Equals(f.Key, "afterRetry", StringComparison.Ordinal)).Value!);
        Assert.IsType<Endpoint>(fields.Single(f => string.Equals(f.Key, "local", StringComparison.Ordinal)).Value);

        var successLogger = new RecordingRuntimeLogger();
        var resolving = new FlowDispatcher(config, new FakeGuard(), new FakeExecutor(), new FakeAttributor("dns.exe"), logger: successLogger);
        await resolving.DispatchAsync(HostUdpPacket(53000), CancellationToken.None);
        Assert.DoesNotContain(successLogger.Events, e => string.Equals(e.Name, "flow.attribution-miss", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PassFlushFailureWarnsAndPropagates()
    {
        var logger = new RecordingRuntimeLogger();
        var executor = new NdisPacketActionExecutor(new ThrowingBatchReinjector(), logger);

        await executor.PassAsync(PassPacket());
        Assert.Throws<Win32Exception>(() => executor.FlushPendingPasses(7));

        var (level, _, fields) = Assert.Single(logger.Events, e => string.Equals(e.Name, "reinject.pass-failed", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Warn, level);
        Assert.Equal(87, fields.Single(f => string.Equals(f.Key, "nativeError", StringComparison.Ordinal)).Value);
        Assert.Equal("Win32Exception", fields.Single(f => string.Equals(f.Key, "error", StringComparison.Ordinal)).Value);
        Assert.Equal(1, fields.Single(f => string.Equals(f.Key, "frames", StringComparison.Ordinal)).Value);
        Assert.Null(fields.Single(f => string.Equals(f.Key, "source", StringComparison.Ordinal)).Value);

        // A second failing flush inside the throttle window still propagates but stays silent.
        await executor.PassAsync(PassPacket());
        Assert.Throws<Win32Exception>(() => executor.FlushPendingPasses(7));
        Assert.Single(logger.Events, e => string.Equals(e.Name, "reinject.pass-failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OverflowImmediateSendFailureWarnsWithFlowKeyAndAdapter()
    {
        var logger = new RecordingRuntimeLogger();
        var executor = new NdisPacketActionExecutor(new ThrowingBatchReinjector(singleSendsToo: true), logger);
        // An empty scope installs the zero-capacity lane table, so the next pass takes the
        // immediate single-send backstop that carries the flow key and adapter stable ID.
        executor.RetireLanesExcept([]);

        await Assert.ThrowsAsync<Win32Exception>(() => executor.PassAsync(PassPacket()).AsTask());

        var (level, _, fields) = Assert.Single(logger.Events, e => string.Equals(e.Name, "reinject.pass-failed", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Warn, level);
        Assert.Equal(87, fields.Single(f => string.Equals(f.Key, "nativeError", StringComparison.Ordinal)).Value);
        Assert.Equal("wlan-1", fields.Single(f => string.Equals(f.Key, "adapter", StringComparison.Ordinal)).Value);
        Assert.IsType<Endpoint>(fields.Single(f => string.Equals(f.Key, "source", StringComparison.Ordinal)).Value);
    }

    private static CapturedFlowPacket HostUdpPacket(ushort clientPort) =>
        new(
            new PacketLease(FrameBuilders.CreateIpv4UdpFrame()),
            FlowContext(FlowKey.Create(Endpoint.From(s_client, clientPort), Endpoint.From(s_destination, 53), TransportProtocol.Udp, FlowOriginKind.Host)));

    private static CapturedFlowPacket PassPacket() =>
        new(
            new PacketLease(FrameBuilders.CreateIpv4UdpFrame()),
            new FlowContext(
                FlowKey.Create(Endpoint.From(s_client, 53000), Endpoint.From(s_destination, 53), TransportProtocol.Udp, FlowOriginKind.Host),
                ProcessName: null, ProcessPath: null, "wlan-1", "Wi-Fi", 53),
            new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 7));

    private static FlowContext FlowContext(FlowKey key) => new(key, ProcessName: null, ProcessPath: null, AdapterId: null, AdapterName: null, key.Remote.Port);

    /// <summary>Fails every batched flush (and optionally every single send) with a native error.</summary>
    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local // Deliberate failure-injection seam: the flag selects whether single sends fail in addition to batched flushes, so both native-failure paths are exercised deterministically.
    private sealed class ThrowingBatchReinjector(bool singleSendsToo = false) : IPacketReinjector
    {
        public void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer)
        {
            if (singleSendsToo) throw new Win32Exception(87);
        }

        public void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer)
        {
            if (singleSendsToo) throw new Win32Exception(87);
        }

        public void SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count) => throw new Win32Exception(87);
        public void SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers, int count) => throw new Win32Exception(87);
    }
}
