using System.Buffers.Binary;
using System.Net;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.ChecksumMath;

namespace WinForward.Core.Tests;

public sealed class UdpRelayTests
{
    private static readonly byte[] s_macA = [0x02, 0x00, 0x00, 0x00, 0x00, 0x01];
    private static readonly byte[] s_macB = [0x02, 0x00, 0x00, 0x00, 0x00, 0x02];
    private static readonly byte[] s_macC = [0x02, 0x00, 0x00, 0x00, 0x00, 0x03];

    // ---- UdpFrameBuilder ----

    [Fact]
    public void Ipv4FrameRoundTripsThroughParser()
    {
        var source = IPAddress.Parse("192.0.2.53");
        var destination = IPAddress.Parse("192.0.2.10");
        var payload = new byte[] { 0xde, 0xad, 0xbe, 0xef };

        Assert.True(UdpFrameBuilder.TryBuild(source, 53, destination, 53000, payload, s_macA, s_macB, out var frame));
        Assert.True(IPUdpPacket.TryParse(frame, out var udp));

        Assert.Equal(source, udp.SourceAddress);
        Assert.Equal(destination, udp.DestinationAddress);
        Assert.Equal((ushort)53, udp.SourcePort);
        Assert.Equal((ushort)53000, udp.DestinationPort);
        Assert.Equal(payload, udp.Payload.ToArray());
        Assert.Equal(20, udp.IPHeaderLength);
    }

    [Fact]
    public void Ipv4HeaderChecksumValidatesToZero()
    {
        Assert.True(UdpFrameBuilder.TryBuild(IPAddress.Parse("192.0.2.53"), 53, IPAddress.Parse("192.0.2.10"), 53000, new byte[] { 1, 2, 3 }, s_macA, s_macB, out var frame));
        Assert.Equal((ushort)0, PacketChecksums.InternetChecksum(frame.AsSpan(14, 20)));
    }

    [Fact]
    public void Ipv4UdpChecksumValidatesWithPseudoHeader()
    {
        var source = IPAddress.Parse("192.0.2.53");
        var destination = IPAddress.Parse("192.0.2.10");
        var payload = new byte[] { 0xaa, 0xbb, 0xcc };

        Assert.True(UdpFrameBuilder.TryBuild(source, 53, destination, 53000, payload, s_macA, s_macB, out var frame));

        var udpLength = 8 + payload.Length;
        var sum = Sum(source.GetAddressBytes()) + Sum(destination.GetAddressBytes()) + 17u + (ushort)udpLength + Sum(frame.AsSpan(34, udpLength));
        Assert.Equal((ushort)0, Finish(sum));
    }

    [Fact]
    public void ZeroUdpChecksumIsInvertedTo0xFFFF()
    {
        // Construct a payload that makes the computed UDP checksum fold to zero, proving the
        // RFC 768 inversion: a computed 0 checksum is transmitted as 0xFFFF. The pseudo-header,
        // UDP header (checksum field zero), and a 2-byte payload contribute a fixed base sum; a
        // payload word equal to the ones-complement of that folded base forces the folded total to
        // all-ones, so the computed checksum is zero.
        var source = IPAddress.Parse("192.0.2.53");
        var destination = IPAddress.Parse("192.0.2.10");
        const ushort sourcePort = 53;
        const ushort destinationPort = 53000;
        const int udpLength = 10;

        var udpHeader = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(udpHeader.AsSpan(0, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(udpHeader.AsSpan(2, 2), destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(udpHeader.AsSpan(4, 2), udpLength);
        // checksum field stays zero during computation

        var baseSum = Sum(source.GetAddressBytes()) + Sum(destination.GetAddressBytes()) + 17u + (ushort)udpLength + Sum(udpHeader);
        var folded = Fold(baseSum);
        var payloadWord = folded == 0 ? (ushort)0xFFFF : (ushort)(0xFFFF - folded);
        var payload = new[] { (byte)(payloadWord >> 8), (byte)payloadWord };

        Assert.True(UdpFrameBuilder.TryBuild(source, sourcePort, destination, destinationPort, payload, s_macA, s_macB, out var frame));
        Assert.Equal((ushort)0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(34 + 6, 2)));

        // The transmitted 0xFFFF still validates when the receiver folds the whole datagram.
        var validationSum = Sum(source.GetAddressBytes()) + Sum(destination.GetAddressBytes()) + 17u + (ushort)udpLength + Sum(frame.AsSpan(34, udpLength));
        Assert.Equal((ushort)0, Finish(validationSum));
    }

    [Fact]
    public void Ipv6FrameParsesAndChecksumValidatesWithPseudoHeader()
    {
        var source = IPAddress.Parse("2001:db8::53");
        var destination = IPAddress.Parse("2001:db8::10");
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };

        Assert.True(UdpFrameBuilder.TryBuild(source, 53, destination, 53000, payload, s_macA, s_macB, out var frame));
        Assert.True(IPUdpPacket.TryParse(frame, out var udp));

        Assert.Equal(source, udp.SourceAddress);
        Assert.Equal(destination, udp.DestinationAddress);
        Assert.Equal((ushort)53, udp.SourcePort);
        Assert.Equal((ushort)53000, udp.DestinationPort);
        Assert.Equal(payload, udp.Payload.ToArray());
        Assert.Equal(40, udp.IPHeaderLength);

        var udpLength = 8 + payload.Length;
        var sum = Sum(source.GetAddressBytes()) + Sum(destination.GetAddressBytes()) + 17u + (uint)udpLength + Sum(frame.AsSpan(54, udpLength));
        Assert.Equal((ushort)0, Finish(sum));
    }

    [Fact]
    public void RejectsMismatchedAddressFamilies()
    {
        Assert.False(UdpFrameBuilder.TryBuild(IPAddress.Parse("192.0.2.53"), 53, IPAddress.Parse("2001:db8::10"), 53000, new byte[] { 1 }, s_macA, s_macB, out _));
        Assert.False(UdpFrameBuilder.TryBuild(IPAddress.Parse("2001:db8::53"), 53, IPAddress.Parse("192.0.2.10"), 53000, new byte[] { 1 }, s_macA, s_macB, out _));
    }

    [Fact]
    public void RejectsBadMacLengths()
    {
        Assert.False(UdpFrameBuilder.TryBuild(IPAddress.Parse("192.0.2.53"), 53, IPAddress.Parse("192.0.2.10"), 53000, new byte[] { 1 }, new byte[] { 1, 2, 3, 4, 5 }, s_macB, out _));
        Assert.False(UdpFrameBuilder.TryBuild(IPAddress.Parse("192.0.2.53"), 53, IPAddress.Parse("192.0.2.10"), 53000, new byte[] { 1 }, s_macA, new byte[] { 1, 2, 3, 4, 5, 6, 7 }, out _));
        Assert.False(UdpFrameBuilder.TryBuild(IPAddress.Parse("192.0.2.53"), 53, IPAddress.Parse("192.0.2.10"), 53000, new byte[] { 1 }, ReadOnlySpan<byte>.Empty, s_macB, out _));
    }

    [Fact]
    public void RejectsOversizedFrame()
    {
        var payload = new byte[UdpFrameBuilder.MaximumEthernetFrame];
        Assert.False(UdpFrameBuilder.TryBuild(IPAddress.Parse("192.0.2.53"), 53, IPAddress.Parse("192.0.2.10"), 53000, payload, s_macA, s_macB, out _));
    }

    [Theory]
    [InlineData(1514)]
    [InlineData(9014)]
    public void FrameBuilderHonorsConfigurableCapBoundary(int cap)
    {
        // M3: the pinned native frame cap is injectable (1514 vs a jumbo 9014 ABI). For IPv4 the
        // rebuilt frame is ethernet(14) + ip(20) + udp(8) + payload, so a payload of cap - 42 fits
        // exactly and cap - 41 exceeds it. The cap must not be a hard-coded 1514 magic number.
        var fits = cap - 42;
        var overflow = cap - 41;
        Assert.True(UdpFrameBuilder.TryBuild(IPAddress.Parse("192.0.2.53"), 53, IPAddress.Parse("192.0.2.10"), 53000, new byte[fits], s_macA, s_macB, out _, cap));
        Assert.False(UdpFrameBuilder.TryBuild(IPAddress.Parse("192.0.2.53"), 53, IPAddress.Parse("192.0.2.10"), 53000, new byte[overflow], s_macA, s_macB, out _, cap));
    }

    [Theory]
    [InlineData("192.0.2.53", "192.0.2.10")]
    [InlineData("2001:db8::53", "2001:db8::10")]
    public void TryBuildIntoMatchesTheAllocatingTryBuildByteForByte(string source, string destination)
    {
        // R4: the span-writing overload must produce byte-identical frames so the pooled
        // reinjection path shares one header/checksum code path with the allocating builder.
        var payload = new byte[] { 0xde, 0xad, 0xbe, 0xef, 0x01 };
        var sourceAddress = IPAddress.Parse(source);
        var destinationAddress = IPAddress.Parse(destination);

        Assert.True(UdpFrameBuilder.TryBuild(sourceAddress, 53, destinationAddress, 53000, payload, s_macA, s_macB, out var frame));
        var storage = new byte[9014];
        Assert.True(UdpFrameBuilder.TryBuildInto(
            WinForward.Core.IPAddressValue.From(sourceAddress), 53,
            WinForward.Core.IPAddressValue.From(destinationAddress), 53000,
            payload, s_macA, s_macB, storage, out var frameLength));

        Assert.Equal(frame.Length, frameLength);
        Assert.True(storage.AsSpan(0, frameLength).SequenceEqual(frame));
    }

    [Fact]
    public void TryBuildIntoRejectsShortDestinationAndInvalidInputs()
    {
        var payload = new byte[] { 1, 2, 3 };
        var storage = new byte[9014];

        // Destination shorter than the computed frame.
        Assert.False(UdpFrameBuilder.TryBuildInto(
            WinForward.Core.IPAddressValue.From(IPAddress.Parse("192.0.2.53")), 53,
            WinForward.Core.IPAddressValue.From(IPAddress.Parse("192.0.2.10")), 53000,
            payload, s_macA, s_macB, storage.AsSpan(0, 40), out var length));
        Assert.Equal(0, length);

        // MAC length rejections shared with the allocating overload.
        Assert.False(UdpFrameBuilder.TryBuildInto(
            WinForward.Core.IPAddressValue.From(IPAddress.Parse("192.0.2.53")), 53,
            WinForward.Core.IPAddressValue.From(IPAddress.Parse("192.0.2.10")), 53000,
            payload, new byte[] { 1, 2, 3, 4, 5 }, s_macB, storage, out _));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task UdpResponseReinjectorHonorsConfiguredFrameCap()
    {
        // M3: the reinjector threads the configured cap into frame building; a payload over the cap
        // is dropped fail-closed, never injected.
        var reinjector = new FakeReinjector();
        var sink = new UdpResponseReinjector(reinjector, (nint)7, s_macA, maximumFrameSize: 1514);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Host);

        var payload = new byte[1514 - 41]; // 1 byte over the 1514 cap for an IPv4 frame
        await sink.InjectAsync(flow, server, payload, null, CancellationToken.None);

        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
    }

    // ---- UdpResponseReinjector ----

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task HostFlowResponseInjectsTowardMstcpWithServerAsSource()
    {
        var reinjector = new FakeReinjector();
        var sink = new UdpResponseReinjector(reinjector, (nint)7, s_macA);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Host);
        var payload = new byte[] { 0xca, 0xfe };

        await sink.InjectAsync(flow, server, payload, s_macC, CancellationToken.None);

        Assert.Equal(1, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.True(IPUdpPacket.TryParse(reinjector.LastFrame!, out var udp));
        Assert.Equal(server.Address, udp.SourceAddress);
        Assert.Equal(server.Port, udp.SourcePort);
        Assert.Equal(client.Address, udp.DestinationAddress);
        Assert.Equal(client.Port, udp.DestinationPort);
        Assert.Equal(payload, udp.Payload.ToArray());
        // Host flows ignore the recorded client MAC: both header slots carry the host adapter MAC.
        Assert.True(reinjector.LastFrame!.AsSpan(0, 6).SequenceEqual(s_macA));
        Assert.True(reinjector.LastFrame!.AsSpan(6, 6).SequenceEqual(s_macA));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task HostFlowResponseInjectsTowardItsOriginAdapter()
    {
        var reinjector = new FakeReinjector();
        var originHandle = (nint)1234;
        var adapters = new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["wlan-1"] = new(originHandle, s_macB)
        };
        var sink = new UdpResponseReinjector(reinjector, (nint)7, s_macA, adaptersByStableId: adapters);
        var adapter = new AdapterContext("WLAN-1", "Wi-Fi", 3);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Host, adapter);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, s_macC, CancellationToken.None);

        Assert.Equal(1, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.Equal(originHandle, reinjector.LastAdapterHandle);
        Assert.Equal(NdisApiAbi.PacketFlagOnReceive, reinjector.LastDeviceFlags);
        Assert.True(reinjector.LastFrame!.AsSpan(0, 6).SequenceEqual(s_macB));
        Assert.True(reinjector.LastFrame!.AsSpan(6, 6).SequenceEqual(s_macB));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task HostFlowWithUnresolvedOriginAdapterUsesFallbackAndWarns()
    {
        var reinjector = new FakeReinjector();
        var logger = new RecordingRuntimeLogger();
        var fallbackHandle = (nint)7;
        var sink = new UdpResponseReinjector(reinjector, fallbackHandle, s_macA, logger: logger);
        var adapter = new AdapterContext("missing-wlan", "Wi-Fi", 3);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Host, adapter);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, null, CancellationToken.None);
        await sink.InjectAsync(flow, server, new byte[] { 2 }, null, CancellationToken.None);

        Assert.Equal(2, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.Equal(fallbackHandle, reinjector.LastAdapterHandle);
        Assert.True(reinjector.LastFrame!.AsSpan(0, 6).SequenceEqual(s_macA));
        Assert.True(reinjector.LastFrame!.AsSpan(6, 6).SequenceEqual(s_macA));
        Assert.Equal(1, logger.WarnCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ForwardedFlowResponseInjectsTowardOriginAdapter()
    {
        var reinjector = new FakeReinjector();
        var originHandle = (nint)1234;
        var adapters = new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["veth-1"] = new(originHandle, s_macB)
        };
        var sink = new UdpResponseReinjector(reinjector, (nint)7, s_macA, adaptersByStableId: adapters);
        var adapter = new AdapterContext("veth-1", "vEthernet 1", 3);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Forwarded, adapter);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, s_macC, CancellationToken.None);

        // H2: a forwarded flow's response must reach the origin adapter, not the host adapter.
        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(1, reinjector.ToAdapterCount);
        Assert.Equal(originHandle, reinjector.LastAdapterHandle);
        // H3: a frame injected toward an adapter is an ON_SEND (interface-bound), not ON_RECEIVE.
        Assert.Equal(NdisApiAbi.PacketFlagOnSend, reinjector.LastDeviceFlags);
        // The rebuilt frame uses the origin adapter's MAC and addresses the recorded client (VM)
        // MAC as its destination: without the client MAC the vSwitch would deliver the response to
        // the host stack and the VM would never receive it (R2).
        Assert.True(reinjector.LastFrame!.AsSpan(0, 6).SequenceEqual(s_macC));
        Assert.True(reinjector.LastFrame!.AsSpan(6, 6).SequenceEqual(s_macB));
        Assert.True(IPUdpPacket.TryParse(reinjector.LastFrame!, out var udp));
        Assert.Equal(server.Address, udp.SourceAddress);
        Assert.Equal(client.Address, udp.DestinationAddress);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ForwardedFlowWithUnresolvedOriginAdapterIsDroppedFailClosed()
    {
        // H2: when a forwarded flow's origin adapter is not in the reinjection map, the response
        // must be dropped fail-closed (with a rate-limited log) rather than sent out the wrong
        // (host) adapter where the VM could never receive it.
        var reinjector = new FakeReinjector();
        // Map intentionally omits "veth-1" so the origin adapter cannot be resolved.
        var logger = new RecordingRuntimeLogger();
        var sink = new UdpResponseReinjector(reinjector, (nint)7, s_macA, logger: logger);
        var adapter = new AdapterContext("veth-1", "vEthernet 1", 3);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Forwarded, adapter);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, null, CancellationToken.None);

        // Fail-closed: no response is sent out any adapter (the VM could never receive it), and the
        // missing-origin is surfaced via a log rather than silently dropped (H2).
        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.Equal(1, logger.WarnCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00 })]
    [SupportedOSPlatform("windows")]
    public async Task ForwardedFlowResponseWithoutValidClientMacIsDroppedFailClosed(byte[]? clientMac)
    {
        // R2: a forwarded response must be rebuilt toward the recorded client MAC. When that MAC is
        // missing or malformed the response cannot reach the VM and must be dropped fail-closed
        // with a rate-limited log, never sent with the host MAC as destination.
        var reinjector = new FakeReinjector();
        var originHandle = (nint)1234;
        var adapters = new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["veth-1"] = new(originHandle, s_macB)
        };
        var logger = new RecordingRuntimeLogger();
        var sink = new UdpResponseReinjector(reinjector, (nint)7, s_macA, adaptersByStableId: adapters, logger: logger);
        var adapter = new AdapterContext("veth-1", "vEthernet 1", 3);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Forwarded, adapter);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, clientMac, CancellationToken.None);

        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.Equal(1, logger.WarnCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task UnbuildableResponseIsDroppedFailClosed()
    {
        var reinjector = new FakeReinjector();
        var sink = new UdpResponseReinjector(reinjector, (nint)7, s_macA);
        var client = Endpoint.From(IPAddress.Parse("2001:db8::10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53); // family mismatch with the flow
        // FlowKey.Create rejects mismatched families, so the key is built directly to reach the
        // frame-builder rejection path inside the sink.
        var flow = new FlowKey(AddressFamilyKind.IPv6, TransportProtocol.Udp, client, server, FlowOriginKind.Host, null, 0);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, null, CancellationToken.None);

        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ResponseInjectionUsesPooledBuffersWithoutPerDatagramManagedAllocation()
    {
        // R4/AC4: the steady-state response path rents pooled native buffers, builds each frame
        // in place, and returns them — no managed byte[] per datagram, and the rented buffer
        // comes back to the pool (a never-returned buffer would leave the pool empty).
        using var pool = new NdisPacketBufferPool();
        var reinjector = new CountingReinjector();
        var sink = new UdpResponseReinjector(reinjector, (nint)7, s_macA, bufferPool: pool);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Host);
        var payload = new byte[64];

        // Warm up the JIT and the pool so the measured loop sees only steady-state behavior.
        for (var index = 0; index < 3; index++) await sink.InjectAsync(flow, server, payload, null, CancellationToken.None);

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int count = 16;
        for (var index = 0; index < count; index++) await sink.InjectAsync(flow, server, payload, null, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(3 + count, reinjector.SendToMstcpCount);
        Assert.Equal(1, pool.Count);
    }

    // ---- Executor UDP wiring ----

    [Fact]
    public async Task ExecutorRoutesUdpProxyDatagramsThroughCoordinatorWithoutReinjection()
    {
        var source = IPAddress.Parse("192.0.2.10");
        var destination = IPAddress.Parse("192.0.2.53");
        var payload = new byte[] { 0x12, 0x34, 0x56 };
        Assert.True(UdpFrameBuilder.TryBuild(source, 53000, destination, 53, payload, s_macA, s_macB, out var frame));

        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        await using var coordinator = new UdpProxyCoordinator(factory, sink);
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector, udpProxy: coordinator);
        var flow = FlowKey.Create(Endpoint.From(source, 53000), Endpoint.From(destination, 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(frame), new FlowContext(flow, null, null, null, null, 53), new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 7));

        await executor.ProxyAsync(packet, s_server, CancellationToken.None);

        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });
        (IPEndPoint Destination, byte[] Payload) sent;
        lock (transport.Sent) sent = transport.Sent[0];
        Assert.Equal(payload, sent.Payload);
        // The original datagram is consumed by the relay forward; it is never reinjected.
        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
    }

    [Fact]
    public async Task ExecutorFailsClosedWhenUdpFrameCannotBeParsed()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new FakeResponseSink());
        var executor = new NdisPacketActionExecutor(new FakeReinjector(), udpProxy: coordinator);
        var flow = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(new byte[] { 0xff, 0xff, 0xff }), new FlowContext(flow, null, null, null, null, 53), new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 7));

        await executor.ProxyAsync(packet, s_server, CancellationToken.None);

        Assert.Empty(factory.Transports);
    }

    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);
}
