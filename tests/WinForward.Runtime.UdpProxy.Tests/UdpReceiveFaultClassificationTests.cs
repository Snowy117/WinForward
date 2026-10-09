using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FlowBuilders;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The receive path classifies the fault its session recorded through the same
/// single decision point every other removal path uses. A relay handshake rejected after the
/// transport's setup call returned is the setup failure the flow must cool down for (armed exactly
/// as a refused `UDP ASSOCIATE` is on the native path), an association death stays the counted,
/// cooldown-free <see cref="UdpTeardownReason.AssociationLost"/>, and a non-typed receive fault
/// keeps the generic <see cref="UdpTeardownReason.Fault"/> every native receive fault already
/// landed as — so the shared classification carries no native-behaviour delta.
/// </summary>
[Collection(UdpAssociationLostCounterCollection.Name)]
public sealed class UdpReceiveFaultClassificationTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    [Fact]
    public async Task ARejectedHandshakeDiscoveredOnTheReceivePathArmsTheSetupCooldown()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new ScriptedFaultTransportFactory();
        var logger = new RecordingLogger();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16, TimeProvider = time, Logger = logger });
        var flow = CreateFlow("192.0.2.53");
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        var transport = await ReadyTransportAsync(factory);

        // The transport discovered the rejection after its setup call returned and faults its
        // receive: the flow's slot must leave as a setup failure.
        transport.FaultReceive(new UdpTransportHandshakeRejectedException("The relay rejected the UoT handshake."));
        await WaitForAsync(() => transport.IsDisposed);
        Assert.Equal(0, coordinator.SessionCount);

        // The same observable the setup-failure cooldown tests read: the tombstone is armed, so the
        // next datagram is refused fail-closed at the frozen clock (and the refusal is traced).
        Assert.Equal(1, coordinator.Diagnostics.SetupCooldownCount);
        Assert.False(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [2], default, CancellationToken.None));
        Assert.Equal(1, factory.CreateCalls);
        Assert.Contains(logger.Events, item => string.Equals(item.Name, "udp.setup.cooldown", StringComparison.Ordinal));

        // ... and the flow re-establishes once the cooldown window elapses.
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [3], default, CancellationToken.None));
        await WaitForAsync(() => factory.CreateCalls == 2);
    }

    [Fact]
    public async Task AnAssociationLostDiscoveredOnTheReceivePathIsCountedAndArmsNoCooldown()
    {
        var factory = new ScriptedFaultTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16 });
        var flow = CreateFlow("192.0.2.53");
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        var transport = await ReadyTransportAsync(factory);

        var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);
        transport.FaultReceive(new UdpAssociationLostException("The flow's SOCKS5 UDP association was lost; the flow must be re-established."));
        await WaitForAsync(() => transport.IsDisposed);

        // Classified as the association loss it is (exactly one count: this class shares the
        // counter's collection, so no sibling collection can move it concurrently) ...
        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) - lostBefore);
        // ... never as the setup failure a cooldown is armed for.
        Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);
        Assert.Equal(0, coordinator.SessionCount);

        // The flow is free to re-establish immediately on its next datagram.
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [2], default, CancellationToken.None));
        await WaitForAsync(() => factory.CreateCalls == 2);
    }

    /// <summary>
    /// The native-delta proof: the native transport's receive path surfaces raw
    /// socket faults only, so an untyped receive fault must keep the generic reason — slot removed,
    /// no cooldown armed, nothing counted as an association loss, and the flow free to re-establish
    /// at once. The assertions together are what discriminate it from both typed classes.
    /// </summary>
    [Fact]
    public async Task AnUntypedReceiveFaultKeepsTheGenericFaultReason()
    {
        var factory = new ScriptedFaultTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16 });
        var flow = CreateFlow("192.0.2.53");
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        var transport = await ReadyTransportAsync(factory);

        var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);
        transport.FaultReceive(new SocketException((int)SocketError.ConnectionRefused));
        await WaitForAsync(() => transport.IsDisposed);

        Assert.Equal(0, RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) - lostBefore);
        Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);
        Assert.Equal(0, coordinator.SessionCount);

        // No cooldown: the very next datagram sets the flow up again.
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [2], default, CancellationToken.None));
        await WaitForAsync(() => factory.CreateCalls == 2);
    }

    /// <summary>
    /// The classification on its own, for all three fault classes the transports can raise: the
    /// rejection is the setup failure that arms the cooldown (and is not counted as an association
    /// loss — it is not one), an association death is the counted cooldown-free loss, and every
    /// other exception keeps the generic reason a native receive fault already carried.
    /// </summary>
    [Fact]
    public void TeardownReasonForClassifiesTheThreeFaultClasses()
    {
        var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);

        Assert.Equal(
            UdpTeardownReason.SetupFailure,
            UdpProxyCoordinator.TeardownReasonFor(new UdpTransportHandshakeRejectedException("The relay rejected the UoT handshake.")));
        Assert.Equal(0, RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) - lostBefore);

        Assert.Equal(
            UdpTeardownReason.AssociationLost,
            UdpProxyCoordinator.TeardownReasonFor(new UdpAssociationLostException("The flow's SOCKS5 UDP association was lost; the flow must be re-established.")));
        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) - lostBefore);

        Assert.Equal(UdpTeardownReason.Fault, UdpProxyCoordinator.TeardownReasonFor(new IOException("the relay socket faulted")));
        Assert.Equal(UdpTeardownReason.Fault, UdpProxyCoordinator.TeardownReasonFor(new SocketException((int)SocketError.ConnectionReset)));
        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) - lostBefore);
    }

    /// <summary>
    /// The rejection is the transport's *send* path's fault too (the fail-closed rethrow of the
    /// fault the receive path recorded), so it must arm the cooldown from there as well instead of
    /// leaving the flow free to re-dial at datagram rate.
    /// </summary>
    [Fact]
    public async Task ARejectedHandshakeResurfacedOnASendArmsTheSetupCooldownToo()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new ScriptedFaultTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16, TimeProvider = time });
        var flow = CreateFlow("192.0.2.53");
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        var transport = await ReadyTransportAsync(factory);

        transport.SendFault = new UdpTransportHandshakeRejectedException("The relay rejected the UoT handshake.");
        await Assert.ThrowsAsync<UdpTransportHandshakeRejectedException>(async () =>
            await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [2], default, CancellationToken.None));
        await WaitForAsync(() => transport.IsDisposed);

        Assert.Equal(1, coordinator.Diagnostics.SetupCooldownCount);
        Assert.False(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [3], default, CancellationToken.None));
    }

    /// <summary>
    /// The receive path's classification is exactly-once, and this is the fault shape that could
    /// break it: a transport that holds one fatal fault and hands the very same instance to both
    /// paths (the UoT transport rethrows its recorded fault on every later send). The receive loop
    /// records it first, the removal owns the single classification and count, and no send ever
    /// reaches the fault-carrying send path — the session refuses a datagram once its scope faulted.
    /// </summary>
    [Fact]
    public async Task AFaultHeldForBothPathsIsCountedExactlyOnceByTheReceivePath()
    {
        var factory = new ScriptedFaultTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16 });
        var flow = CreateFlow("192.0.2.53");
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        var transport = await ReadyTransportAsync(factory);

        var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);
        var lost = new UdpAssociationLostException("The flow's SOCKS5 UDP association was lost; the flow must be re-established.");
        // The transport records one fatal fault: its receive faults with it and its send path would
        // refuse with the identical instance.
        transport.SendFault = lost;
        transport.FaultReceive(lost);
        await WaitForAsync(() => transport.IsDisposed);

        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) - lostBefore);
        Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);
        // The single count is the receive path's: the only datagram that ever reached the transport is
        // the setup flush, so the fault-carrying send path was never handed a datagram to refuse with.
        Assert.Single(transport.Sent);
    }

    /// <summary>
    /// The mirror ordering of the fact above: the *send* path is the one that discovers the held
    /// fault first (a datagram admitted before the receive loop recorded it). That classification and
    /// its removal own the single count, and the receive path adds none: the removal disposes the
    /// session, whose cancellation ends the parked receive as normal teardown — and a fault recorded
    /// in that window would still find the slot gone and skip the classification, because the removal
    /// re-checks that the flow's live slot holds this session before it classifies.
    /// </summary>
    [Fact]
    public async Task AFaultDiscoveredByTheSendPathIsNotCountedAgainOnTheReceivePath()
    {
        var factory = new ScriptedFaultTransportFactory();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new FakeResponseSink(), new UdpProxyOptions { Capacity = 16 });
        var flow = CreateFlow("192.0.2.53");
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        var transport = await ReadyTransportAsync(factory);

        var lostBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost);
        var lost = new UdpAssociationLostException("The flow's SOCKS5 UDP association was lost; the flow must be re-established.");
        transport.SendFault = lost;
        Assert.Same(lost, await Assert.ThrowsAsync<UdpAssociationLostException>(async () =>
            await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [2], default, CancellationToken.None)));
        await WaitForAsync(() => transport.IsDisposed);

        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationLost) - lostBefore);
        Assert.Equal(0, coordinator.Diagnostics.SetupCooldownCount);
        Assert.Equal(0, coordinator.SessionCount);
    }

    private static async Task<ScriptedFaultTransport> ReadyTransportAsync(ScriptedFaultTransportFactory factory)
    {
        await WaitForAsync(() => factory.CreateCalls == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() => transport.Sent.Count >= 1);
        return transport;
    }

    /// <summary>
    /// A transport the test drives directly: the receive loop parks on a read the test can fault with
    /// an exact instance (a channel completion would only surface as a <c>ChannelClosedException</c>
    /// wrapping the error, which is not the typed fault under test), and its send path refuses with
    /// the fault the transport holds, the way the UoT transport's fail-closed rethrow does.
    /// </summary>
    private sealed class ScriptedFaultTransportFactory : IUdpProxyTransportFactory
    {
        private int _nextLocalPort = 41_000;
        private int _createCalls;

        public int CreateCalls => Volatile.Read(ref _createCalls);
        public List<ScriptedFaultTransport> Transports { get; } = [];

        public ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
        {
            var transport = new ScriptedFaultTransport(Interlocked.Increment(ref _nextLocalPort));
            lock (Transports) Transports.Add(transport);
            // Counted after the publish, so a test that observed the count is guaranteed the list entry.
            Interlocked.Increment(ref _createCalls);
            return ValueTask.FromResult<IUdpProxyTransport>(transport);
        }
    }

    private sealed class ScriptedFaultTransport(int localPort) : IUdpProxyTransport
    {
        private readonly TaskCompletionSource<UdpTransportReceiveResult> _receive = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPEndPoint PeerEndpoint { get; } = new(IPAddress.Loopback, 50000);
        public IPEndPoint LocalEndpoint { get; } = new(IPAddress.Loopback, localPort);
        public List<byte[]> Sent { get; } = [];
        public bool IsDisposed { get; private set; }

        /// <summary>The fault the send path refuses with, or null while the transport is healthy.</summary>
        public Exception? SendFault { get; set; }

        /// <summary>Faults the session's receive with this exact instance, as the transport's own read would.</summary>
        public void FaultReceive(Exception fault) => _receive.TrySetException(fault);

        public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
        {
            if (SendFault is { } fault) return ValueTask.FromException(fault);
            lock (Sent) Sent.Add(payload.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask<UdpTransportReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            new(_receive.Task.WaitAsync(cancellationToken));

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
