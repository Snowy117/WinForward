using System.Buffers.Binary;
using System.Net;
using System.Runtime.Versioning;
using System.Threading.Channels;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class UdpRelayTests
{
    private static readonly byte[] s_macA = [0x02, 0x00, 0x00, 0x00, 0x00, 0x01];
    private static readonly byte[] s_macB = [0x02, 0x00, 0x00, 0x00, 0x00, 0x02];

    // ---- UdpFrameBuilder ----

    [Fact]
    public void Ipv4FrameRoundTripsThroughParser()
    {
        var source = IPAddress.Parse("192.0.2.53");
        var destination = IPAddress.Parse("192.0.2.10");
        var payload = new byte[] { 0xde, 0xad, 0xbe, 0xef };

        Assert.True(UdpFrameBuilder.TryBuild(source, 53, destination, 53000, payload, s_macA, s_macB, out var frame));
        Assert.True(IpUdpPacket.TryParse(frame, out var udp));

        Assert.Equal(source, udp.SourceAddress);
        Assert.Equal(destination, udp.DestinationAddress);
        Assert.Equal((ushort)53, udp.SourcePort);
        Assert.Equal((ushort)53000, udp.DestinationPort);
        Assert.Equal(payload, udp.Payload.ToArray());
        Assert.Equal(20, udp.IpHeaderLength);
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
        Assert.True(IpUdpPacket.TryParse(frame, out var udp));

        Assert.Equal(source, udp.SourceAddress);
        Assert.Equal(destination, udp.DestinationAddress);
        Assert.Equal((ushort)53, udp.SourcePort);
        Assert.Equal((ushort)53000, udp.DestinationPort);
        Assert.Equal(payload, udp.Payload.ToArray());
        Assert.Equal(40, udp.IpHeaderLength);

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

        await sink.InjectAsync(flow, server, payload, CancellationToken.None);

        Assert.Equal(1, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.True(IpUdpPacket.TryParse(reinjector.LastFrame, out var udp));
        Assert.Equal(server.Address, udp.SourceAddress);
        Assert.Equal(server.Port, udp.SourcePort);
        Assert.Equal(client.Address, udp.DestinationAddress);
        Assert.Equal(client.Port, udp.DestinationPort);
        Assert.Equal(payload, udp.Payload.ToArray());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ForwardedFlowResponseInjectsTowardAdapter()
    {
        var reinjector = new FakeReinjector();
        var sink = new UdpResponseReinjector(reinjector, (nint)7, s_macA);
        var adapter = new AdapterContext("veth-1", "vEthernet 1", 3);
        var client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flow = FlowKey.Create(client, server, TransportProtocol.Udp, FlowOriginKind.Forwarded, adapter);

        await sink.InjectAsync(flow, server, new byte[] { 1 }, CancellationToken.None);

        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(1, reinjector.ToAdapterCount);
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

        await sink.InjectAsync(flow, server, new byte[] { 1 }, CancellationToken.None);

        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
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

        var transport = Assert.Single(factory.Transports);
        var sent = Assert.Single(transport.Sent);
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

    private static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var index = 0;
        for (; index + 1 < data.Length; index += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
        if (index < data.Length) sum += (uint)data[index] << 8;
        return sum;
    }

    private static ushort Fold(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)sum;
    }

    private static ushort Finish(uint sum) => (ushort)~Fold(sum);

    private sealed class FakeReinjector : IPacketReinjector
    {
        public int ToAdapterCount { get; private set; }
        public int ToMstcpCount { get; private set; }
        public byte[] LastFrame { get; private set; } = [];

        public void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer)
        {
            ToAdapterCount++;
            LastFrame = buffer.GetFrame().ToArray();
        }

        public void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer)
        {
            ToMstcpCount++;
            LastFrame = buffer.GetFrame().ToArray();
        }
    }

    private sealed class FakeTransportFactory : IUdpProxyTransportFactory
    {
        public List<FakeTransport> Transports { get; } = [];
        private int _nextLocalPort = 40000;

        public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, System.Net.Sockets.AddressFamily addressFamily, CancellationToken cancellationToken)
        {
            // Each transport models a distinct bound UDP socket, so its local port is unique; the
            // relay alias collision guard in UdpProxyCoordinator must not reject distinct flows.
            var transport = new FakeTransport(addressFamily, Interlocked.Increment(ref _nextLocalPort));
            lock (Transports) Transports.Add(transport);
            return ValueTask.FromResult<IUdpProxyTransport>(transport);
        }
    }

    private sealed class FakeTransport : IUdpProxyTransport
    {
        public FakeTransport(System.Net.Sockets.AddressFamily addressFamily, int localPort)
        {
            var loopback = addressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            LocalEndpoint = new IPEndPoint(loopback, localPort);
            RelayEndpoint = new IPEndPoint(loopback, 50000);
        }

        public IPEndPoint RelayEndpoint { get; }
        public IPEndPoint LocalEndpoint { get; }
        public bool IsDisposed { get; private set; }
        public List<(IPEndPoint Destination, byte[] Payload)> Sent { get; } = [];
        public Channel<Socks5UdpDatagram> Responses { get; } = Channel.CreateUnbounded<Socks5UdpDatagram>();

        public ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            lock (Sent) Sent.Add((destination, payload.ToArray()));
            return ValueTask.CompletedTask;
        }

        public ValueTask<Socks5UdpDatagram> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            // A disposed transport models a closed socket: the pump's pending receive must end
            // promptly instead of blocking forever, mirroring the real socket's throw.
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return Responses.Reader.ReadAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            Responses.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeResponseSink : IUdpResponseSink
    {
        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}