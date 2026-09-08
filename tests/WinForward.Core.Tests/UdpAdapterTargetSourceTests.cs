using System.Net;
using System.Runtime.Versioning;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime.UdpProxy;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class UdpAdapterTargetSourceTests
{
    private static readonly byte[] s_macA = [0x02, 0x00, 0x00, 0x00, 0x00, 0x01];
    private static readonly byte[] s_macB = [0x02, 0x00, 0x00, 0x00, 0x00, 0x02];

    // ---- UdpAdapterTargetSource ----

    [Fact]
    public void ResolveReadsTheLatestSnapshotAfterUpdateWithoutReconstruction()
    {
        var source = new UdpAdapterTargetSource(
            new UdpAdapterTarget((nint)7, s_macA),
            new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase) { ["veth-1"] = new((nint)1234, s_macB) });

        Assert.Equal((nint)1234, source.Resolve("veth-1")!.Value.Handle);
        Assert.Equal((nint)1234, source.Resolve("VETH-1")!.Value.Handle);
        Assert.Null(source.Resolve("missing"));

        source.Update(
            new UdpAdapterTarget((nint)9, s_macA),
            new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase) { ["veth-1"] = new((nint)5678, s_macB) });

        Assert.Equal((nint)9, source.Host!.Value.Handle);
        Assert.Equal((nint)5678, source.Resolve("veth-1")!.Value.Handle);
    }

    [Fact]
    public void SnapshotIsFrozenAgainstLaterCallerSideMapMutation()
    {
        var map = new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase) { ["veth-1"] = new((nint)1234, s_macB) };
        var source = new UdpAdapterTargetSource(new UdpAdapterTarget((nint)7, s_macA), map);

        map["veth-1"] = new((nint)9999, s_macB);
        map.Clear();

        Assert.Equal((nint)1234, source.Resolve("veth-1")!.Value.Handle);
    }

    [Fact]
    public void EmptyScopeYieldsNullHostAndResolutions()
    {
        var source = new UdpAdapterTargetSource();

        Assert.Null(source.Host);
        Assert.Null(source.Resolve("veth-1"));

        source.Update(null, new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase));
        Assert.Null(source.Host);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(7)]
    public void ConstructorAndUpdateRejectMalformedHostMac(int macLength)
    {
        var malformed = new UdpAdapterTarget((nint)7, new byte[macLength]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new UdpAdapterTargetSource(malformed));
        var source = new UdpAdapterTargetSource();
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Update(malformed, new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase)));
    }

    // ---- UdpResponseReinjector over a refreshable source ----

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ForwardedFlowUsesRefreshedHandleAfterSnapshotSwap()
    {
        var reinjector = new FakeReinjector();
        var source = new UdpAdapterTargetSource(
            new UdpAdapterTarget((nint)7, s_macA),
            new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase) { ["veth-1"] = new((nint)1234, s_macB) });
        var sink = new UdpResponseReinjector(reinjector, source);
        var adapter = new AdapterContext("veth-1", "vEthernet 1", 3);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Forwarded, adapter);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, s_macB, CancellationToken.None);
        Assert.Equal(1, reinjector.ToAdapterCount);
        Assert.Equal((nint)1234, reinjector.LastAdapterHandle);

        // The re-enumerated handle (adapter list rebuilt) is picked up per response through the
        // same sink instance — no reconstruction involved.
        source.Update(null, new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase) { ["veth-1"] = new((nint)5678, s_macB) });
        await sink.InjectAsync(flow, server, new byte[] { 2 }, s_macB, CancellationToken.None);
        Assert.Equal(2, reinjector.ToAdapterCount);
        Assert.Equal((nint)5678, reinjector.LastAdapterHandle);
        Assert.Equal(NdisApiAbi.PacketFlagOnSend, reinjector.LastDeviceFlags);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ForwardedFlowDropsFailClosedWhenAdapterLeavesTheSnapshot()
    {
        var reinjector = new FakeReinjector();
        var logger = new RecordingRuntimeLogger();
        var source = new UdpAdapterTargetSource(
            new UdpAdapterTarget((nint)7, s_macA),
            new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase) { ["veth-1"] = new((nint)1234, s_macB) });
        var sink = new UdpResponseReinjector(reinjector, source, logger: logger);
        var adapter = new AdapterContext("veth-1", "vEthernet 1", 3);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Forwarded, adapter);

        source.Update(new UdpAdapterTarget((nint)7, s_macA), new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase));
        await sink.InjectAsync(flow, server, new byte[] { 1 }, s_macB, CancellationToken.None);

        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.Equal(1, logger.WarnCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task HostFlowWithoutHostTargetIsDroppedFailClosed()
    {
        var reinjector = new FakeReinjector();
        var logger = new RecordingRuntimeLogger();
        var sink = new UdpResponseReinjector(reinjector, new UdpAdapterTargetSource(), logger: logger);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Host);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, null, CancellationToken.None);
        await sink.InjectAsync(flow, server, new byte[] { 2 }, null, CancellationToken.None);

        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.Equal(1, logger.WarnCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task HostFlowWithResolvedOriginInjectsEvenWithoutHostTarget()
    {
        var reinjector = new FakeReinjector();
        var originHandle = (nint)1234;
        var sink = new UdpResponseReinjector(
            reinjector,
            new UdpAdapterTargetSource(null, new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase) { ["wlan-1"] = new(originHandle, s_macB) }));
        var adapter = new AdapterContext("wlan-1", "Wi-Fi", 3);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Host, adapter);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, null, CancellationToken.None);

        Assert.Equal(1, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.Equal(originHandle, reinjector.LastAdapterHandle);
        Assert.True(reinjector.LastFrame!.AsSpan(0, 6).SequenceEqual(s_macB));
        Assert.True(reinjector.LastFrame!.AsSpan(6, 6).SequenceEqual(s_macB));
    }
}
