using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using WinForward.Cli;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using WinForward.Windows;
using Xunit;

namespace WinForward.Integration.Tests;

/// <summary>
/// The kernel pass mirror's composition: the configuration switch is the only thing that creates it,
/// the driver table is never touched while the feature is off, the executor's observer is the cache
/// itself, the capture scope pauses and resumes it, and the bundle disposes it first so its final reset
/// still reaches the open driver.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KernelPassCacheCompositionTests
{
    [Fact]
    public void TheFeatureOffCreatesNoCacheAndNeverTouchesTheTable()
    {
        var table = new RecordingKernelFilterTable();

        var cache = DurableCaptureBundle.CreateKernelPassCache(CreateConfiguration(kernelPassCache: false), table, NullLoggerFactory.Instance, new RuntimeCounters());

        Assert.Null(cache);
        Assert.Empty(table.Operations);
    }

    [Fact]
    public async Task TheFeatureOnCreatesTheCacheAndOnlyStartClaimsTheTable()
    {
        var table = new RecordingKernelFilterTable();
        var cache = DurableCaptureBundle.CreateKernelPassCache(CreateConfiguration(kernelPassCache: true), table, NullLoggerFactory.Instance, new RuntimeCounters());

        Assert.NotNull(cache);
        Assert.Equal(0, table.ResetCalls);

        cache.Start();
        Assert.True(cache.IsRunning);
        Assert.Equal(1, table.ResetCalls);

        await cache.DisposeAsync();
        Assert.True(cache.IsDisabled);
        Assert.True(table.ResetCalls >= 2);
        Assert.Empty(table.InstalledRules);
    }

    [Fact]
    public void TheFeatureOnWithoutATableSeamIsACompositionError() =>
        Assert.Throws<InvalidOperationException>(() => DurableCaptureBundle.CreateKernelPassCache(CreateConfiguration(kernelPassCache: true), kernelFilterTable: null, NullLoggerFactory.Instance, new RuntimeCounters()));

    [Fact]
    public async Task TheScopeInstallPausesAndResumesTheMirror()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        var resets = harness.Table.ResetCalls;

        harness.Bundle.OnScopeInstalled([]);
        Assert.False(harness.Cache.IsRunning);
        Assert.True(harness.Table.ResetCalls > resets);

        harness.Bundle.OnScopeInstalled([new AdapterEnumerationItem(new WindowsAdapter("id-a", "Ethernet", "internal-a", 7, 1), [0x01, 0x02, 0x03, 0x04, 0x05, 0x06], 1500)]);
        Assert.True(harness.Cache.IsRunning);
    }

    [Fact]
    public async Task APolicyPassThroughTheWiredExecutorArmsTheMirror()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        var key = FlowKey.Create(
            Endpoint.From(System.Net.IPAddress.Parse("192.0.2.10"), 53000),
            Endpoint.From(System.Net.IPAddress.Parse("192.0.2.53"), 443),
            TransportProtocol.Tcp,
            FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 1 }), FlowBuilders.Context(key));

        await harness.Bundle.Dispatcher.DispatchAsync(packet, CancellationToken.None);

        // The dispatcher decided Pass, the executor observed it, and the cache queued the tuple: the
        // whole observer chain is wired by composition, not by the test.
        Assert.Equal(1, harness.Cache.PendingCandidates);
        Assert.Same(harness.Cache, harness.Bundle.KernelPassCache);
    }

    [Fact]
    public async Task DisposingTheBundleDisposesTheMirrorFirst()
    {
        var harness = new Harness();
        harness.Cache.Start();
        var resets = harness.Table.ResetCalls;

        await harness.Bundle.DisposeAsync();

        Assert.True(harness.Cache.IsDisabled);
        Assert.True(harness.Table.ResetCalls > resets);
        await harness.DisposeAsync();
    }

    private static ValidatedConfiguration CreateConfiguration(bool kernelPassCache) =>
        new(new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase), new PolicySnapshot([], FlowAction.Pass), KernelPassCache: kernelPassCache);

    [SupportedOSPlatform("windows")]
    private sealed class Harness : IAsyncDisposable
    {
        public Harness()
        {
            Table = new RecordingKernelFilterTable();
            Cache = new NdisKernelPassCache(Table, counters: new RuntimeCounters());
            var adapterSlots = new AdapterSlotTable();
            var configuration = CreateConfiguration(kernelPassCache: true);
            var executor = new NdisPacketActionExecutor(new FakeReinjector(), passFlowObserver: Cache);
            Dispatcher = new FlowDispatcher(configuration, new FakeGuard(), executor);
            var sweeper = new IdleExpirySweeper(Dispatcher, tcp: null, udp: null);
            var udp = UdpCoordinatorFakes.CreateCoordinator(new FakeTransportFactory(), new FakeResponseSink());
            var tcp = TcpCoordinatorFakes.CreateCoordinator(
                new FakeListenerFactory(),
                new FakeRelayFactory(),
                new FakeInjector(),
                new TcpRedirectTable(),
                new SelfTrafficRegistry(),
                new FakeLocalAddressProvider());
            Bundle = new DurableCaptureBundle(Dispatcher, executor, new UdpAdapterTargetSource(adapterSlots), sweeper, udp, tcp, NullLogger.Instance, adapterSlots, kernelPassCache: Cache);
        }

        public RecordingKernelFilterTable Table { get; }

        public NdisKernelPassCache Cache { get; }

        private FlowDispatcher Dispatcher { get; }

        public DurableCaptureBundle Bundle { get; }

        public ValueTask DisposeAsync() => Bundle.DisposeAsync();
    }
}
