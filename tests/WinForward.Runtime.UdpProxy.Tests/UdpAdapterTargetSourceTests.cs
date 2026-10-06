using System.Net;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Runtime.UdpProxy.Tests;

public sealed class UdpAdapterTargetSourceTests
{
    private static readonly byte[] s_macA = [0x02, 0x00, 0x00, 0x00, 0x00, 0x01];
    private static readonly byte[] s_macB = [0x02, 0x00, 0x00, 0x00, 0x00, 0x02];
    private static readonly ushort s_veth = FlowBuilders.SlotOf("veth-1", 3);
    private static readonly ushort s_wlan = FlowBuilders.SlotOf("wlan-1", 3);

    // ---- UdpAdapterTargetSource ----

    [Fact]
    public void ResolveReadsTheLatestSnapshotAfterUpdateWithoutReconstruction()
    {
        var source = new UdpAdapterTargetSource(
            FlowBuilders.Slots,
            new UdpAdapterTarget(7, s_macA),
            new Dictionary<ushort, UdpAdapterTarget> { [s_veth] = new(1234, s_macB) });

        Assert.Equal(1234, source.Resolve(s_veth)!.Value.Handle);
        Assert.Null(source.Resolve(AdapterSlotTable.NoSlot));
        Assert.Null(source.Resolve((ushort)(s_veth + 500)));

        source.Update(
            new UdpAdapterTarget(9, s_macA),
            new Dictionary<ushort, UdpAdapterTarget> { [s_veth] = new(5678, s_macB) });

        Assert.Equal(9, source.Host!.Value.Handle);
        Assert.Equal(5678, source.Resolve(s_veth)!.Value.Handle);
    }

    [Fact]
    public void SnapshotIsFrozenAgainstLaterCallerSideMapMutation()
    {
        var map = new Dictionary<ushort, UdpAdapterTarget> { [s_veth] = new(1234, s_macB) };
        var source = new UdpAdapterTargetSource(FlowBuilders.Slots, new UdpAdapterTarget(7, s_macA), map);

        map[s_veth] = new(9999, s_macB);
        map.Clear();

        Assert.Equal(1234, source.Resolve(s_veth)!.Value.Handle);
    }

    [Fact]
    public void EmptyScopeYieldsNullHostAndResolutions()
    {
        var source = new UdpAdapterTargetSource();

        Assert.Null(source.Host);
        Assert.Null(source.Resolve(s_veth));

        source.Update(host: null, new Dictionary<ushort, UdpAdapterTarget>());
        Assert.Null(source.Host);
    }

    [Fact]
    public void AdapterIdsResolveThroughTheSlotTable()
    {
        var source = new UdpAdapterTargetSource(FlowBuilders.Slots);
        source.Update(host: null, new Dictionary<ushort, UdpAdapterTarget> { [s_wlan] = new(1234, s_macB), [s_veth] = new(1234, s_macB) });

        Assert.Equal(["veth-1", "wlan-1"], source.AdapterIds);

        // AdapterIds resolves identically whether the snapshot came from the constructor or Update.
        var seeded = new UdpAdapterTargetSource(FlowBuilders.Slots, host: null, new Dictionary<ushort, UdpAdapterTarget> { [s_wlan] = new(1234, s_macB) });
        Assert.Equal(["wlan-1"], seeded.AdapterIds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(7)]
    public void ConstructorAndUpdateRejectMalformedHostMac(int macLength)
    {
        var malformed = new UdpAdapterTarget(7, new byte[macLength]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new UdpAdapterTargetSource(FlowBuilders.Slots, malformed));
        var source = new UdpAdapterTargetSource();
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Update(malformed, new Dictionary<ushort, UdpAdapterTarget>()));
    }

    // ---- UdpResponseReinjector over a refreshable source ----

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ForwardedFlowUsesRefreshedHandleAfterSnapshotSwap()
    {
        var reinjector = new FakeReinjector();
        var source = new UdpAdapterTargetSource(
            FlowBuilders.Slots,
            new UdpAdapterTarget(7, s_macA),
            new Dictionary<ushort, UdpAdapterTarget> { [s_veth] = new(1234, s_macB) });
        var sink = new UdpResponseReinjector(reinjector, source, FlowBuilders.Slots);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Forwarded, s_veth, 3);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, MacAddress.From(s_macB), CancellationToken.None);
        Assert.Equal(1, reinjector.ToAdapterCount);
        Assert.Equal(1234, reinjector.LastAdapterHandle);

        // The re-enumerated handle (adapter list rebuilt) is picked up per response through the
        // same sink instance — no reconstruction involved.
        source.Update(host: null, new Dictionary<ushort, UdpAdapterTarget> { [s_veth] = new(5678, s_macB) });
        await sink.InjectAsync(flow, server, new byte[] { 2 }, MacAddress.From(s_macB), CancellationToken.None);
        Assert.Equal(2, reinjector.ToAdapterCount);
        Assert.Equal(5678, reinjector.LastAdapterHandle);
        Assert.Equal(NdisApiAbi.PacketFlagOnSend, reinjector.LastDeviceFlags);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ForwardedFlowDropsFailClosedWhenAdapterLeavesTheSnapshot()
    {
        var reinjector = new FakeReinjector();
        var logger = new RecordingLogger();
        var source = new UdpAdapterTargetSource(
            FlowBuilders.Slots,
            new UdpAdapterTarget(7, s_macA),
            new Dictionary<ushort, UdpAdapterTarget> { [s_veth] = new(1234, s_macB) });
        var sink = new UdpResponseReinjector(reinjector, source, FlowBuilders.Slots, logger: logger);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Forwarded, s_veth, 3);

        source.Update(new UdpAdapterTarget(7, s_macA), new Dictionary<ushort, UdpAdapterTarget>());
        await sink.InjectAsync(flow, server, new byte[] { 1 }, MacAddress.From(s_macB), CancellationToken.None);

        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.Single(logger.Events, e => e.Level == LogLevel.Warning && string.Equals(e.Name, "udp.reinject.drop", StringComparison.Ordinal));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task HostFlowWithoutHostTargetIsDroppedFailClosed()
    {
        var reinjector = new FakeReinjector();
        var logger = new RecordingLogger();
        var sink = new UdpResponseReinjector(reinjector, new UdpAdapterTargetSource(), logger: logger);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Host);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, MacAddress.Invalid, CancellationToken.None);
        await sink.InjectAsync(flow, server, new byte[] { 2 }, MacAddress.Invalid, CancellationToken.None);

        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.Single(logger.Events, e => e.Level == LogLevel.Warning && string.Equals(e.Name, "udp.reinject.drop", StringComparison.Ordinal));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task HostFlowWithResolvedOriginInjectsEvenWithoutHostTarget()
    {
        var reinjector = new FakeReinjector();
        const nint originHandle = 1234;
        var sink = new UdpResponseReinjector(
            reinjector,
            new UdpAdapterTargetSource(FlowBuilders.Slots, host: null, new Dictionary<ushort, UdpAdapterTarget> { [s_wlan] = new(originHandle, s_macB) }),
            FlowBuilders.Slots);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Host, s_wlan, 3);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, MacAddress.Invalid, CancellationToken.None);

        Assert.Equal(1, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.Equal(originHandle, reinjector.LastAdapterHandle);
        Assert.True(reinjector.LastFrame!.AsSpan(0, 6).SequenceEqual(s_macB));
        Assert.True(reinjector.LastFrame!.AsSpan(6, 6).SequenceEqual(s_macB));
    }
}
