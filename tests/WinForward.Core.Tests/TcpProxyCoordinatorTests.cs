using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class TcpProxyCoordinatorTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);
    private static readonly IPAddress s_clientIpv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destIpv4 = IPAddress.Parse("192.0.2.53");
    private static readonly IPAddress s_clientIpv6 = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress s_destIpv6 = IPAddress.Parse("2001:db8::53");

    [Fact]
    public async Task SynClaimIsExactlyOnce()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);
        var packet = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);

        var first = await coordinator.HandleSynAsync(packet, s_server, CancellationToken.None);
        var second = await coordinator.HandleSynAsync(packet, s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, first);
        Assert.Equal(TcpRedirectOutcome.Injected, second);
        Assert.Single(listenerFactory.Listeners);
        // A retransmitted SYN re-injects toward the listener; the flow is still one association.
        Assert.Equal(2, injector.InjectedFrames.Count);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public async Task TranslatedTupleAliasCollisionIsRejectedFailClosed()
    {
        var sharedTuple = Endpoint.From(IPAddress.Loopback, 9999);
        var listenerFactory = new FakeListenerFactory(sharedTuple);
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        var first = await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        var second = await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80), s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, first);
        Assert.Equal(TcpRedirectOutcome.Blocked, second);
        Assert.Single(listenerFactory.Listeners, listener => !listener.IsDisposed);
        Assert.Single(injector.InjectedFrames);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public async Task ReversePacketResolvesToOriginalFlow()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacket(listenerTuple.Address, listenerTuple.Port, s_clientIpv4, 53000);
        var outcome = await coordinator.HandleReverseAsync(reverse, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, outcome);
        Assert.Equal(2, injector.InjectedFrames.Count);
    }

    [Fact]
    public async Task CapacityBoundBlocksFailClosed()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable(capacity: 1);
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic, capacity: 1);

        var first = await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        var second = await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.99"), 53001, 80), s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Injected, first);
        Assert.Equal(TcpRedirectOutcome.Blocked, second);
        Assert.Single(listenerFactory.Listeners);
        Assert.Single(injector.InjectedFrames);
    }

    [Fact]
    public async Task ListenerAllocationFailureBlocks()
    {
        var listenerFactory = new FakeListenerFactory(throwOnCreate: true);
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        var outcome = await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Blocked, outcome);
        Assert.Empty(injector.InjectedFrames);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public async Task RelaySetupFailureBlocksAndReleasesAlias()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var relayFactory = new FakeRelayFactory(throwOnEstablish: true);
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic);

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        var listener = Assert.Single(listenerFactory.Listeners);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(IPAddress.Loopback, 1111)), CancellationToken.None);

        await WaitForAsync(() => table.Count == 0);

        Assert.Equal(0, table.Count);
        Assert.True(listener.IsDisposed);
    }

    [Fact]
    public async Task SelfTrafficRegistryContainsListenerTupleBeforeInjection()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);

        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var key = FlowKey.Create(listenerTuple, listenerTuple, TransportProtocol.Tcp, FlowOriginKind.Host);
        var context = new FlowContext(key, null, null, null, null, listenerTuple.Port);

        Assert.True(selfTraffic.IsOwned(context));
    }

    [Fact]
    public async Task ConcurrentSynBurstOnOneFlowUsesOneListener()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);
        var packet = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        const int count = 32;

        var tasks = Enumerable.Range(0, count)
            .Select(_ => coordinator.HandleSynAsync(packet, s_server, CancellationToken.None).AsTask())
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);

        Assert.All(outcomes, outcome => Assert.Equal(TcpRedirectOutcome.Injected, outcome));
        Assert.Single(listenerFactory.Listeners);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public async Task ConcurrentSynBurstWithAsyncListenerStaysExactlyOnce()
    {
        // The synchronous fake listener lets the first caller finish before others observe the
        // table, masking the redirect-table exactly-once race. An async listener forces every
        // concurrent caller through the allocation gap before any TryClaim runs, proving the
        // redundant-listener release and re-inject fallback keep the flow single-listener.
        var listenerFactory = new FakeListenerFactory(yieldOnce: true);
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);
        var packet = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        const int count = 16;

        var tasks = Enumerable.Range(0, count)
            .Select(_ => coordinator.HandleSynAsync(packet, s_server, CancellationToken.None).AsTask())
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);

        Assert.All(outcomes, outcome => Assert.Equal(TcpRedirectOutcome.Injected, outcome));
        Assert.Equal(1, table.Count);
        // Only the winning caller's listener survives; the losers' redundant listeners are disposed.
        Assert.Single(listenerFactory.Listeners, listener => !listener.IsDisposed);
    }

    [Fact]
    public async Task IPv4AndIPv6SynsAllocateMatchingFamilyListener()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv6, s_destIpv6, 53001, 443), s_server, CancellationToken.None);

        Assert.Equal(2, listenerFactory.RequestedFamilies.Count);
        Assert.Equal(AddressFamilyKind.IPv4, listenerFactory.RequestedFamilies[0]);
        Assert.Equal(AddressFamilyKind.IPv6, listenerFactory.RequestedFamilies[1]);
    }

    [Fact]
    public async Task ReverseRewriteRestoresOriginalRemoteEndpoint()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        var syn = MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));

        injector.InjectedFrames.Clear();
        var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacket(listenerTuple.Address, listenerTuple.Port, s_clientIpv4, 53000);
        Assert.Equal(TcpRedirectOutcome.Injected, await coordinator.HandleReverseAsync(reverse, CancellationToken.None));

        var injectedFrame = Assert.Single(injector.InjectedFrames).Frame;
        var rewrittenSrcAddress = new IPAddress(injectedFrame.AsSpan(26, 4).ToArray());
        var rewrittenSrcPort = BinaryPrimitives.ReadUInt16BigEndian(injectedFrame.AsSpan(34, 2));
        Assert.Equal(s_destIpv4, rewrittenSrcAddress);
        Assert.Equal(443u, rewrittenSrcPort);
    }

    [Fact]
    public async Task ShutdownDisposesAllSessionsAndListeners()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.53"), 53000, 443), s_server, CancellationToken.None);
        await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.11"), IPAddress.Parse("192.0.2.54"), 53001, 443), s_server, CancellationToken.None);
        await coordinator.HandleSynAsync(MakeSynPacket(IPAddress.Parse("192.0.2.12"), IPAddress.Parse("192.0.2.55"), 53002, 443), s_server, CancellationToken.None);

        await coordinator.DisposeAsync();

        Assert.All(listenerFactory.Listeners, listener => Assert.True(listener.IsDisposed));
    }

    [Fact]
    public async Task ExpiryRemovesIdleAssociations()
    {
        var listenerFactory = new FakeListenerFactory();
        var injector = new FakeInjector();
        var selfTraffic = new SelfTrafficRegistry();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, selfTraffic);

        await coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53000, 443), s_server, CancellationToken.None);
        Assert.Equal(1, table.Count);

        var removed = table.RemoveExpired(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1));

        Assert.Equal(1, removed);
        Assert.Equal(0, table.Count);
    }

    private static CapturedFlowPacket MakeSynPacket(IPAddress client, IPAddress destination, ushort clientPort, ushort destinationPort)
    {
        var frame = client.AddressFamily == AddressFamily.InterNetwork
            ? BuildIpv4TcpSyn(client, destination, clientPort, destinationPort)
            : BuildIpv6TcpSyn(client, destination, clientPort, destinationPort);
        var local = Endpoint.From(client, clientPort);
        var remote = Endpoint.From(destination, destinationPort);
        var key = FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Host);
        var context = new FlowContext(key, "app.exe", null, null, "eth0", destinationPort);
        var lease = new PacketLease(frame);
        return new CapturedFlowPacket(lease, context, new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 0x1234));
    }

    private static CapturedFlowPacket MakeReversePacket(IPAddress source, ushort sourcePort, IPAddress destination, ushort destinationPort)
    {
        var frame = source.AddressFamily == AddressFamily.InterNetwork
            ? BuildIpv4TcpSyn(source, destination, sourcePort, destinationPort)
            : BuildIpv6TcpSyn(source, destination, sourcePort, destinationPort);
        var remote = Endpoint.From(source, sourcePort);
        var local = Endpoint.From(destination, destinationPort);
        var key = FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Host);
        var context = new FlowContext(key, "app.exe", null, null, "eth0", destinationPort);
        var lease = new PacketLease(frame);
        return new CapturedFlowPacket(lease, context, new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnReceive, 0x1234));
    }

    private static byte[] BuildIpv4TcpSyn(IPAddress source, IPAddress destination, ushort sourcePort, ushort destinationPort)
    {
        const int tcpHeaderLength = 20;
        const int totalLength = 20 + tcpHeaderLength;
        var frame = new byte[14 + totalLength];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), (ushort)totalLength);
        frame[23] = 6;
        source.TryWriteBytes(frame.AsSpan(26, 4), out _);
        destination.TryWriteBytes(frame.AsSpan(30, 4), out _);

        const int tcp = 34;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 2, 2), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 4, 4), 0x00000001);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 8, 4), 0x00000000);
        frame[tcp + 12] = 0x50;
        frame[tcp + 13] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 14, 2), 0xffff);

        SetIpv4HeaderChecksum(frame);
        SetIpv4TcpChecksum(frame, tcp, tcpHeaderLength);
        return frame;
    }

    private static byte[] BuildIpv6TcpSyn(IPAddress source, IPAddress destination, ushort sourcePort, ushort destinationPort)
    {
        const int tcpLength = 20;
        var frame = new byte[14 + 40 + tcpLength];
        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), (ushort)tcpLength);
        frame[20] = 6;
        source.TryWriteBytes(frame.AsSpan(22, 16), out _);
        destination.TryWriteBytes(frame.AsSpan(38, 16), out _);

        const int tcp = 54;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 2, 2), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcp + 4, 4), 0x00000002);
        frame[tcp + 12] = 0x50;
        frame[tcp + 13] = 0x02;

        SetIpv6TcpChecksum(frame, tcp, tcpLength);
        return frame;
    }

    private static void SetIpv4HeaderChecksum(byte[] frame)
    {
        const int headerLength = 20;
        frame[24] = 0;
        frame[25] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(24, 2), PacketChecksums.InternetChecksum(frame.AsSpan(14, headerLength)));
    }

    private static void SetIpv4TcpChecksum(byte[] frame, int tcp, int tcpLength)
    {
        frame[tcp + 16] = 0;
        frame[tcp + 17] = 0;
        var sum = Sum(frame.AsSpan(26, 4)) + Sum(frame.AsSpan(30, 4)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcp, tcpLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 16, 2), Finish(sum));
    }

    private static void SetIpv6TcpChecksum(byte[] frame, int tcp, int tcpLength)
    {
        frame[tcp + 16] = 0;
        frame[tcp + 17] = 0;
        var sum = Sum(frame.AsSpan(22, 16)) + Sum(frame.AsSpan(38, 16)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcp, tcpLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 16, 2), Finish(sum));
    }

    private static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var index = 0;
        for (; index + 1 < data.Length; index += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
        if (index < data.Length) sum += (uint)data[index] << 8;
        return sum;
    }

    private static ushort Finish(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
        }
    }

    private sealed class FakeListenerFactory : ITcpRedirectListenerFactory
    {
        private readonly Endpoint? _fixedTuple;
        private readonly bool _throwOnCreate;
        private readonly bool _yieldOnce;
        private int _nextPort = 40000;
        private int _yielded;

        public FakeListenerFactory(Endpoint? fixedTuple = null, bool throwOnCreate = false, bool yieldOnce = false)
        {
            _fixedTuple = fixedTuple;
            _throwOnCreate = throwOnCreate;
            _yieldOnce = yieldOnce;
        }

        public List<FakeListener> Listeners { get; } = [];
        public List<AddressFamilyKind> RequestedFamilies { get; } = [];

        public async ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken cancellationToken)
        {
            if (_throwOnCreate) throw new IOException("listener allocation failed");
            // Force an async gap on the first allocation so concurrent callers for the same flow
            // all observe an empty table before any TryClaim runs, exercising the redirect-table
            // exactly-once path rather than the synchronous fast path.
            if (_yieldOnce && Interlocked.Exchange(ref _yielded, 1) == 0) await Task.Yield();
            RequestedFamilies.Add(addressFamily);
            var loopback = addressFamily == AddressFamilyKind.IPv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            var tuple = _fixedTuple ?? Endpoint.From(loopback, checked((ushort)Interlocked.Increment(ref _nextPort)));
            var listener = new FakeListener(tuple);
            lock (Listeners) Listeners.Add(listener);
            return listener;
        }
    }

    private sealed class FakeListener(Endpoint translatedTuple) : ITcpRedirectListener
    {
        public Endpoint TranslatedTuple { get; } = translatedTuple;
        public Channel<FakeAcceptedConnection> AcceptChannel { get; } = Channel.CreateUnbounded<FakeAcceptedConnection>();
        public bool IsDisposed { get; private set; }

        public async ValueTask<ITcpAcceptedConnection> AcceptAsync(CancellationToken cancellationToken)
        {
            return await AcceptChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAcceptedConnection(Endpoint remoteEndPoint) : ITcpAcceptedConnection
    {
        public Endpoint RemoteEndPoint { get; } = remoteEndPoint;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRelayFactory(bool throwOnEstablish = false) : ITcpProxyRelayFactory
    {
        public List<Endpoint> EstablishedDestinations { get; } = [];

        public ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, ITcpAcceptedConnection acceptedConnection, CancellationToken cancellationToken)
        {
            if (throwOnEstablish) throw new IOException("relay setup failed");
            lock (EstablishedDestinations) EstablishedDestinations.Add(originalDestination);
            return ValueTask.FromResult<ITcpRelay>(new FakeRelay());
        }
    }

    private sealed class FakeRelay : ITcpRelay
    {
        public Task Completion { get; } = new TaskCompletionSource<bool>().Task;
        public bool IsDisposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeInjector : ITcpRedirectInjector
    {
        public List<(byte[] Frame, bool IsOnSend, nint AdapterHandle)> InjectedFrames { get; } = [];

        public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool isOnSend, nint adapterHandle, CancellationToken cancellationToken)
        {
            lock (InjectedFrames) InjectedFrames.Add((rewrittenFrame.ToArray(), isOnSend, adapterHandle));
            return ValueTask.CompletedTask;
        }
    }
}
