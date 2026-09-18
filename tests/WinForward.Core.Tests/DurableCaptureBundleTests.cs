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
/// forwarded responses, host responses use the fallback) with a structured warn, a zero-MAC
/// scope head keeps the zero-placeholder fallback semantics, an empty scope clears the snapshot,
/// and a new scope swaps the snapshot wholesale so stale adapters stop resolving. The no-MAC
/// warns are change-gated (task 09-17 R2.4): repeated installs of the same zero-MAC set warn
/// once, a changed set warns again, and an all-MAC (or empty) install resets the memory.
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

    /// <summary>The structured <c>udp.targets.noMac</c> warns recorded so far, both kinds included.</summary>
    private static List<RuntimeLogField[]> NoMacEvents(RecordingRuntimeLogger logger) =>
        logger.Events.Where(entry => string.Equals(entry.Name, "udp.targets.noMac", StringComparison.Ordinal))
            .Select(entry => entry.Fields)
            .ToList();

    private static object? Field(RuntimeLogField[] fields, string key) =>
        fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal)).Value;

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
        Assert.Empty(NoMacEvents(logger));
    }

    [Fact]
    public async Task ZeroMacAdaptersAreFilteredOutOfThePerAdapterMapWithWarn()
    {
        var logger = new RecordingRuntimeLogger();
        await using var bundle = CreateBundle(logger);

        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_macA), Item("b", (nint)0x11, s_zeroMac)]);

        // The zero-MAC adapter fails closed: it has no resolvable target, and the group warn names it.
        Assert.Null(bundle.UdpTargets.Resolve("b"));
        Assert.NotNull(bundle.UdpTargets.Resolve("a"));
        var warn = Assert.Single(NoMacEvents(logger));
        Assert.Equal("adapters", Field(warn, "kind"));
        Assert.Contains("b(b)", (string)Field(warn, "adapters")!, StringComparison.Ordinal);
        Assert.Equal(1L, Field(warn, "count"));
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
        var events = NoMacEvents(logger);
        Assert.Equal(2, events.Count);
        Assert.Contains(events, fields => Equals(Field(fields, "kind"), "hostFallback") && Equals(Field(fields, "host"), "a"));
        Assert.Contains(events, fields => Equals(Field(fields, "kind"), "adapters") && ((string)Field(fields, "adapters")!).Contains("a(a)", StringComparison.Ordinal));
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

    [Fact]
    public async Task NoMacGroupWarnFiresOnceForAnUnchangedAdapterSet()
    {
        var logger = new RecordingRuntimeLogger();
        await using var bundle = CreateBundle(logger);
        var scope = new[] { Item("a", (nint)0x10, s_macA), Item("b", (nint)0x11, s_zeroMac) };

        bundle.UpdateUdpTargets(scope);
        bundle.UpdateUdpTargets(scope);
        bundle.UpdateUdpTargets(scope);

        Assert.Single(NoMacEvents(logger), fields => Equals(Field(fields, "kind"), "adapters"));
    }

    [Fact]
    public async Task NoMacGroupWarnRefiresWhenTheAdapterSetChanges()
    {
        var logger = new RecordingRuntimeLogger();
        await using var bundle = CreateBundle(logger);

        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_macA), Item("b", (nint)0x11, s_zeroMac)]);
        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_macA), Item("b", (nint)0x11, s_zeroMac), Item("c", (nint)0x12, s_zeroMac)]);

        var groupWarns = NoMacEvents(logger).Where(fields => Equals(Field(fields, "kind"), "adapters")).ToArray();
        Assert.Equal(2, groupWarns.Length);
        Assert.Contains("b(b)", (string)Field(groupWarns[0], "adapters")!, StringComparison.Ordinal);
        Assert.DoesNotContain("c(c)", (string)Field(groupWarns[0], "adapters")!, StringComparison.Ordinal);
        Assert.Contains("b(b)", (string)Field(groupWarns[1], "adapters")!, StringComparison.Ordinal);
        Assert.Contains("c(c)", (string)Field(groupWarns[1], "adapters")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoMacGroupWarnResetsAfterAnAllMacScope()
    {
        var logger = new RecordingRuntimeLogger();
        await using var bundle = CreateBundle(logger);

        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_macA), Item("b", (nint)0x11, s_zeroMac)]);
        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_macA), Item("b", (nint)0x11, s_macB)]);
        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_macA), Item("b", (nint)0x11, s_zeroMac)]);

        var groupWarns = NoMacEvents(logger).Where(fields => Equals(Field(fields, "kind"), "adapters")).ToArray();
        Assert.Equal(2, groupWarns.Length);
    }

    [Fact]
    public async Task NoMacGroupWarnResetsAfterAnEmptyScope()
    {
        var logger = new RecordingRuntimeLogger();
        await using var bundle = CreateBundle(logger);

        bundle.UpdateUdpTargets([Item("b", (nint)0x11, s_zeroMac)]);
        bundle.UpdateUdpTargets([]);
        bundle.UpdateUdpTargets([Item("b", (nint)0x11, s_zeroMac)]);

        Assert.Equal(2, NoMacEvents(logger).Count(fields => Equals(Field(fields, "kind"), "adapters")));
    }

    [Fact]
    public async Task ZeroMacHostWarnFiresOncePerDistinctHostAdapter()
    {
        var logger = new RecordingRuntimeLogger();
        await using var bundle = CreateBundle(logger);

        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_zeroMac), Item("b", (nint)0x11, s_macB)]);
        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_zeroMac), Item("b", (nint)0x11, s_macB)]);
        bundle.UpdateUdpTargets([Item("c", (nint)0x12, s_zeroMac), Item("b", (nint)0x11, s_macB)]);
        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_macA), Item("b", (nint)0x11, s_zeroMac)]);
        bundle.UpdateUdpTargets([Item("a", (nint)0x10, s_zeroMac), Item("b", (nint)0x11, s_macB)]);

        var hostWarns = NoMacEvents(logger).Where(fields => Equals(Field(fields, "kind"), "hostFallback")).ToArray();
        // First occurrence on 'a', the host change to 'c', and the return to a zero-MAC host
        // after 'a' recovered — but never a repeat of the same unchanged host.
        Assert.Equal(3, hostWarns.Length);
        Assert.Equal("a", Field(hostWarns[0], "host"));
        Assert.Equal("c", Field(hostWarns[1], "host"));
        Assert.Equal("a", Field(hostWarns[2], "host"));
    }
}
