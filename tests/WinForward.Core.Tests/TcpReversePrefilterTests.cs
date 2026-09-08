using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// The X1 listener-port prefilter: table reference-count semantics, the coordinator's
/// <see cref="TcpProxyCoordinator.WantsPacket"/> diversion predicate, and the dispatcher's
/// production-shaped warm/candidate routing including the tombstone fall-through theorem (D3).
/// </summary>
public sealed class TcpReversePrefilterTests
{
    private static readonly IPAddress s_client = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destination = IPAddress.Parse("192.0.2.80");

    [Fact]
    public void ClaimMakesListenerPortVisibleBeforeInjection()
    {
        var table = new TcpRedirectTable();

        Assert.False(table.IsReverseCandidatePort(40000));
        Assert.True(TryClaimListener(table, MakeOriginalKey(53000), IPAddress.Loopback, 40000));

        // The count rises inside TryClaim — before the setup pipeline rewrites or injects the
        // SYN — so the listener's first reverse candidate (its SYN-ACK) can never arrive before
        // the port is observable on the warm path.
        Assert.True(table.IsReverseCandidatePort(40000));
    }

    [Fact]
    public void ClaimReuseDoesNotDoubleCountAndIdempotentRemoveDoesNotDoubleRelease()
    {
        var table = new TcpRedirectTable();
        Assert.True(TryClaimListener(table, MakeOriginalKey(53000), IPAddress.Loopback, 40000));
        Assert.True(table.TryResolveByOriginal(MakeOriginalKey(53000), DateTimeOffset.UtcNow, out var association));
        Assert.NotNull(association);

        // A re-claim of the same original flow touches the existing association: no second count.
        Assert.True(TryClaimListener(table, MakeOriginalKey(53000), IPAddress.Loopback, 50000));
        Assert.True(table.IsReverseCandidatePort(40000));
        Assert.False(table.IsReverseCandidatePort(50000));

        Assert.True(table.TryRemove(association));
        Assert.False(table.TryRemove(association));
        Assert.False(table.IsReverseCandidatePort(40000));
    }

    [Fact]
    public void PortIsReferenceCountedAcrossDistinctListenerTuples()
    {
        var table = new TcpRedirectTable();
        Assert.True(TryClaimListener(table, MakeOriginalKey(53000), IPAddress.Loopback, 40000));
        Assert.True(TryClaimListener(table, MakeOriginalKey(53001), IPAddress.Parse("192.0.2.1"), 40000));
        Assert.True(table.IsReverseCandidatePort(40000));

        table.RemoveExpired(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1));

        Assert.Equal(0, table.Count);
        Assert.False(table.IsReverseCandidatePort(40000));
    }

    [Fact]
    public async Task ConcurrentClaimsAndRemovalsKeepTheCountConsistent()
    {
        var table = new TcpRedirectTable();
        const int claims = 64;
        var associations = new TcpRedirectAssociation?[claims];
        await Task.WhenAll(Enumerable.Range(0, claims).Select(index => Task.Run(() =>
        {
            var original = FlowKey.Create(
                Endpoint.From(IPAddress.Parse($"192.0.2.{10 + index / 250}"), checked((ushort)(49_000 + index))),
                Endpoint.From(s_destination, 443),
                TransportProtocol.Tcp, FlowOriginKind.Host);
            Assert.True(TryClaimListener(table, original, IPAddress.Parse($"198.51.100.{1 + index % 250}"), 40000));
            associations[index] = table.TryResolveByOriginal(original, DateTimeOffset.UtcNow, out var found) ? found : null;
        })));

        Assert.True(table.IsReverseCandidatePort(40000));

        await Task.WhenAll(Enumerable.Range(0, claims).Select(index => Task.Run(() => table.TryRemove(associations[index]!))));

        Assert.Equal(0, table.Count);
        Assert.False(table.IsReverseCandidatePort(40000));
    }

    [Fact]
    public async Task WantsPacketRequiresTcpAndAClaimedListenerSourcePort()
    {
        var table = new TcpRedirectTable();
        var listenerFactory = new FakeListenerFactory();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new CompletableRelayFactory(), new FakeInjector(), table, new SelfTrafficRegistry(), new FakeLocalAddressProvider());
        Assert.True(TryClaimListener(table, MakeOriginalKey(53000), IPAddress.Loopback, 40000));

        // Stage (b): TCP with a claimed source port diverts; any other shape stays warm. The UDP
        // rejection holds even when the source port numerically equals the claimed listener port.
        Assert.True(coordinator.WantsPacket(MakePacket(MakeTcpKey(40000))));
        Assert.False(coordinator.WantsPacket(MakePacket(MakeTcpKey(53000))));
        Assert.False(coordinator.WantsPacket(MakePacket(MakeUdpKey(40000))));
    }

    [Fact]
    public async Task WarmEntryPassesUdpAndBlockFlowsWithoutInvokingReverseHandler()
    {
        // AC1: in the production-shaped composition (reverse handler wired), resolved UDP pass
        // and block flows execute on the warm entry — WantsPacket is consulted (the warm entry is
        // its only caller), the full handler never runs, and the decision still executes.
        var executor = new CountingExecutor();
        var table = new TcpRedirectTable();
        var handler = new TablePrefilterReverseHandler(table);
        var config = new ValidatedConfiguration(new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase), new PolicySnapshot([], FlowAction.Pass));
        var blockConfig = new ValidatedConfiguration(new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase), new PolicySnapshot([], FlowAction.Block));
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, reverseHandler: handler);
        var blockDispatcher = new FlowDispatcher(blockConfig, new FakeGuard(), executor, reverseHandler: handler);

        var passKey = MakeUdpKey(53000);
        var blockKey = MakeUdpKey(53001);
        var firstPass = MakePacket(passKey);
        var secondPass = MakePacket(passKey);
        var block = MakePacket(blockKey);
        await dispatcher.DispatchAsync(firstPass, CancellationToken.None);
        await dispatcher.DispatchAsync(secondPass, CancellationToken.None);
        await blockDispatcher.DispatchAsync(block, CancellationToken.None);

        Assert.Equal(2, executor.PassCount);
        Assert.Equal(1, executor.BlockCount);
        Assert.Equal(PacketDisposition.Pass, firstPass.Lease.Disposition);
        Assert.Equal(PacketDisposition.Pass, secondPass.Lease.Disposition);
        Assert.Equal(PacketDisposition.Block, block.Lease.Disposition);
        Assert.Equal(3, handler.WantsCount);
        Assert.Equal(0, handler.HandleCount);
    }

    [Fact]
    public async Task WarmEntryHandlesNonCandidateTcpWithoutInvokingReverseHandler()
    {
        // A TCP packet whose source port is no live listener port: the prefilter declines, so
        // the warm entry resolves and executes the decision. For TCP the slow path always
        // invokes the handler, so HandleCount == 0 proves the slow path never ran.
        var executor = new CountingExecutor();
        var table = new TcpRedirectTable();
        var handler = new TablePrefilterReverseHandler(table);
        Assert.True(TryClaimListener(table, MakeOriginalKey(54000), IPAddress.Loopback, 40000));
        var config = new ValidatedConfiguration(new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase), new PolicySnapshot([], FlowAction.Pass));
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, reverseHandler: handler);

        var nonCandidateKey = MakeTcpKey(53000);
        var first = MakePacket(nonCandidateKey);
        var second = MakePacket(nonCandidateKey);
        await dispatcher.DispatchAsync(first, CancellationToken.None);
        // The new-flow claim legitimately runs the slow path, which consults the full handler
        // for TCP exactly as before (AC2): one call, answered NotRelevant.
        var handledAfterFirst = handler.HandleCount;
        var wantsAfterFirst = handler.WantsCount;
        Assert.Equal(1, handledAfterFirst);
        await dispatcher.DispatchAsync(second, CancellationToken.None);

        Assert.Equal(2, executor.PassCount);
        Assert.Equal(PacketDisposition.Pass, first.Lease.Disposition);
        Assert.Equal(PacketDisposition.Pass, second.Lease.Disposition);
        // For a resolved TCP flow the slow path would invoke the handler, so an unchanged handle
        // count after the second dispatch proves it rode the warm entry.
        Assert.Equal(wantsAfterFirst + 1, handler.WantsCount);
        Assert.Equal(handledAfterFirst, handler.HandleCount);
    }

    [Fact]
    public async Task CandidateTcpStillDivertsToReverseHandlerExactlyAsBefore()
    {
        // AC2/AC3: a TCP packet whose source port is a claimed listener port diverts to the slow
        // path where the full handler runs — the reverse routing behavior is unchanged.
        var executor = new CountingExecutor();
        var table = new TcpRedirectTable();
        var handler = new TablePrefilterReverseHandler(table);
        Assert.True(TryClaimListener(table, MakeOriginalKey(53000), IPAddress.Loopback, 40000));
        var config = new ValidatedConfiguration(new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase), new PolicySnapshot([], FlowAction.Pass));
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), executor, reverseHandler: handler);

        var candidateKey = MakeTcpKey(40000);
        var first = MakePacket(candidateKey);
        var second = MakePacket(candidateKey);
        await dispatcher.DispatchAsync(first, CancellationToken.None);
        var handledAfterFirst = handler.HandleCount;
        await dispatcher.DispatchAsync(second, CancellationToken.None);

        Assert.Equal(2, executor.PassCount);
        Assert.Equal(handledAfterFirst + 1, handler.HandleCount);
    }

    [Fact]
    public async Task TombstoneStragglerFallsThroughPrefilterToGraceDrop()
    {
        // D3 pin: after teardown the listener port left the prefilter, so the reverse straggler
        // declines the diversion — yet the warm entry cannot resolve the listener-shaped tuple,
        // falls through to the slow path, and the full handler still lands the grace drop.
        var harness = CreateDispatcherHarness();
        await using var coordinator = harness.Coordinator;
        await EstablishRelayingSessionAsync(harness);
        var listenerTuple = Assert.Single(harness.ListenerFactory.Listeners).TranslatedTuple;
        Assert.True(harness.Table.IsReverseCandidatePort(listenerTuple.Port));

        harness.RelayFactory.Relay!.Complete();
        await WaitForAsync(() => harness.Table.Count == 0);
        Assert.False(harness.Table.IsReverseCandidatePort(listenerTuple.Port));

        harness.Injector.InjectedFrames.Clear();
        var straggler = MakeReversePacketClassifierOrientation(s_client, listenerTuple.Port, IPAddress.Parse("192.0.2.53"), 53000, mutateFrame: f => f[47] = TcpFlagFinAck);
        await harness.Dispatcher.DispatchAsync(straggler, CancellationToken.None);

        Assert.Equal(PacketDisposition.ProxyConsumed, straggler.Lease.Disposition);
        Assert.Empty(harness.Injector.InjectedFrames);
    }

    private static FlowKey MakeOriginalKey(ushort clientPort) => MakeTcpKey(clientPort);

    private static FlowKey MakeTcpKey(ushort localPort) => FlowKey.Create(Endpoint.From(s_client, localPort), Endpoint.From(s_destination, 443), TransportProtocol.Tcp, FlowOriginKind.Host);

    private static FlowKey MakeUdpKey(ushort localPort) => FlowKey.Create(Endpoint.From(s_client, localPort), Endpoint.From(s_destination, 53), TransportProtocol.Udp, FlowOriginKind.Host);

    private static FlowContext MakeContext(FlowKey key) => new(key, null, null, key.OriginAdapterId, null, key.Remote.Port);

    private static CapturedFlowPacket MakePacket(FlowKey key) => new(new PacketLease(new byte[] { 1 }), MakeContext(key));

    private static bool TryClaimListener(TcpRedirectTable table, FlowKey originalKey, IPAddress listenerAddress, ushort listenerPort)
    {
        var translated = Endpoint.From(listenerAddress, listenerPort);
        return table.TryClaim(originalKey, originalKey.Remote, new AdapterContext("eth0", "eth0", 1), 0x1234, translated, null, DateTimeOffset.UtcNow, out _);
    }

    private sealed class CountingExecutor : IPacketActionExecutor
    {
        public int PassCount { get; private set; }
        public int BlockCount { get; private set; }
        public ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) { PassCount++; return ValueTask.CompletedTask; }
        public ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) { BlockCount++; return ValueTask.CompletedTask; }
        public ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A reverse handler running the real predicate path (protocol gate + a real
    /// <see cref="TcpRedirectTable"/> port array) so dispatcher tests observe exactly what the
    /// production coordinator would answer.
    /// </summary>
    private sealed class TablePrefilterReverseHandler(TcpRedirectTable table) : ITcpReverseHandler
    {
        private int _wantsCount;
        private int _handleCount;
        public int WantsCount => _wantsCount;
        public int HandleCount => _handleCount;

        public bool WantsPacket(in CapturedFlowPacket packet)
        {
            Interlocked.Increment(ref _wantsCount);
            var key = packet.Context.Key;
            return key.Protocol == TransportProtocol.Tcp && table.IsReverseCandidatePort(key.Local.Port);
        }

        public ValueTask<TcpRedirectOutcome> HandleReverseIfApplicableAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _handleCount);
            return ValueTask.FromResult(TcpRedirectOutcome.NotRelevant);
        }
    }
}
