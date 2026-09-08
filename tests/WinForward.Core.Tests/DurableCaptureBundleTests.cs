using System.Runtime.Versioning;
using WinForward.Cli;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The durable bundle's UDP reinjection-target policy at scope install: the scope head becomes
/// the host-flow fallback, zero-MAC adapters are dropped from the per-adapter map (fail-closed
/// forwarded responses, host responses use the fallback) with a warn, a zero-MAC scope head
/// keeps the zero-placeholder fallback semantics, an empty scope clears the snapshot, and a new
/// scope swaps the snapshot wholesale so stale adapters stop resolving.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DurableCaptureBundleTests
{
    private static readonly byte[] s_macA = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
    private static readonly byte[] s_macB = [0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F];
    private static readonly byte[] s_zeroMac = new byte[6];

    private static AdapterEnumerationItem Item(string stableId, nint handle, byte[] mac) =>
        new(new WindowsAdapter(stableId, stableId, stableId, handle, 1), mac, 1500);

    private static DurableCaptureBundle CreateBundle(RecordingRuntimeLogger logger)
    {
        var configuration = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        var dispatcher = new FlowDispatcher(configuration, new FakeGuard(), new FakeExecutor());
        var executor = new NdisPacketActionExecutor(new FakeReinjector());
        var udpTargets = new UdpAdapterTargetSource();
        var sweeper = new IdleExpirySweeper(dispatcher, tcp: null, udp: null, logger: logger);
        var udp = new UdpProxyCoordinator(new FakeTransportFactory(), new FakeResponseSink());
        var tcp = new TcpProxyCoordinator(
            new FakeListenerFactory(),
            new FakeRelayFactory(),
            new FakeInjector(),
            new TcpRedirectTable(),
            new SelfTrafficRegistry(),
            new FakeLocalAddressProvider(),
            logger);
        return new DurableCaptureBundle(dispatcher, executor, udpTargets, sweeper, udp, tcp, logger);
    }

    private static byte[] MacOf(UdpAdapterTarget? target) => target!.Value.Mac;

    [Fact]
    public async Task ScopeHeadBecomesHostFallbackAndEachAdapterResolves()
    {
        var logger = new RecordingRuntimeLogger();
        await using var bundle = CreateBundle(logger);

        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_macA), Item("b", (nint)0x11, s_macB)]);

        var host = bundle.UdpTargets.Host;
        Assert.Equal((nint)0x10, host!.Value.Handle);
        Assert.Equal(s_macA, MacOf(host));
        var a = bundle.UdpTargets.Resolve("a");
        var b = bundle.UdpTargets.Resolve("b");
        Assert.Equal((nint)0x10, a!.Value.Handle);
        Assert.Equal(s_macA, MacOf(a));
        Assert.Equal((nint)0x11, b!.Value.Handle);
        Assert.Equal(s_macB, MacOf(b));
        Assert.Null(bundle.UdpTargets.Resolve("missing"));
        Assert.Empty(logger.Lines);
    }

    [Fact]
    public async Task ZeroMacAdaptersAreFilteredOutOfThePerAdapterMapWithWarn()
    {
        var logger = new RecordingRuntimeLogger();
        await using var bundle = CreateBundle(logger);

        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_macA), Item("b", (nint)0x11, s_zeroMac)]);

        // The zero-MAC adapter fails closed: it has no resolvable target, and the warn names it.
        Assert.Null(bundle.UdpTargets.Resolve("b"));
        Assert.NotNull(bundle.UdpTargets.Resolve("a"));
        var warn = Assert.Single(logger.Lines, line => line.Level == RuntimeLogLevel.Warn);
        Assert.Contains("no MAC for adapter 'b' (b)", warn.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZeroMacScopeHeadKeepsZeroPlaceholderHostFallbackWithWarn()
    {
        var logger = new RecordingRuntimeLogger();
        await using var bundle = CreateBundle(logger);

        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_zeroMac), Item("b", (nint)0x11, s_macB)]);

        var host = bundle.UdpTargets.Host;
        Assert.Equal((nint)0x10, host!.Value.Handle);
        Assert.Equal(s_zeroMac, MacOf(host));
        Assert.Null(bundle.UdpTargets.Resolve("a"));
        Assert.NotNull(bundle.UdpTargets.Resolve("b"));
        // The zero-MAC head warns twice: once for the zero-placeholder host fallback and once for
        // its own per-adapter fail-closed skip.
        Assert.Contains(logger.Lines, line => line.Level == RuntimeLogLevel.Warn && line.Message.Contains("zero MAC", StringComparison.Ordinal));
        Assert.Contains(logger.Lines, line => line.Level == RuntimeLogLevel.Warn && line.Message.Contains("no MAC for adapter 'a' (a)", StringComparison.Ordinal));
        Assert.Equal(2, logger.Lines.Count(line => line.Level == RuntimeLogLevel.Warn));
    }

    [Fact]
    public async Task EmptyScopeClearsHostFallbackAndAdapterMap()
    {
        var logger = new RecordingRuntimeLogger();
        await using var bundle = CreateBundle(logger);
        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_macA)]);

        bundle.UpdateUdpTargets([]);

        Assert.Null(bundle.UdpTargets.Host);
        Assert.Null(bundle.UdpTargets.Resolve("a"));
    }

    [Fact]
    public async Task NewScopeSwapsTheSnapshotWholesale()
    {
        var logger = new RecordingRuntimeLogger();
        await using var bundle = CreateBundle(logger);
        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_macA)]);

        bundle.UpdateUdpTargets([Item("c", (nint)0x12, s_macB)]);

        // The stale adapter no longer resolves; the fresh one does, and the fallback follows.
        Assert.Null(bundle.UdpTargets.Resolve("a"));
        Assert.NotNull(bundle.UdpTargets.Resolve("c"));
        Assert.Equal((nint)0x12, bundle.UdpTargets.Host!.Value.Handle);
    }
}
