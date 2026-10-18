using System.Net;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime.TcpRedirect;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Runtime.Capture.Tests;

/// <summary>
/// The executor's pass observation gate: only a packet the dispatcher tagged as a claimed policy pass
/// reaches <see cref="IKernelPassFlowObserver"/>. Every other pass origin — self traffic, non-flow
/// frames, a proxied flow's reverse leg, the redirect fallback — goes through the same
/// <see cref="NdisPacketActionExecutor.PassAsync"/> and must never arm a kernel mirror, because the
/// executor cannot tell those apart from a policy pass on its own.
/// </summary>
public sealed class NdisPacketActionExecutorPassKindTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task OnlyAPolicyPassReachesTheObserver()
    {
        var observer = new RecordingPassObserver();
        var executor = new NdisPacketActionExecutor(new CountingReinjector(), passFlowObserver: observer);
        using var buffer = new NdisPacketBuffer();
        buffer.SetFrame([0x40], NdisApiAbi.PacketFlagOnSend, 9);
        var key = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host);

        foreach (var kind in Enum.GetValues<PacketPassKind>())
        {
            await executor.PassAsync(PassPacket(buffer, key, kind));
        }

        var observed = Assert.Single(observer.Keys);
        Assert.Equal(key, observed);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AnUntaggedPassStillReinjectsTheFrame()
    {
        var observer = new RecordingPassObserver();
        var reinjector = new CountingReinjector();
        var executor = new NdisPacketActionExecutor(reinjector, passFlowObserver: observer);
        using var buffer = new NdisPacketBuffer();
        buffer.SetFrame([0x41], NdisApiAbi.PacketFlagOnSend, 9);
        var key = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host);

        await executor.PassAsync(PassPacket(buffer, key, PacketPassKind.None));
        executor.FlushPendingPasses(9);

        Assert.Empty(observer.Keys);
        Assert.Equal(0, reinjector.SendToAdapterCount + reinjector.SendToMstcpCount);
        Assert.Equal(1, reinjector.BatchSendToAdapterCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task TheRedirectFallbackPassIsNotAPolicyPass()
    {
        var observer = new RecordingPassObserver();
        var reinjector = new CountingReinjector();
        await using var coordinator = TcpCoordinatorFakes.CreateCoordinator(
            new ThrowingRedirectListenerFactory(), new ThrowingRelayFactory(), new ThrowingRedirectInjector(),
            new TcpRedirectTable(), new SelfTrafficRegistry(), new FakeLocalAddressProvider());
        var executor = new NdisPacketActionExecutor(reinjector, logger: NullLogger.Instance, tcpProxy: coordinator, passFlowObserver: observer);
        using var buffer = new NdisPacketBuffer();
        buffer.SetFrame(FrameBuilders.CreateIPv4TcpFrame(), NdisApiAbi.PacketFlagOnSend, 7);
        var key = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var packet = PassPacket(buffer, key, PacketPassKind.None);

        // The coordinator reports NotRelevant for a proxied flow whose SYN was never captured; the
        // executor passes the frame so the pre-existing connection stays alive. That pass is not a
        // policy decision and must not be mirrored.
        await executor.ProxyAsync(packet, ProxyTarget.FromServer(new Socks5Server("p", "127.0.0.1", 1080, Username: null, Password: null)), CancellationToken.None);
        executor.FlushPendingPasses(7);

        Assert.Empty(observer.Keys);
        Assert.Equal(1, reinjector.BatchSendToAdapterCount);
    }

    /// <summary>A packet whose only interesting property is its pass kind; the frame keeps the lane simple.</summary>
    private static CapturedFlowPacket PassPacket(NdisPacketBuffer buffer, FlowKey key, PacketPassKind kind) =>
        new(
            new PacketLease(buffer),
            FlowBuilders.Context(key),
            new PacketCaptureMetadata(buffer.DeviceFlags, buffer.CapturedAdapterHandle, buffer.Flags),
            NativeFrame: new NativeFrameHandle(buffer),
            PassKind: kind);

    private sealed class RecordingPassObserver : IKernelPassFlowObserver
    {
        private readonly Lock _gate = new();
        private readonly List<FlowKey> _keys = [];

        public IReadOnlyList<FlowKey> Keys
        {
            get { lock (_gate) return [.. _keys]; }
        }

        public void OnPassPacket(in FlowKey key)
        {
            lock (_gate) _keys.Add(key);
        }
    }
}
