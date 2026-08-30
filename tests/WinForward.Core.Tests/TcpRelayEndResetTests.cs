using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.FrameBuilders;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// R1/D1: a relay that ends stalled or faulted mid-flow must surface the end to the client as an
/// in-window RST|ACK injected before session teardown (while the association still holds the SYN
/// template and sequence trackers); a clean end already propagated FINs and must not reset. The
/// end-kind derivation itself is pinned on the real relay through all three terminal paths.
/// </summary>
public sealed class TcpRelayEndResetTests
{
    private static readonly IPAddress s_clientIpv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destIpv4 = IPAddress.Parse("192.0.2.53");

    [Fact]
    public async Task FaultedRelayEndInjectsInWindowClientResetBeforeTeardown()
    {
        var relay = new EndKindRelay { EndKind = RelayEndKind.Faulted };
        var (acceptor, injector, order, session, listener) = CreateAcceptor(relay);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        relay.Fault(new IOException("upstream reset"));
        await WaitForAsync(() => order.Count == 2);

        AssertResetFromTrackers(injector);
        Assert.Equal(new[] { "reset", "teardown" }, order);
        session.Retire();
        await acceptLoop;
    }

    [Fact]
    public async Task StalledRelayEndInjectsClientResetBeforeTeardown()
    {
        var relay = new EndKindRelay { EndKind = RelayEndKind.Stalled };
        var (acceptor, injector, order, session, listener) = CreateAcceptor(relay);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        relay.Complete();
        await WaitForAsync(() => order.Count == 2);

        AssertResetFromTrackers(injector);
        Assert.Equal(new[] { "reset", "teardown" }, order);
        session.Retire();
        await acceptLoop;
    }

    [Fact]
    public async Task CleanRelayEndDoesNotInjectClientReset()
    {
        var relay = new EndKindRelay { EndKind = RelayEndKind.CleanEnded };
        var (acceptor, injector, order, session, listener) = CreateAcceptor(relay);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        relay.Complete();
        await WaitForAsync(() => order.Count == 1);

        Assert.Empty(injector.Frames);
        Assert.Equal(new[] { "teardown" }, order);
        session.Retire();
        await acceptLoop;
    }

    [Fact]
    public async Task RelayWithoutEndInfoIsTreatedAsCleanEnd()
    {
        var relay = new FaultableRelay();
        var (acceptor, injector, order, session, listener) = CreateAcceptor(relay);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        relay.Fault(new IOException("legacy relay fault"));
        await WaitForAsync(() => order.Count == 1);

        Assert.Empty(injector.Frames);
        Assert.Equal(new[] { "teardown" }, order);
        session.Retire();
        await acceptLoop;
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RelayEndKindIsCleanEndedWhenBothDirectionsFinish()
    {
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        var (upstreamPeer, relayUpstream) = await CreateSocketPairAsync();
        using var local = localPeer;
        using var upstream = upstreamPeer;
        using var upstreamStream = new NetworkStream(relayUpstream, ownsSocket: true);
        await using var relay = new TcpProxyRelay(relayLocal, upstreamStream, new NoopDisposable());

        local.Shutdown(SocketShutdown.Send);
        upstream.Shutdown(SocketShutdown.Send);
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RelayEndKind.CleanEnded, relay.EndKind);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RelayEndKindIsFaultedWhenPumpFaults()
    {
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        using var local = localPeer;
        await using var relay = new TcpProxyRelay(relayLocal, new FaultingUpstreamStream(), new NoopDisposable());

        await local.SendAsync(new byte[] { 1 }, SocketFlags.None);
        await Assert.ThrowsAsync<IOException>(async () => await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(RelayEndKind.Faulted, relay.EndKind);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RelayEndKindIsStalledWhenPumpStalls()
    {
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        using var local = localPeer;
        await using var relay = new TcpProxyRelay(relayLocal, new StallingUpstreamStream(), new NoopDisposable());

        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RelayEndKind.Stalled, relay.EndKind);
    }

    /// <summary>
    /// Asserts the crafted RST|ACK: original server -> client, seq from the server tracker,
    /// ack from the client tracker, host shape (toward MSTCP).
    /// </summary>
    private static void AssertResetFromTrackers(OrderingInjector injector)
    {
        var reset = Assert.Single(injector.Frames);
        Assert.True(reset.TowardMstcp);
        var frame = reset.Frame;
        Assert.Equal(s_destIpv4, new IPAddress(frame.AsSpan(26, 4).ToArray()));
        Assert.Equal(443u, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(34, 2)));
        Assert.Equal(s_clientIpv4, new IPAddress(frame.AsSpan(30, 4).ToArray()));
        Assert.Equal(53000u, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(36, 2)));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(38, 4)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(42, 4)));
        Assert.Equal(0x14, frame[47]);
    }

    private static (TcpRedirectAcceptor Acceptor, OrderingInjector Injector, List<string> Order, TcpRedirectSession Session, FakeListener Listener) CreateAcceptor(ITcpRelay relay)
    {
        var session = CreateSessionWithObservedSequences(out var listener);
        var order = new List<string>();
        var injector = new OrderingInjector(order);
        var logger = new RecordingRuntimeLogger();
        var acceptor = new TcpRedirectAcceptor(
            new InlineRelayFactory(relay),
            logger,
            new ClientResetInjector(injector, logger, _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask),
            tryAttachRelay: (_, _) => true,
            tearDownSession: _ =>
            {
                order.Add("teardown");
                return ValueTask.CompletedTask;
            });
        return (acceptor, injector, order, session, listener);
    }

    /// <summary>
    /// A host-shape session whose association carries the full reset inputs: the original SYN
    /// template plus both ISNs, and trackers advanced past data (client next seq 7, server next
    /// seq 10) so the reset must use the tracked values, not the ISN+1 fallbacks.
    /// </summary>
    private static TcpRedirectSession CreateSessionWithObservedSequences(out FakeListener listener)
    {
        var key = FlowKey.Create(Endpoint.From(s_clientIpv4, 53000), Endpoint.From(s_destIpv4, 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var association = new TcpRedirectAssociation(key, key.Remote, new AdapterContext("eth0", "Ethernet", 1), 0x1234, Endpoint.From(IPAddress.Loopback, 40000), null, 1, DateTimeOffset.UtcNow);

        TcpSequenceObservation.RecordClientSyn(BuildIpv4TcpSyn(s_clientIpv4, s_destIpv4, 53000, 443), association);
        var synAck = BuildIpv4TcpSyn(s_destIpv4, s_clientIpv4, 443, 53000);
        synAck[47] = 0x12;
        TcpSequenceObservation.RecordServerSynAck(synAck, association);

        var clientData = BuildIpv4TcpSyn(s_clientIpv4, s_destIpv4, 53000, 443, [1, 2, 3, 4, 5]);
        clientData[47] = TcpFlagAck;
        BinaryPrimitives.WriteUInt32BigEndian(clientData.AsSpan(38, 4), 2);
        TcpSequenceObservation.TrackClientSequence(clientData, association);
        var serverData = BuildIpv4TcpSyn(s_destIpv4, s_clientIpv4, 443, 53000, [1, 2, 3, 4, 5, 6, 7, 8]);
        serverData[47] = TcpFlagAck;
        BinaryPrimitives.WriteUInt32BigEndian(serverData.AsSpan(38, 4), 2);
        TcpSequenceObservation.TrackServerSequence(serverData, association);

        listener = new FakeListener(association.TranslatedListenerTuple);
        var token = new SelfTrafficRegistry().Register(new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Tcp, association.TranslatedListenerTuple, association.TranslatedListenerTuple));
        return new TcpRedirectSession(association, listener, token, new Socks5Server("primary", "127.0.0.1", 1080, null, null), CancellationToken.None);
    }

    private static async Task<(Socket Peer, Socket Relay)> CreateSocketPairAsync()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await peer.ConnectAsync((IPEndPoint)server.LocalEndpoint!);
            var relaySide = await server.AcceptSocketAsync();
            return (peer, relaySide);
        }
        finally
        {
            server.Stop();
        }
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InlineRelayFactory(ITcpRelay relay) : ITcpProxyRelayFactory
    {
        public ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, ITcpAcceptedConnection acceptedConnection, Socks5Server server, CancellationToken cancellationToken)
            => ValueTask.FromResult(relay);
    }

    private sealed class EndKindRelay : ITcpRelay, ITcpRelayEndInfo
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => _completion.Task;
        public RelayEndKind EndKind { get; set; }

        public void Complete() => _completion.TrySetResult();
        public void Fault(Exception exception) => _completion.TrySetException(exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FaultableRelay : ITcpRelay
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => _completion.Task;

        public void Fault(Exception exception) => _completion.TrySetException(exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OrderingInjector(List<string> order) : ITcpRedirectInjector
    {
        public List<(byte[] Frame, bool TowardMstcp, nint AdapterHandle)> Frames { get; } = [];

        public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken)
        {
            lock (Frames) Frames.Add((rewrittenFrame.ToArray(), towardMstcp, adapterHandle));
            order.Add("reset");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FaultingUpstreamStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("relay write failed");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WaitForCancellationAsync(cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(WaitForCancellationAsync(cancellationToken));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException(new IOException("relay write failed"));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("relay write failed"));

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    /// <summary>Reads that end in an <see cref="OperationCanceledException"/> the pump token did
    /// not request — the exact classification shape the stall window produces when its
    /// <c>CancelAfter</c> fires on a live pump.</summary>
    private sealed class StallingUpstreamStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new OperationCanceledException());
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
