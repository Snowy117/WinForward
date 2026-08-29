using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// S3: a faulted relay completion must be observed on every path that discards a relay without
/// awaiting it — the acceptor's attach-failure branch and the relay's own dispose. Both are
/// asserted through the debug event the fault observer emits, so no unobserved-exception
/// finalizer timing is involved.
/// </summary>
public sealed class TcpRelayObservationTests
{
    [Fact]
    public async Task AttachFailureObservesFaultedRelayCompletion()
    {
        var logger = new RecordingRuntimeLogger();
        var relay = new FaultableRelay();
        var session = CreateSession(out var listener);
        var acceptor = new TcpRedirectAcceptor(
            new InlineRelayFactory(relay),
            logger,
            new ClientResetInjector(new FakeInjector(), logger, _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask),
            tryAttachRelay: (_, _) => false,
            tearDownSession: _ => ValueTask.CompletedTask);

        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        await acceptor.RunAcceptLoopAsync(session);

        // The discarded relay was disposed and its completion observed before the fault lands.
        Assert.True(relay.IsDisposed);
        relay.Fault(new IOException("pump died"));
        AssertContainsFaultedEvent(logger);
    }

    [Fact]
    public async Task AttachFailureObservesAlreadyFaultedRelayCompletion()
    {
        // The continuation must also cover a completion that faulted before the discard.
        var logger = new RecordingRuntimeLogger();
        var relay = new FaultableRelay();
        relay.Fault(new IOException("pump died before attach"));
        var session = CreateSession(out var listener);
        var acceptor = new TcpRedirectAcceptor(
            new InlineRelayFactory(relay),
            logger,
            new ClientResetInjector(new FakeInjector(), logger, _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask),
            tryAttachRelay: (_, _) => false,
            tearDownSession: _ => ValueTask.CompletedTask);

        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        await acceptor.RunAcceptLoopAsync(session);

        AssertContainsFaultedEvent(logger);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RelayDisposeObservesFaultedCompletion()
    {
        // Disposing the relay faults its in-flight pump (the local socket dies under the read),
        // so the observation must be hooked before disposal for the fault to stay observed.
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        using var local = localPeer;
        var logger = new RecordingRuntimeLogger();
        var relay = new TcpProxyRelay(relayLocal, new FaultingUpstreamStream(), new NoopDisposable(), logger);

        await relay.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => relay.Completion.WaitAsync(TimeSpan.FromSeconds(2)));

        AssertContainsFaultedEvent(logger);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RelayDisposeObservesNothingWhenCompletionSucceeds()
    {
        // A relay that ends cleanly must not emit a fault event for having been disposed.
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        var (upstreamPeer, relayUpstream) = await CreateSocketPairAsync();
        using var local = localPeer;
        using var upstream = upstreamPeer;
        using var upstreamStream = new NetworkStream(relayUpstream, ownsSocket: true);
        var logger = new RecordingRuntimeLogger();
        var relay = new TcpProxyRelay(relayLocal, upstreamStream, new NoopDisposable(), logger);

        local.Shutdown(SocketShutdown.Send);
        upstream.Shutdown(SocketShutdown.Send);
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await relay.DisposeAsync();

        Assert.DoesNotContain(logger.Events, e => string.Equals(e.Name, "tcp.relay.faulted", StringComparison.Ordinal));
    }

    private static void AssertContainsFaultedEvent(RecordingRuntimeLogger logger) =>
        Assert.Contains(logger.Events, e => string.Equals(e.Name, "tcp.relay.faulted", StringComparison.Ordinal)
            && e.Fields.Any(field => string.Equals(field.Key, "error", StringComparison.Ordinal)));

    private static TcpRedirectSession CreateSession(out FakeListener listener)
    {
        var client = IPAddress.Parse("192.0.2.10");
        var destination = IPAddress.Parse("192.0.2.53");
        var key = FlowKey.Create(Endpoint.From(client, 53000), Endpoint.From(destination, 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var association = new TcpRedirectAssociation(key, key.Remote, new AdapterContext("eth0", "Ethernet", 1), 0x1234, Endpoint.From(IPAddress.Loopback, 40000), null, 1, DateTimeOffset.UtcNow);
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

    private sealed class FaultableRelay : ITcpRelay
    {
        // Inline continuations: Fault() must complete the observation before returning, so the
        // test never races the logger.
        private readonly TaskCompletionSource _completion = new();

        public Task Completion => _completion.Task;
        public bool IsDisposed { get; private set; }

        public void Fault(Exception exception) => _completion.TrySetException(exception);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>An upstream stream whose reads hang until cancelled; any write faults.</summary>
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
}
