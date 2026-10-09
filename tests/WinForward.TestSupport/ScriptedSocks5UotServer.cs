using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using Xunit.Sdk;

namespace WinForward.TestSupport;

/// <summary>
/// A loopback SOCKS5 server that terminates a UoT v2 connect-mode flow on every accepted
/// connection: it reads the greeting (and the RFC 1929 message when the test constructed it with a
/// credential pair), writes the method selection, reads the <c>CONNECT</c> to
/// <see cref="UotCodec.MagicAddress"/>, reads the UoT request header, and then reads
/// <c>u16be length | payload</c> frames.
/// <para>
/// Its defining property is the order it writes the CONNECT reply in: <b>the reply is written only
/// after the first frame has been read</b>. That makes the client's pipelined flight (greeting,
/// CONNECT request, UoT header, first datagram written without awaiting any reply) an observation
/// rather than a construction — and it turns a client that waits for a handshake reply before
/// sending its first datagram into a visible failure instead of a hang: the bounded first-frame
/// read records a <see cref="ProtocolViolation"/> and closes the connection, so the client's reply
/// read fails with EOF in the same window.
/// </para>
/// <para>
/// Test-only by construction: no product code path references this type.
/// </para>
/// </summary>
internal sealed class ScriptedSocks5UotServer : IAsyncDisposable
{
    /// <summary>How long the first frame may take before the client is declared to have waited for the CONNECT reply.</summary>
    private static readonly TimeSpan s_defaultFirstFrameTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The window an injection waits for the connection to exist and the CONNECT reply to be written.</summary>
    private static readonly TimeSpan s_injectionTimeout = TimeSpan.FromSeconds(10);

    private readonly string? _username;
    private readonly string? _password;
    private readonly byte _connectReplyStatus;
    private readonly TimeSpan _firstFrameTimeout;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Socket, byte> _connections = new();
    private readonly Lock _framesGate = new();
    private readonly List<byte[]> _frames = [];
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Task _acceptLoop;

    private Stream? _clientStream;
    private IPEndPoint? _acceptedRemoteEndpoint;
    private int _acceptedConnections;
    private int _liveConnections;
    private int _connectReplies;
    private int _disposed;

    /// <summary>Starts the listener on an ephemeral loopback port.</summary>
    /// <param name="username">The username the flow must present; null serves the no-authentication method.</param>
    /// <param name="password">The password paired with <paramref name="username"/>.</param>
    /// <param name="connectReplyStatus">The REP status the CONNECT reply carries (0 = success), so a test can refuse the CONNECT.</param>
    /// <param name="firstFrameTimeout">How long the first frame may take before the client is declared to have waited for the CONNECT reply.</param>
    public ScriptedSocks5UotServer(
        string? username = null,
        string? password = null,
        byte connectReplyStatus = 0,
        TimeSpan? firstFrameTimeout = null)
    {
        _username = username;
        _password = password;
        _connectReplyStatus = connectReplyStatus;
        _firstFrameTimeout = firstFrameTimeout ?? s_defaultFirstFrameTimeout;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        ControlEndpoint = (IPEndPoint)_listener.LocalEndpoint;
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    /// <summary>The endpoint the flow dials.</summary>
    public IPEndPoint ControlEndpoint { get; }

    /// <summary>The server a UoT-configured target points at.</summary>
    public Socks5Server Server => new("scripted", "127.0.0.1", checked((ushort)ControlEndpoint.Port), _username, _password, UdpOverTcp: true);

    /// <summary>Total connections accepted (one per flow, per the per-flow shape).</summary>
    public int ConnectionCount => Volatile.Read(ref _acceptedConnections);

    /// <summary>Connections still open; zero after every client-side flow is gone.</summary>
    public int LiveConnectionCount => Volatile.Read(ref _liveConnections);

    /// <summary>CONNECT replies written; one per connection whose first frame arrived.</summary>
    public int ConnectReplyCount => Volatile.Read(ref _connectReplies);

    /// <summary>Datagram frames read.</summary>
    public int FrameCount
    {
        get
        {
            lock (_framesGate) return _frames.Count;
        }
    }

    /// <summary>A snapshot of the payloads of every frame read, in arrival order.</summary>
    public IReadOnlyList<byte[]> Frames
    {
        get
        {
            lock (_framesGate) return [.. _frames];
        }
    }

    /// <summary>Completes when the first connection was accepted.</summary>
    public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The first accepted connection's remote endpoint (the client's local endpoint).</summary>
    public IPEndPoint? AcceptedRemoteEndpoint => Volatile.Read(ref _acceptedRemoteEndpoint);

    /// <summary>Completes with the UoT request header's destination.</summary>
    public TaskCompletionSource<Endpoint> UotDestination { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the first frame's payload has been read, before the CONNECT reply is written.</summary>
    public TaskCompletionSource FirstFrameRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the CONNECT reply bytes have been written.</summary>
    public TaskCompletionSource ConnectReplyWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with whether the presented credential pair matched the configured one.</summary>
    public TaskCompletionSource<bool> CredentialsAccepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The recorded ordering fact: the first frame was read while the CONNECT reply was still
    /// unwritten. True by construction on every flow that pipelines its first datagram, and the
    /// observable a test asserts; a client that waits for the reply never reaches it (its bounded
    /// first-frame read records a <see cref="ProtocolViolation"/> instead).
    /// </summary>
    public bool FirstFrameBeforeConnectReply { get; private set; }

    /// <summary>
    /// The fixture's own failure channel: completed with the exception behind a protocol violation
    /// (a CONNECT for another target, a non-connect-mode UoT header, a client that waited for the
    /// CONNECT reply). Tests assert it never completes on the happy path, and read its message when
    /// the client failed instead.
    /// </summary>
    public TaskCompletionSource<Exception> ProtocolViolation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Test-only gate around the CONNECT reply write. It runs after the first frame has been read
    /// and before the reply bytes are written, so a test can hold the reply deliberately and observe
    /// that the frame already arrived.
    /// </summary>
    public Func<CancellationToken, ValueTask>? ConnectReplyWriteGate { get; set; }

    /// <summary>Observes every frame payload the client sent, before the CONNECT reply is written for the first one.</summary>
    public Func<byte[], CancellationToken, ValueTask>? FrameObserver { get; set; }

    /// <summary>When set, injected frames are written one byte at a time with this delay, so a test can pin partial-read reassembly.</summary>
    public TimeSpan? InjectByteDelay { get; set; }

    /// <summary>Closes every live connection without stopping the listener: the peer observes EOF.</summary>
    /// <param name="reset">True to close with SO_LINGER 0, so the peer observes a connection reset rather than a clean close.</param>
    public void DropConnection(bool reset = false)
    {
        foreach (var connection in _connections.Keys)
        {
            if (reset)
            {
                try
                {
                    connection.LingerState = new LingerOption(enable: true, seconds: 0);
                }
                catch (SocketException)
                {
                    // A connection that already died cannot be re-configured; closing it is still right.
                }
            }

            connection.Dispose();
        }
    }

    /// <summary>
    /// Writes one datagram frame (<c>u16be length | payload</c>) toward the client. Waits for the
    /// CONNECT reply first: the reply precedes every frame on the wire, so an injection racing a
    /// deliberately held reply would otherwise leave bytes the client would parse as its reply.
    /// </summary>
    public async ValueTask InjectFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (payload.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(payload));
        await ConnectReplyWritten.Task.WaitAsync(s_injectionTimeout, cancellationToken).ConfigureAwait(false);
        var stream = Volatile.Read(ref _clientStream) ?? throw new InvalidOperationException("The UoT connection is not established yet.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(s_injectionTimeout);
        var frame = new byte[UotCodec.FrameHeaderSize + payload.Length];
        if (!UotCodec.TryWriteFrameHeader(checked((ushort)payload.Length), frame, out _))
        {
            throw new InvalidOperationException("The injected frame prefix did not fit its buffer.");
        }

        payload.CopyTo(frame.AsMemory(UotCodec.FrameHeaderSize));
        await _writeGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            if (InjectByteDelay is not { } delay)
            {
                await stream.WriteAsync(frame, deadline.Token).ConfigureAwait(false);
                return;
            }

            for (var index = 0; index < frame.Length; index++)
            {
                await stream.WriteAsync(frame.AsMemory(index, 1), deadline.Token).ConfigureAwait(false);
                await Task.Delay(delay, deadline.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Stops accepting and closes every live connection, modelling a dead SOCKS5 server.</summary>
    private async ValueTask StopAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        DropConnection();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            GC.KeepAlive(exception);
        }

        _writeGate.Dispose();
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _connections.TryAdd(socket, 0);
            _acceptedRemoteEndpoint ??= (IPEndPoint)socket.RemoteEndPoint!;
            Interlocked.Increment(ref _acceptedConnections);
            Interlocked.Increment(ref _liveConnections);
            Accepted.TrySetResult();
            _ = ServeAsync(socket, token);
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken token)
    {
        try
        {
            using (socket)
            {
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                Volatile.Write(ref _clientStream, stream);
                await ServeConnectionAsync(stream, token).ConfigureAwait(false);
            }
        }
        catch (XunitException violation)
        {
            // A protocol violation is a test failure, not a shutdown: publish it where the test can
            // read it and let the connection close, so a client that waits for a reply the fixture
            // will never write fails with EOF in the same window instead of both sides hanging.
            ProtocolViolation.TrySetResult(violation);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            GC.KeepAlive(exception);
        }
        catch (Exception exception)
        {
            ProtocolViolation.TrySetResult(exception);
        }
        finally
        {
            Volatile.Write(ref _clientStream, null);
            Interlocked.Decrement(ref _liveConnections);
            _connections.TryRemove(socket, out _);
        }
    }

    private async Task ServeConnectionAsync(Stream stream, CancellationToken token)
    {
        await ReadGreetingAsync(stream, token).ConfigureAwait(false);
        var credentials = _username is not null && _password is not null;
        await WriteAsync(stream, credentials ? [5, 2] : [5, 0], token).ConfigureAwait(false);
        // A refused credential exchange ends the connection: the client has already been told
        // (RFC 1929 reply 1) and must not be served any request bytes.
        if (credentials && !await ReadCredentialsAsync(stream, token).ConfigureAwait(false)) return;

        // The request flight — the CONNECT and the UoT request header — is bounded too: a client that
        // waits for a reply before writing any of it must fail visibly rather than park the fixture.
        using (var requestWindow = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            requestWindow.CancelAfter(_firstFrameTimeout);
            try
            {
                await ReadConnectRequestAsync(stream, requestWindow.Token).ConfigureAwait(false);
                UotDestination.TrySetResult(await ReadUotRequestAsync(stream, requestWindow.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new XunitException("The client did not write its CONNECT request and UoT request header within the request window; the request flight must not wait for a reply.");
            }
        }

        // The defining property: the CONNECT reply is deferred until the first frame has been read.
        var replyWritten = false;
        while (await ReadFrameAsync(stream, !replyWritten, token).ConfigureAwait(false) is { } frame)
        {
            if (FrameObserver is { } observer) await observer(frame, token).ConfigureAwait(false);
            if (!replyWritten)
            {
                FirstFrameBeforeConnectReply = true;
                FirstFrameRead.TrySetResult();
                await WriteConnectReplyAsync(stream, token).ConfigureAwait(false);
                replyWritten = true;
            }
        }
    }

    private static async ValueTask ReadGreetingAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (header[0] != 5) throw new XunitException($"The greeting declared version {header[0]}, not 5.");
        var methods = new byte[header[1]];
        await stream.ReadExactlyAsync(methods, token).ConfigureAwait(false);
    }

    private async ValueTask<bool> ReadCredentialsAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (header[0] != 1) throw new XunitException($"The credential message declared version {header[0]}, not 1.");
        var user = new byte[header[1]];
        await stream.ReadExactlyAsync(user, token).ConfigureAwait(false);
        var secretLength = new byte[1];
        await stream.ReadExactlyAsync(secretLength, token).ConfigureAwait(false);
        var secret = new byte[secretLength[0]];
        await stream.ReadExactlyAsync(secret, token).ConfigureAwait(false);
        var accepted = string.Equals(System.Text.Encoding.UTF8.GetString(user), _username, StringComparison.Ordinal)
            && string.Equals(System.Text.Encoding.UTF8.GetString(secret), _password, StringComparison.Ordinal);
        await WriteAsync(stream, [1, accepted ? (byte)0 : (byte)1], token).ConfigureAwait(false);
        CredentialsAccepted.TrySetResult(accepted);
        return accepted;
    }

    private static async ValueTask ReadConnectRequestAsync(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        if (prefix[0] != 5 || prefix[1] != (byte)Socks5Command.Connect || prefix[2] != 0 || prefix[3] != 3)
        {
            throw new XunitException(
                $"The request was not a domain-typed SOCKS5 CONNECT (VER {prefix[0]}, CMD {prefix[1]}, RSV {prefix[2]}, ATYP {prefix[3]}).");
        }

        var domainLength = new byte[1];
        await stream.ReadExactlyAsync(domainLength, token).ConfigureAwait(false);
        var domain = new byte[domainLength[0]];
        await stream.ReadExactlyAsync(domain, token).ConfigureAwait(false);
        var port = new byte[2];
        await stream.ReadExactlyAsync(port, token).ConfigureAwait(false);
        var target = System.Text.Encoding.ASCII.GetString(domain);
        if (!string.Equals(target, UotCodec.MagicAddress, StringComparison.Ordinal))
        {
            throw new XunitException(
                $"The UoT CONNECT target was '{target}', not the magic address '{UotCodec.MagicAddress}'.");
        }
    }

    /// <summary>
    /// Reads the UoT request header and returns the destination it carries, decoding the destination
    /// with the ordinary SOCKS address mapping — the mapping the pinned server reads the request with
    /// (<c>0x01</c> IPv4, <c>0x04</c> IPv6, <c>0x03</c> length-prefixed domain) — rather than with
    /// anything the client's own codec defines. An address family the SOCKS mapping does not define is
    /// refused loudly as <c>unknown address family</c>, mirroring the server's serializer; a family it
    /// does define is consumed by its own length, so a client that wrote the protocol's per-datagram
    /// types (<c>0x00</c>/<c>0x01</c>) is either refused here or leaves the stream visibly desynced
    /// instead of being mirrored silently.
    /// </summary>
    private static async ValueTask<Endpoint> ReadUotRequestAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (header[0] != 1)
        {
            throw new XunitException($"The UoT request header declared isConnect={header[0]}; connect mode requires 1.");
        }

        if (header[1] == Socks5Messages.AddressTypeDomain)
        {
            // The mapping's domain form is well-formed on the wire, but this product's connect-mode
            // client captures IP destinations only, so a name here is a violation rather than a
            // destination this fixture could serve. Its length prefix is still consumed first, so the
            // failure reports the name the client sent instead of a desynced stream.
            var nameLength = new byte[1];
            await stream.ReadExactlyAsync(nameLength, token).ConfigureAwait(false);
            var name = new byte[nameLength[0]];
            await stream.ReadExactlyAsync(name, token).ConfigureAwait(false);
            var namePort = new byte[2];
            await stream.ReadExactlyAsync(namePort, token).ConfigureAwait(false);
            throw new XunitException(
                $"The UoT request destination was the domain '{System.Text.Encoding.ASCII.GetString(name)}' (port {BinaryPrimitives.ReadUInt16BigEndian(namePort)}); connect mode carries an IP destination.");
        }

        var addressFieldLength = Socks5Messages.AddressFieldLength(header[1]);
        if (addressFieldLength < 0)
        {
            throw new XunitException($"unknown address family: {header[1]}");
        }

        var address = new byte[addressFieldLength];
        await stream.ReadExactlyAsync(address, token).ConfigureAwait(false);
        var port = new byte[2];
        await stream.ReadExactlyAsync(port, token).ConfigureAwait(false);
        return Endpoint.From(new IPAddress(address), BinaryPrimitives.ReadUInt16BigEndian(port));
    }

    /// <summary>
    /// Reads one frame, or null when the client closed the connection between frames. The first
    /// frame's read is bounded: a client that waits for the CONNECT reply never writes it, and that
    /// deadlock must surface as a violation instead of parking the fixture forever. Any frame after
    /// the first is an ordinary read that parks until the client closes.
    /// </summary>
    private async ValueTask<byte[]?> ReadFrameAsync(Stream stream, bool applyFirstFrameDeadline, CancellationToken token)
    {
        using var deadline = applyFirstFrameDeadline ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
        deadline?.CancelAfter(_firstFrameTimeout);
        var readToken = deadline?.Token ?? token;
        try
        {
            var prefix = new byte[UotCodec.FrameHeaderSize];
            await stream.ReadExactlyAsync(prefix, readToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
            var payload = new byte[length];
            if (length > 0) await stream.ReadExactlyAsync(payload, readToken).ConfigureAwait(false);
            lock (_framesGate) _frames.Add(payload);
            return payload;
        }
        catch (EndOfStreamException)
        {
            // The client closed the connection: a clean end of the flow, not a protocol violation.
            return null;
        }
        catch (OperationCanceledException) when (deadline is not null && !token.IsCancellationRequested)
        {
            throw new XunitException(
                "The client did not write its first datagram frame within the request window; the request flight (CONNECT, UoT header, first datagram) must not wait for a reply.");
        }
    }

    private async ValueTask WriteConnectReplyAsync(Stream stream, CancellationToken token)
    {
        var addressBytes = ControlEndpoint.Address.GetAddressBytes();
        var reply = new byte[4 + addressBytes.Length + 2];
        reply[0] = 5;
        reply[1] = _connectReplyStatus;
        reply[2] = 0;
        reply[3] = addressBytes.Length == 4 ? (byte)1 : (byte)4;
        addressBytes.CopyTo(reply, 4);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4 + addressBytes.Length), checked((ushort)ControlEndpoint.Port));

        await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Published before the write so a test asserting the count right after
            // ConnectReplyWritten observes at least the write that completed the handshake.
            Interlocked.Increment(ref _connectReplies);
            if (ConnectReplyWriteGate is { } gate) await gate(token).ConfigureAwait(false);
            await stream.WriteAsync(reply, token).ConfigureAwait(false);
            ConnectReplyWritten.TrySetResult();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask WriteAsync(Stream stream, byte[] message, CancellationToken token)
    {
        await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(message, token).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
