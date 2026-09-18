using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.FrameBuilders;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// Allocation gates for the per-packet hot paths (task 09-18 M2, AC2): each gate drives one
/// steady-state packet shape through its production code and asserts the managed-heap delta is
/// exactly zero. The fakes are counting-only (no recording lists, no frame copies) so test
/// scaffolding never pollutes the measurement; every production trace is off (null loggers).
/// </summary>
public sealed class HotPathAllocationGateTests
{
    private static readonly IPAddress ClientIpv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress DestIpv4 = IPAddress.Parse("192.0.2.53");
    private static readonly Socks5Server Server = new("primary", "127.0.0.1", 1080, null, null);

    [Fact]
    public async Task MidFlowRewriteAndInjectAllocatesNoManagedBytes()
    {
        var injector = new CountingInjector();
        var listenerFactory = new FakeListenerFactory();
        var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, new TcpRedirectTable(), new SelfTrafficRegistry(), new FakeLocalAddressProvider());
        await using (coordinator)
        {
            await HandleSynSettledAsync(coordinator, MakeSynPacket(ClientIpv4, DestIpv4, 53000, 443), Server);
            var midFlow = MakeForwardTcpPacket(ClientIpv4, DestIpv4, 53000, 443, TcpFlagAck, payload: [1, 2, 3, 4]);

            // Warm the JIT and the frame pool so the measured loop sees only steady-state behavior.
            for (var warm = 0; warm < 8; warm++) await coordinator.HandlePacketAsync(midFlow, Server, CancellationToken.None);

            var before = GC.GetAllocatedBytesForCurrentThread();
            const int count = 64;
            for (var index = 0; index < count; index++)
            {
                if (await coordinator.HandlePacketAsync(midFlow, Server, CancellationToken.None) != TcpRedirectOutcome.Injected) Assert.Fail("mid-flow reinjection was not injected");
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(8 + count, injector.Calls);
        }
    }

    [Fact]
    public async Task ReverseRewriteAndInjectAllocatesNoManagedBytes()
    {
        var injector = new CountingInjector();
        var listenerFactory = new FakeListenerFactory();
        var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, new TcpRedirectTable(), new SelfTrafficRegistry(), new FakeLocalAddressProvider());
        await using (coordinator)
        {
            await HandleSynSettledAsync(coordinator, MakeSynPacket(ClientIpv4, DestIpv4, 53000, 443), Server);
            var listenerTuple = Assert.Single(listenerFactory.Listeners).TranslatedTuple;
            var synAck = MakeReversePacketClassifierOrientation(ClientIpv4, listenerTuple.Port, DestIpv4, 53000, mutateFrame: frame => frame[47] = 0x12);

            for (var warm = 0; warm < 8; warm++) await coordinator.HandleReverseAsync(synAck, CancellationToken.None);

            var before = GC.GetAllocatedBytesForCurrentThread();
            const int count = 64;
            for (var index = 0; index < count; index++)
            {
                if (await coordinator.HandleReverseAsync(synAck, CancellationToken.None) != TcpRedirectOutcome.Injected) Assert.Fail("reverse reinjection was not injected");
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(8 + count, injector.Calls);
        }
    }

    [Fact]
    public async Task EstablishedUdpDatagramPathAllocatesNoManagedBytes()
    {
        var factory = new CountingTransportFactory();
        await using var coordinator = new UdpProxyCoordinator(factory, new NoopResponseSink());
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector, udpProxy: coordinator);

        var payload = new byte[64];
        Assert.True(UdpFrameBuilder.TryBuild(ClientIpv4, 53000, DestIpv4, 53, payload, [0x02, 0x00, 0x00, 0x00, 0x00, 0x01], [0x02, 0x00, 0x00, 0x00, 0x00, 0x02], out var frame));
        var flow = FlowKey.Create(Endpoint.From(ClientIpv4, 53000), Endpoint.From(DestIpv4, 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(new PacketLease(frame), new FlowContext(flow, null, null, null, null, 53), new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 7));

        // The first datagram arms the background setup (cold, allocates freely); the session is
        // warm once its flush delivers the buffered datagram through the transport.
        await executor.ProxyAsync(packet, Server, CancellationToken.None);
        await WaitForAsync(() => factory.Transport is not null);
        await WaitForAsync(() => factory.Transport!.Sends >= 1);
        for (var warm = 0; warm < 3; warm++) await executor.ProxyAsync(packet, Server, CancellationToken.None);

        var memorySendsBeforeMeasure = factory.Transport!.MemorySends;
        var spanSendsBeforeMeasure = factory.Transport!.SpanSends;
        var before = GC.GetAllocatedBytesForCurrentThread();
        const int count = 64;
        for (var index = 0; index < count; index++) await executor.ProxyAsync(packet, Server, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        // A4/B4 mutation gate: the measured window must ride the span overload exclusively, and
        // the cold setup flush is a span send too (the queue holds native leases, not managed
        // buffers). Reverting HandleUdpProxyAsync to TrySendAsync — which stays at 0 B via the
        // lease's cached materialization — moves every measured send onto MemorySends and fails
        // these counters.
        Assert.Equal(0, factory.Transport!.MemorySends - memorySendsBeforeMeasure);
        Assert.Equal(count, factory.Transport!.SpanSends - spanSendsBeforeMeasure);
        Assert.Equal(0, factory.Transport!.MemorySends);
        Assert.Equal(4 + count, factory.Transport!.SpanSends);
        Assert.Equal(4 + count, factory.Transport!.Sends);
        // The original datagram is consumed by the relay forward; it is never reinjected.
        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
    }

    [Fact]
    public async Task UdpSetupEnqueuePathAllocatesNoManagedBytes()
    {
        // B4: while a flow's setup is stalled, every accepted datagram is copied into a pooled
        // native lease and appended to the bounded setup queue. Once the per-flow bound is reached
        // the drop-oldest steady state rents and releases one lease per datagram, so the measured
        // window must not touch the managed heap (no ToArray, no Queue growth).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new StalledTransportFactory(gate.Task);
        using var pool = new NativeBufferPool(64, capacity: 64);
        await using var coordinator = new UdpProxyCoordinator(factory, new NoopResponseSink(), setupQueuePool: pool, maximumFrameSize: 64);
        var flow = FlowKey.Create(Endpoint.From(ClientIpv4, 53000), Endpoint.From(DestIpv4, 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var payload = new byte[32];

        // The first datagram starts the cold setup; 40 more materialize the Queue<> and reach the
        // 32-packet drop-oldest steady state.
        Assert.True(await coordinator.TrySendAsync(flow, Server, payload, CancellationToken.None));
        for (var warm = 0; warm < 40; warm++) Assert.True(await coordinator.TrySendAsync(flow, Server, payload, CancellationToken.None));

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int count = 128;
        for (var index = 0; index < count; index++) Assert.True(await coordinator.TrySendAsync(flow, Server, payload, CancellationToken.None));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, coordinator.SetupBudgetRejectionCount);
        Assert.True(pool.Stats.Rented > 0);
        gate.TrySetResult();
    }

    [Fact]
    public void Socks5MessageProductionAllocatesNoManagedBytes()
    {
        Span<byte> scratch = stackalloc byte[3 + 255 + 255];
        var ipv4 = IPAddress.Parse("192.0.2.53");
        var ipv6 = IPAddress.Parse("2001:db8::53");

        for (var warm = 0; warm < 8; warm++)
        {
            _ = Socks5Messages.Greeting(credentials: true);
            _ = Socks5Messages.WriteUsernamePassword("user", "secret", scratch);
            _ = Socks5Messages.WriteRequest(Socks5Command.UdpAssociate, IPAddress.Any, 0, scratch);
            _ = Socks5Messages.WriteRequest(Socks5Command.Connect, ipv4, 443, scratch);
            _ = Socks5Messages.WriteRequest(Socks5Command.Connect, ipv6, 443, scratch);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int count = 64;
        for (var index = 0; index < count; index++)
        {
            _ = Socks5Messages.Greeting(credentials: true);
            _ = Socks5Messages.WriteUsernamePassword("user", "secret", scratch);
            _ = Socks5Messages.WriteRequest(Socks5Command.UdpAssociate, IPAddress.Any, 0, scratch);
            _ = Socks5Messages.WriteRequest(Socks5Command.Connect, ipv4, 443, scratch);
            _ = Socks5Messages.WriteRequest(Socks5Command.Connect, ipv6, 443, scratch);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void TcpResetBuildViaSpanAllocatesNoManagedBytes()
    {
        var template = BuildIpv4TcpSyn(ClientIpv4, DestIpv4, 53000, 443);
        Span<byte> destination = stackalloc byte[TcpResetBuilder.MaxResetFrameLength];
        var serverAddress = IPAddressValue.From(DestIpv4);
        var clientAddress = IPAddressValue.From(ClientIpv4);

        for (var warm = 0; warm < 8; warm++)
        {
            Assert.True(TcpResetBuilder.TryBuildReset(template, serverAddress, 443, clientAddress, 53000, 1001, 2002, destination, out _));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int count = 64;
        var totalWritten = 0;
        for (var index = 0; index < count; index++)
        {
            if (!TcpResetBuilder.TryBuildReset(template, serverAddress, 443, clientAddress, 53000, 1001, 2002, destination, out var written)) Assert.Fail("the span reset build failed");
            totalWritten += written;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(count * (14 + 20 + 20), totalWritten);
    }

    [Fact]
    public async Task SynRetentionWithWarmSynCopyPoolAllocatesNoManagedBytes()
    {
        // The retention hot path is the retransmission overwrite: a SYN for a flow whose setup is
        // already pending rents a syn-copy lease, copies the frame into it, and swaps it into the
        // index — all without touching the managed heap. The first-SYN path additionally takes
        // the bounded cold-setup managed copy (exempt from this gate) and spawns the setup task.
        var injector = new CountingInjector();
        var listenerFactory = new GatedListenerFactory();
        using var synCopyPool = new NativeBufferPool(NdisApiAbi.MaximumEthernetFrame, capacity: 8);
        var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), injector, new TcpRedirectTable(), new SelfTrafficRegistry(), new FakeLocalAddressProvider(), synCopyPool: synCopyPool);
        try
        {
            var syn = MakeSynPacket(ClientIpv4, DestIpv4, 53000, 443);
            Assert.Equal(TcpRedirectOutcome.SetupPending, await coordinator.HandleSynAsync(syn, Server, CancellationToken.None));
            await listenerFactory.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            for (var warm = 0; warm < 8; warm++) Assert.Equal(TcpRedirectOutcome.SetupPending, await coordinator.HandleSynAsync(syn, Server, CancellationToken.None));

            var before = GC.GetAllocatedBytesForCurrentThread();
            const int count = 64;
            for (var index = 0; index < count; index++)
            {
                if (await coordinator.HandleSynAsync(syn, Server, CancellationToken.None) != TcpRedirectOutcome.SetupPending) Assert.Fail("the retransmitted SYN was not retained");
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(1, coordinator.PendingSetups.ActiveCount);
            Assert.Equal(1, synCopyPool.Stats.Outstanding);
        }
        finally
        {
            // The setup task parks inside the gated listener factory; release it before disposal
            // so a failed assertion can never strand the listener and hang teardown.
            listenerFactory.Release();
            await coordinator.DrainPendingSetupsAsync();
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task DispatcherWarmFastPathAllocatesNoManagedBytes()
    {
        // AC2: a resolved warm flow riding the non-async DispatchAsync entry must not allocate.
        // The DispatcherBenchmarks ~160 B/op figure is harness allocation (a fresh lease byte[]
        // and FlowContext per iteration), so this gate measures DispatchAsync alone over a
        // pre-built packet. The reverse handler runs the production predicate shape (protocol
        // gate + a real TcpRedirectTable port array) and declines, keeping the UDP pass warm.
        var executor = new FakeExecutor();
        var table = new TcpRedirectTable();
        var handler = new DecliningReverseHandler(table);
        var config = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, reverseHandler: handler);

        var key = FlowKey.Create(Endpoint.From(ClientIpv4, 53000), Endpoint.From(DestIpv4, 53), TransportProtocol.Udp, FlowOriginKind.Host);
        CapturedFlowPacket MakePacket() => new(new PacketLease(new byte[] { 1, 2, 3, 4 }), new FlowContext(key, null, null, key.OriginAdapterId, null, key.Remote.Port));

        // The first dispatch claims the flow (cold, allocates freely); the measured window is warm.
        await dispatcher.DispatchAsync(MakePacket(), CancellationToken.None);
        for (var warm = 0; warm < 8; warm++) await dispatcher.DispatchAsync(MakePacket(), CancellationToken.None);

        // A lease completes once, so every measured dispatch needs its own packet; pre-building
        // them keeps the harness allocation out of the measured window.
        const int count = 256;
        var packets = new CapturedFlowPacket[count];
        for (var index = 0; index < count; index++) packets[index] = MakePacket();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++) await dispatcher.DispatchAsync(packets[index], CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1 + 8 + count, executor.PassCount);
        Assert.Equal(1 + 8 + count, handler.WantsCount);
    }

    /// <summary>
    /// The production reverse-handler stand-in for the dispatcher allocation gate: its predicate
    /// runs the real path (protocol gate + the live <see cref="TcpRedirectTable"/> port array) and
    /// its handling side answers NotRelevant — an answer a UDP pass flow never reaches.
    /// </summary>
    private sealed class DecliningReverseHandler(TcpRedirectTable table) : ITcpReverseHandler
    {
        private int _wantsCount;

        public int WantsCount => Volatile.Read(ref _wantsCount);

        public bool WantsPacket(in CapturedFlowPacket packet)
        {
            Interlocked.Increment(ref _wantsCount);
            var flowKey = packet.Context.Key;
            return flowKey.Protocol == TransportProtocol.Tcp && table.IsReverseCandidatePort(flowKey.Local.Port);
        }

        public ValueTask<TcpRedirectOutcome> HandleReverseIfApplicableAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
            => ValueTask.FromResult(TcpRedirectOutcome.NotRelevant);
    }

    [Fact]
    public void FlowTableClaimAndExpireCycleAllocatesNoManagedBytes()
    {
        // B10: a new-flow claim rents a pooled FlowState (no per-flow allocation) and expiry
        // returns it. Once the dictionaries are pre-sized and the state free list and expiry
        // scratch buffer are warm, a claim + expire cycle must not touch the managed heap.
        const int capacity = 64;
        var table = new FlowTable(capacity);
        var idleTimeout = TimeSpan.FromMinutes(1);
        var lastActivity = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        var keys = new FlowKey[capacity];
        for (var index = 0; index < capacity; index++)
        {
            keys[index] = FlowKey.Create(
                Endpoint.From(ClientIpv4, (ushort)(10_000 + index)),
                Endpoint.From(DestIpv4, 53),
                TransportProtocol.Udp,
                FlowOriginKind.Host);
        }

        // One delegate instance shared by both loops: a per-call-site lambda would allocate its
        // cached delegate on its own first invocation, inside the measured window.
        var decide = () => new FlowDecision(FlowAction.Pass, 0, null);

        for (var warm = 0; warm < capacity; warm++)
        {
            if (!table.TryClaimResolved(keys[warm], decide, out var warmState)) Assert.Fail("the warm claim failed");
            warmState!.Touch(lastActivity);
        }
        if (table.RemoveExpired(lastActivity + TimeSpan.FromMinutes(2), idleTimeout) != capacity) Assert.Fail("the warm expiry did not remove every state");

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int cycles = 256;
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var key = keys[cycle % capacity];
            if (!table.TryClaimResolved(key, decide, out var state)) Assert.Fail("the measured claim failed");
            state!.Touch(lastActivity);
            if (table.RemoveExpired(lastActivity + TimeSpan.FromMinutes(2), idleTimeout) != 1) Assert.Fail("the measured expiry did not remove the state");
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void FlowTableRecyclesExpiredStatesThroughItsPool()
    {
        var table = new FlowTable(capacity: 1);
        var first = FlowKey.Create(Endpoint.From(ClientIpv4, 53_000), Endpoint.From(DestIpv4, 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var second = FlowKey.Create(Endpoint.From(ClientIpv4, 53_001), Endpoint.From(DestIpv4, 53), TransportProtocol.Udp, FlowOriginKind.Host);

        Assert.True(table.TryClaimResolved(first, () => new FlowDecision(FlowAction.Pass, 0, null), out var state));
        state!.Touch(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5));
        Assert.Equal(1, table.RemoveExpired(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));

        Assert.True(table.TryClaimResolved(second, () => new FlowDecision(FlowAction.Block, 1, null), out var recycled));
        Assert.Same(state, recycled);
        Assert.Equal(second, recycled!.Key);
        Assert.Equal(FlowAction.Block, recycled.Decision.Action);
    }

    /// <summary>
    /// A counting-only TCP redirect injector: no frame recording, so the allocation gates measure
    /// the coordinator path alone (the recording fakes copy every frame and would dominate the
    /// measurement).
    /// </summary>
    private sealed class CountingInjector : ITcpRedirectInjector
    {
        private long _calls;

        public long Calls => Interlocked.Read(ref _calls);

        public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Inject(NdisPacketBuffer stagedFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken) => Interlocked.Increment(ref _calls);
    }

    /// <summary>
    /// A counting-only UDP relay transport pair: sends complete synchronously with nothing
    /// recorded, and the receive parks until disposal so the session's receive loop stays quiet
    /// through the measurement.
    /// </summary>
    private sealed class CountingTransportFactory : IUdpProxyTransportFactory
    {
        public CountingTransport? Transport { get; private set; }

        public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            Transport = new CountingTransport();
            return ValueTask.FromResult<IUdpProxyTransport>(Transport);
        }
    }

    /// <summary>A factory whose setup parks on a gate until released (or cancelled by disposal).</summary>
    private sealed class StalledTransportFactory(Task gate) : IUdpProxyTransportFactory
    {
        public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new CountingTransport();
        }
    }

    private sealed class CountingTransport : IUdpProxyTransport
    {
        private readonly TaskCompletionSource<Socks5UdpReceiveResult> _parkedReceive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _memorySends;
        private long _spanSends;

        public IPEndPoint RelayEndpoint { get; } = new(IPAddress.Loopback, 50000);
        public IPEndPoint LocalEndpoint { get; } = new(IPAddress.Loopback, 40000);

        public long Sends => Interlocked.Read(ref _memorySends) + Interlocked.Read(ref _spanSends);

        public long MemorySends => Interlocked.Read(ref _memorySends);

        public long SpanSends => Interlocked.Read(ref _spanSends);

        public ValueTask SendAsync(Endpoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _memorySends);
            return ValueTask.CompletedTask;
        }

        public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _spanSends);
            return ValueTask.CompletedTask;
        }

        public ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => new(_parkedReceive.Task);

        public ValueTask DisposeAsync()
        {
            _parkedReceive.TrySetException(new ObjectDisposedException(nameof(CountingTransport)));
            return ValueTask.CompletedTask;
        }
    }
}
