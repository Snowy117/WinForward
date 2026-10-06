using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.Socks5TestServer;

namespace WinForward.Runtime.Socks5.Tests;

/// <summary>
/// The deferred-handshake contract of <see cref="Socks5ControlConnection"/>: the pipelined dial writes
/// the greeting (and, with credentials, the RFC 1929 message) and reads nothing, the completion step
/// reads and validates the replies exactly once, and neither the stream hand-off's timeout reset nor a
/// non-blocking socket disturbs those asynchronous reads.
/// </summary>
public sealed class Socks5ControlConnectionDeferredHandshakeTests
{
    private static readonly TimeSpan s_budget = TimeSpan.FromSeconds(30);

    // The window a wrongly ordered implementation would have to beat: a SYN issued before the
    // self-traffic callback returned, or a credential write issued before the method-selection read
    // parked, shows up on the observing side within microseconds — far inside this window. Both
    // assertions are one-directional (only a wrong order fails them), so the delay cannot flake a
    // correct implementation.
    private static readonly TimeSpan s_wrongOrderWindow = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task DeferredDialWritesTheGreetingAndReadsNoReply()
    {
        await using var server = new ScriptedHandshakeServer();
        await using var control = await Socks5ControlConnection.ConnectDeferredHandshakeAsync(server.Server, CancellationToken.None).AsTask().WaitAsync(s_budget);

        // The dial returned while the server had written nothing: had it awaited any reply, this task
        // could not have completed. The greeting is exactly the credential-free RFC 1928 frame.
        Assert.Equal(new byte[] { 5, 1, 0 }, await server.Greeting.Task.WaitAsync(s_budget));

        var completion = control.CompleteDeferredHandshakeAsync(CancellationToken.None).AsTask();
        Assert.False(completion.IsCompleted, "no reply byte is on the wire, so the completion cannot have finished");

        // The completion — not the dial — is what consumes the reply.
        await server.ReplyAsync([5, 0]);
        await completion.WaitAsync(s_budget);
    }

    [Fact]
    public async Task DeferredDialWritesTheCredentialMessageAndReadsNoReply()
    {
        await using var server = new ScriptedHandshakeServer(username: "user", password: "secret");
        await using var control = await Socks5ControlConnection.ConnectDeferredHandshakeAsync(server.Server, CancellationToken.None).AsTask().WaitAsync(s_budget);

        // RFC 1928 offers the username/password method, and the RFC 1929 message rides the greeting
        // without waiting for the method-selection reply.
        Assert.Equal(new byte[] { 5, 2, 0, 2 }, await server.Greeting.Task.WaitAsync(s_budget));
        Assert.Equal(new byte[] { 1, 4, (byte)'u', (byte)'s', (byte)'e', (byte)'r', 6, (byte)'s', (byte)'e', (byte)'c', (byte)'r', (byte)'e', (byte)'t' }, await server.Credentials.Task.WaitAsync(s_budget));

        var completion = control.CompleteDeferredHandshakeAsync(CancellationToken.None).AsTask();
        Assert.False(completion.IsCompleted, "no reply byte is on the wire, so the completion cannot have finished");

        await server.ReplyAsync([5, 2]);
        await server.ReplyAsync([1, 0]);
        await completion.WaitAsync(s_budget);
    }

    [Fact]
    public async Task SynchronousDialWritesTheCredentialMessageOnlyAfterTheMethodSelection()
    {
        // The pin for "the split did not move the credential write into the shared write half": the
        // synchronous dial writes the greeting, parks on the method-selection read, and writes the
        // RFC 1929 message only once the server selected that method. The deferred dial above is the
        // deliberate contrast — it writes both without reading anything.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stopServer = new CancellationTokenSource();
        var greetingRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var credentialBytesBeforeSelection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ProbeCredentialsBeforeMethodSelectionAsync(listener, greetingRead, credentialBytesBeforeSelection, stopServer.Token);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var socksServer = new Socks5Server("test", endpoint.Address.ToString(), checked((ushort)endpoint.Port), Username: "user", Password: "secret");

        var dial = Socks5ControlConnection.ConnectAsync(socksServer, CancellationToken.None, perAttemptTimeout: TimeSpan.FromSeconds(30)).AsTask();
        await greetingRead.Task.WaitAsync(s_budget);

        // The dial is parked on the selection read, so a credential byte the probe reads inside this
        // window would be a write the synchronous path must not have issued yet.
        var probe = await Task.WhenAny(credentialBytesBeforeSelection.Task, Task.Delay(s_wrongOrderWindow));
        Assert.NotSame(credentialBytesBeforeSelection.Task, probe);

        await stopServer.CancelAsync();
        await IgnoreExpectedCancellationAsync(server);
        await Assert.ThrowsAsync<IOException>(async () => await dial);
    }

    [Fact]
    public async Task DeferredDialRegistersSelfTrafficBeforeTheConnect()
    {
        await using var server = new ScriptedHandshakeServer();
        using var registrationEntered = new SemaphoreSlim(0);
        using var connectReleased = new SemaphoreSlim(0);
        var registry = new SelfTrafficRegistry();
        IPEndPoint? registeredLocal = null;

        // ReSharper disable AccessToDisposedClosure // The dial task is joined by dial.WaitAsync(s_budget) below, still inside these scopes, so no closure access can reach a disposed server or semaphore on the normal path; on an assertion-failure path the scopes' disposal is this test scaffolding's own backstop.
        var dial = Task.Run(async () => await Socks5ControlConnection.ConnectDeferredHandshakeAsync(
            server.Server,
            CancellationToken.None,
            (local, remote) =>
            {
                var token = registry.Register(new SelfTrafficRegistry.SelfTrafficKey(
                    TransportProtocol.Tcp,
                    Endpoint.From(local.Address, checked((ushort)local.Port)),
                    Endpoint.From(remote.Address, checked((ushort)remote.Port))));
                registeredLocal = local;
                registrationEntered.Release();
                // Hold the dial inside the callback, before its connect: nothing may reach the server
                // while the registration this callback performs is the only thing that has happened.
                connectReleased.Wait();
                return token;
            }).AsTask());
        // ReSharper restore AccessToDisposedClosure

        await registrationEntered.WaitAsync(s_budget);
        var acceptedWhileRegistering = await Task.WhenAny(server.Accepted.Task, Task.Delay(s_wrongOrderWindow));
        Assert.NotSame(server.Accepted.Task, acceptedWhileRegistering);
        connectReleased.Release();

        await using var control = await dial.WaitAsync(s_budget);
        await server.Greeting.Task.WaitAsync(s_budget);

        Assert.NotNull(registeredLocal);
        var local = Endpoint.From(registeredLocal.Address, checked((ushort)registeredLocal.Port));
        var remote = Endpoint.From(IPAddress.Loopback, checked((ushort)server.Port));
        var context = FlowBuilders.Context(FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Host));
        Assert.True(registry.IsOwned(context));

        // The registration is owned by the connection: its disposal releases the tuple.
        // ReSharper disable once DisposeOnUsingVariable // The explicit DisposeAsync is the act under test: the assertion below reads the registry only after the connection's own disposal released the registration; the await using declaration only backstops assertion-failure paths.
        await control.DisposeAsync();
        Assert.False(registry.IsOwned(context));
    }

    [Fact]
    public async Task CompletionAcceptsANoAuthenticationSelectionEvenWhenCredentialsWereWritten()
    {
        // The inline handshake accepts a no-authentication selection whatever credentials are
        // configured, and the completion keeps that decision: the credential message the dial already
        // wrote is then bytes the server chose to ignore, not a completion failure.
        await using var server = new ScriptedHandshakeServer(username: "user", password: "secret");
        await using var control = await Socks5ControlConnection.ConnectDeferredHandshakeAsync(server.Server, CancellationToken.None);
        await server.Greeting.Task.WaitAsync(s_budget);

        await server.ReplyAsync([5, 0]);

        await control.CompleteDeferredHandshakeAsync(CancellationToken.None).AsTask().WaitAsync(s_budget);
    }

    [Theory]
    [InlineData(new byte[] { 5, 0xFF }, "did not accept a configured authentication method")]
    [InlineData(new byte[] { 5, 1 }, "did not accept a configured authentication method")]
    [InlineData(new byte[] { 4, 0 }, "invalid greeting version")]
    public async Task CompletionRejectsWhatTheSynchronousHandshakeAlsoRejects(byte[] selectionReply, string expectedMessage)
    {
        await using var server = new ScriptedHandshakeServer();
        await using var control = await Socks5ControlConnection.ConnectDeferredHandshakeAsync(server.Server, CancellationToken.None);
        await server.Greeting.Task.WaitAsync(s_budget);

        await server.ReplyAsync(selectionReply);

        // The same type and message the inline handshake throws for this condition; the transport
        // wraps this IOException into its own handshake-rejected fault.
        var refusal = await Assert.ThrowsAsync<IOException>(async () => await control.CompleteDeferredHandshakeAsync(CancellationToken.None));
        Assert.Contains(expectedMessage, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionRejectsANonSuccessCredentialReply()
    {
        await using var server = new ScriptedHandshakeServer(username: "user", password: "secret");
        await using var control = await Socks5ControlConnection.ConnectDeferredHandshakeAsync(server.Server, CancellationToken.None);
        await server.Greeting.Task.WaitAsync(s_budget);
        await server.Credentials.Task.WaitAsync(s_budget);

        await server.ReplyAsync([5, 2]);
        await server.ReplyAsync([1, 1]);

        var refusal = await Assert.ThrowsAsync<IOException>(async () => await control.CompleteDeferredHandshakeAsync(CancellationToken.None));
        Assert.Contains("username/password authentication failed", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionIsRefusedAfterTheFirstOne()
    {
        await using var server = new ScriptedHandshakeServer();
        await using var control = await Socks5ControlConnection.ConnectDeferredHandshakeAsync(server.Server, CancellationToken.None);
        await server.Greeting.Task.WaitAsync(s_budget);

        await server.ReplyAsync([5, 0]);
        await control.CompleteDeferredHandshakeAsync(CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () => await control.CompleteDeferredHandshakeAsync(CancellationToken.None));
        Assert.Contains("exactly once", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionIsRefusedOnASynchronouslyAuthenticatedConnection()
    {
        // A connection whose handshake was read inline has no deferred replies: completing it must be
        // refused rather than consume the next server byte as a method selection.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = AcceptGreetingAsync(listener, CancellationToken.None);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var socksServer = new Socks5Server("test", endpoint.Address.ToString(), checked((ushort)endpoint.Port), Username: null, Password: null);
        await using var control = await Socks5ControlConnection.ConnectAsync(socksServer, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await control.CompleteDeferredHandshakeAsync(CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task CompletionAfterDisposeIsRefusedWithoutHanging()
    {
        await using var server = new ScriptedHandshakeServer();
        var control = await Socks5ControlConnection.ConnectDeferredHandshakeAsync(server.Server, CancellationToken.None);
        await server.Greeting.Task.WaitAsync(s_budget);

        await control.DisposeAsync().AsTask().WaitAsync(s_budget);

        var refusal = await Record.ExceptionAsync(async () => await control.CompleteDeferredHandshakeAsync(CancellationToken.None));
        var disposed = Assert.IsType<ObjectDisposedException>(refusal);
        Assert.EndsWith(nameof(Socks5ControlConnection), disposed.ObjectName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeJoinsAParkedCompletion()
    {
        await using var server = new ScriptedHandshakeServer();
        var control = await Socks5ControlConnection.ConnectDeferredHandshakeAsync(server.Server, CancellationToken.None);
        await server.Greeting.Task.WaitAsync(s_budget);
        var completion = control.CompleteDeferredHandshakeAsync(CancellationToken.None);
        Assert.False(completion.IsCompleted);

        await AsTask(control.DisposeAsync()).WaitAsync(s_budget);

        // The quiescence lease made disposal join the parked read, so the fault is settled here instead
        // of surfacing as an unobserved task exception. "Settled" is asserted as a bounded await rather
        // than an instantaneous IsCompleted: the lease is released in the read's finally, and the async
        // state machine settles the returned task a step later, so reading IsCompleted the moment
        // disposal returns races that final step. A disposal that had not joined would leave the read
        // parked on an unanswered reply, and the bounded await below would time out.
        var fault = await Record.ExceptionAsync(async () => await AsTask(completion).WaitAsync(s_budget));
        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract // Record.ExceptionAsync (xunit 2.9.3) is declared Task<Exception> but returns null when the delegate throws nothing — verified at runtime — so '?.' is required: without it this message would throw NullReferenceException on that reachable path instead of naming the fault.
        Assert.True(fault is IOException or ObjectDisposedException, $"unexpected completion fault: {fault?.GetType().Name ?? "none"}");
    }

    private static Task AsTask(ValueTask value) => value.AsTask();

    [Fact]
    public async Task CompletionReadsTheRepliesAfterTheStreamHandOffAndNonBlockingSwitch()
    {
        // The documented order: the hand-off (which resets the per-attempt socket timeouts) and the
        // caller's non-blocking send fast path may both precede the completion, because the completion
        // reads asynchronously and neither socket timeouts nor blocking mode govern an asynchronous
        // read. This test fails if that order is violated.
        await using var server = new ScriptedHandshakeServer(username: "user", password: "secret");
        await using var control = await Socks5ControlConnection.ConnectDeferredHandshakeAsync(server.Server, CancellationToken.None);
        await server.Greeting.Task.WaitAsync(s_budget);
        await server.Credentials.Task.WaitAsync(s_budget);

        var stream = Assert.IsType<NetworkStream>(control.GetUpstreamStream());
        Assert.True(stream.Socket.ReceiveTimeout <= 0);
        Assert.True(stream.Socket.SendTimeout <= 0);
        stream.Socket.Blocking = false;
        Assert.False(stream.Socket.Blocking);

        // Park both reply reads on the non-blocking socket before either reply exists, so they are
        // served by the asynchronous path rather than by bytes that were already buffered.
        var completion = control.CompleteDeferredHandshakeAsync(CancellationToken.None);
        Assert.False(completion.IsCompleted, "no reply byte is on the wire, so the completion cannot have finished");

        await server.ReplyAsync([5, 2]);
        await server.ReplyAsync([1, 0]);
        await AsTask(completion).WaitAsync(s_budget);
    }

    [Fact]
    public async Task CompletionReadsTheRepliesAfterTheTimeoutResetAlone()
    {
        // The narrow form of the same pin, without the non-blocking switch: the hand-off's timeout
        // reset must not be read as an incomplete-handshake failure.
        await using var server = new ScriptedHandshakeServer(username: "user", password: "secret");
        await using var control = await Socks5ControlConnection.ConnectDeferredHandshakeAsync(server.Server, CancellationToken.None);
        await server.Greeting.Task.WaitAsync(s_budget);
        await server.Credentials.Task.WaitAsync(s_budget);

        _ = control.GetUpstreamStream();
        var completion = control.CompleteDeferredHandshakeAsync(CancellationToken.None);
        Assert.False(completion.IsCompleted, "no reply byte is on the wire, so the completion cannot have finished");

        await server.ReplyAsync([5, 2]);
        await server.ReplyAsync([1, 0]);
        await AsTask(completion).WaitAsync(s_budget);
    }

    /// <summary>
    /// The synchronous-path probe: reads the greeting, signals it, and then parks a one-byte read on
    /// the connection — never writing the method-selection reply. Any credential byte the client
    /// writes before that reply therefore completes the probe; a client that waits leaves it parked
    /// until the test cancels.
    /// </summary>
    private static async Task ProbeCredentialsBeforeMethodSelectionAsync(TcpListener listener, TaskCompletionSource<bool> greetingRead, TaskCompletionSource<bool> credentialBytesBeforeSelection, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        // The whole greeting: VER/NMETHODS, then NMETHODS selector bytes. A credential-configured
        // greeting is four bytes, so a fixed three-byte read would leave a selector byte in the
        // buffer and read it back as the credential message.
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var methods = new byte[header[1]];
        await stream.ReadExactlyAsync(methods, cancellationToken).ConfigureAwait(false);
        greetingRead.TrySetResult(true);
        var probe = new byte[1];
        if (await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false) > 0) credentialBytesBeforeSelection.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The scripted server of the deferred-handshake tests: it accepts one control connection, reads
    /// the client's write half — the greeting always, the RFC 1929 message when a credential pair was
    /// configured — and publishes exactly what it read. It writes <b>nothing</b> until the test
    /// releases a scripted reply, so "the dial read no reply" is an observation of the client rather
    /// than a property of a fixture that answers eagerly.
    /// </summary>
    private sealed class ScriptedHandshakeServer : IAsyncDisposable
    {
        private readonly string? _username;
        private readonly string? _password;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly Task _serve;

        public ScriptedHandshakeServer(string? username = null, string? password = null)
        {
            _username = username;
            _password = password;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _serve = ServeAsync(_shutdown.Token);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Socks5Server Server => new("scripted", "127.0.0.1", checked((ushort)Port), _username, _password);

        /// <summary>Completes when a control connection was accepted, i.e. after the client's SYN arrived.</summary>
        public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The exact greeting bytes the client wrote.</summary>
        public TaskCompletionSource<byte[]> Greeting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<byte[]> Credentials { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Queues one reply message for the connection; messages are written in call order.</summary>
        public ValueTask ReplyAsync(byte[] message) => _replies.Writer.WriteAsync(message);

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _serve.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                GC.KeepAlive(exception);
            }

            _shutdown.Dispose();
        }

        private async Task ServeAsync(CancellationToken token)
        {
            try
            {
                using var client = await _listener.AcceptSocketAsync(token).ConfigureAwait(false);
                Accepted.TrySetResult();
                await using var stream = new NetworkStream(client, ownsSocket: false);
                var header = new byte[2];
                await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                var methods = new byte[header[1]];
                await stream.ReadExactlyAsync(methods, token).ConfigureAwait(false);
                Greeting.TrySetResult([.. header, .. methods]);

                if (_username is not null && _password is not null)
                {
                    var credentialHeader = new byte[2];
                    await stream.ReadExactlyAsync(credentialHeader, token).ConfigureAwait(false);
                    var user = new byte[credentialHeader[1]];
                    await stream.ReadExactlyAsync(user, token).ConfigureAwait(false);
                    var secretLength = new byte[1];
                    await stream.ReadExactlyAsync(secretLength, token).ConfigureAwait(false);
                    var secret = new byte[secretLength[0]];
                    await stream.ReadExactlyAsync(secret, token).ConfigureAwait(false);
                    Credentials.TrySetResult([.. credentialHeader, .. user, .. secretLength, .. secret]);
                }

                await foreach (var reply in _replies.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    await stream.WriteAsync(reply, token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                GC.KeepAlive(exception);
            }
        }
    }
}
