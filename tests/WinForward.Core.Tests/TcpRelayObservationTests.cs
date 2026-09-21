using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;

namespace WinForward.Core.Tests;

/// <summary>
/// S3/F2: fault observation is intrinsic to the pump body — a faulting pump records the fault and
/// reports it as a pump result, so an abandoned pump can never surface as an unobserved task
/// exception. The acceptor's attach-failure branch observes a discarded relay by disposing it, and
/// the `tcp.relay.faulted` debug event is emitted from the pump's own catch.
/// </summary>
public sealed class TcpRelayObservationTests
{
    [Fact]
    public async Task AttachFailureDisposesTheDiscardedRelay()
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

        // Observation is now the relay's own dispose: a discarded relay must have been disposed,
        // so its completion is awaited by construction rather than by an attached observer.
        Assert.True(relay.IsDisposed);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PumpFaultFaultsCompletionAndEmitsExactlyOneFaultEvent()
    {
        // F2 fault injection: a genuine pump failure still faults the relay completion and is
        // recorded as the `tcp.relay.faulted` debug event — the event did not die with the
        // deleted external observer, it moved into the pump's own catch.
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        using var local = localPeer;
        var logger = new RecordingRuntimeLogger();
        var pumpFault = new IOException("relay write failed");
        var relay = new TcpProxyRelay(relayLocal, new FaultingUpstreamStream(pumpFault), new NoopDisposable(), logger);

        await local.SendAsync(new byte[] { 1 }, SocketFlags.None);

        // The pump's own exception instance reaches the completion unwrapped: it is the recorded
        // fault that RunPumpAsync surfaces, not a wrapper or a freshly built exception.
        var observed = await Assert.ThrowsAsync<IOException>(async () => await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(pumpFault, observed);
        AssertExactlyOneFaultedEvent(logger);

        await relay.DisposeAsync();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DisposingARelayObservesItsFaultedCompletionSoItNeverEscapes()
    {
        // The deleted external observer's remaining job — never let a faulted completion go
        // unobserved — is now the relay's own dispose. A pump fault is recorded and reported as a
        // result rather than thrown, so Completion is the only faultable task a relay owns, and a
        // foreign observer must be unnecessary. The probe is filtered to the injected fault, and it
        // is the only reader of that completion in this test: if the disposal observation regresses,
        // the faulted task becomes collectable and this count turns non-zero.
        var probe = new UnobservedExceptionProbe();
        TaskScheduler.UnobservedTaskException += probe.OnUnobserved;
        try
        {
            var pumpFault = new IOException("relay write failed");
            probe.Track(pumpFault);

            await FaultAndDisposeRelayAsync(pumpFault);

            UnobservedExceptionProbe.ForceFinalization();
            Assert.Equal(0, probe.Count);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= probe.OnUnobserved;
        }
    }

    /// <summary>
    /// Faults a relay's pump and disposes it without any test-side read of its completion. The relay
    /// stays a local of this helper so it is unreachable — and therefore collectable — once it
    /// returns.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task FaultAndDisposeRelayAsync(IOException pumpFault)
    {
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        using var local = localPeer;
        var logger = new RecordingRuntimeLogger();
        var relay = new TcpProxyRelay(relayLocal, new FaultingUpstreamStream(pumpFault), new NoopDisposable(), logger);

        await local.SendAsync(new byte[] { 1 }, SocketFlags.None);
        await WaitForAsync(() => logger.Events.Any(e => string.Equals(e.Name, "tcp.relay.faulted", StringComparison.Ordinal)));

        await relay.DisposeAsync();
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
        await using var upstreamStream = new NetworkStream(relayUpstream, ownsSocket: true);
        var logger = new RecordingRuntimeLogger();
        var relay = new TcpProxyRelay(relayLocal, upstreamStream, new NoopDisposable(), logger);

        local.Shutdown(SocketShutdown.Send);
        upstream.Shutdown(SocketShutdown.Send);
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await relay.DisposeAsync();

        Assert.DoesNotContain(logger.Events, e => string.Equals(e.Name, "tcp.relay.faulted", StringComparison.Ordinal));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task FaultEventIsSuppressedWhenDebugLoggingIsDisabled()
    {
        // S3: the production default threshold (info) disables debug, so observation must not
        // depend on the event being emitted — the fault still faults the completion, it simply
        // produces no event.
        var (localPeer, relayLocal) = await CreateSocketPairAsync();
        using var local = localPeer;
        var logger = new RecordingRuntimeLogger(level => level != RuntimeLogLevel.Debug);
        var relay = new TcpProxyRelay(relayLocal, new FaultingUpstreamStream(new IOException("relay write failed")), new NoopDisposable(), logger);

        await local.SendAsync(new byte[] { 1 }, SocketFlags.None);

        await Assert.ThrowsAsync<IOException>(async () => await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain(logger.Events, e => string.Equals(e.Name, "tcp.relay.faulted", StringComparison.Ordinal));

        await relay.DisposeAsync();
    }

    private static void AssertExactlyOneFaultedEvent(RecordingRuntimeLogger logger) =>
        Assert.Equal(1, logger.Events.Count(e => string.Equals(e.Name, "tcp.relay.faulted", StringComparison.Ordinal)
            && e.Fields.Any(field => string.Equals(field.Key, "error", StringComparison.Ordinal))));

    private static TcpRedirectSession CreateSession(out FakeListener listener)
    {
        var association = TcpCoordinatorFakes.CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40000));
        listener = new FakeListener(association.TranslatedListenerTuple);
        return TcpCoordinatorFakes.CreateSession(association, listener);
    }

    private static async Task<(Socket Peer, Socket Relay)> CreateSocketPairAsync()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await peer.ConnectAsync((IPEndPoint)server.LocalEndpoint);
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
        private readonly TaskCompletionSource _completion = new();

        public Task Completion => _completion.Task;
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>An upstream stream whose reads hang until cancelled; any write faults with the injected exception.</summary>
    private sealed class FaultingUpstreamStream(Exception pumpFault) : Stream
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
        public override void Write(byte[] buffer, int offset, int count) => throw pumpFault;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WaitForCancellationAsync(cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(WaitForCancellationAsync(cancellationToken));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException(pumpFault);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(pumpFault);

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
