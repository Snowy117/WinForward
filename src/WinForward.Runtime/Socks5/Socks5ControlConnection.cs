using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime.Socks5;

public sealed class Socks5ControlConnection : IAsyncDisposable
{
    // L1: the connect-attempt loop is bounded by a global attempt cap and a per-attempt socket
    // timeout so DNS resolution plus sequential connects cannot hang the capture path indefinitely.
    // Each candidate address is one attempt; exhausted candidates fail closed (exception -> blocked)
    // without changing policy.
    private const int MaxConnectionAttempts = 4;
    private static readonly TimeSpan s_connectAttemptTimeout = TimeSpan.FromSeconds(30);

    // One reusable handshake scratch buffer per connection must hold the largest frame the
    // connection writes or reads. An RFC 1929 username/password message (3-byte header + up to
    // 255-byte user + 255-byte secret = 513) dominates the reply bound (5-byte prefix + up to
    // 256-byte domain + 2-byte port = 263). The buffer is only ever in flight in one direction
    // at a time: every write completes before the matching read starts.
    private const int HandshakeScratchLength = 3 + 255 + 255;

    // Default DNS seam, cached so the null-argument path allocates no delegate per connect.
    private static readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> s_defaultAddressResolver =
        static (host, token) => new ValueTask<IPAddress[]>(Dns.GetHostAddressesAsync(host, token));

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly IDisposable? _loopPrevention;
    private readonly CancellationTokenSource _attemptCancellation;
    private readonly CancellationToken _connectCancellation;
    private readonly Socks5AddressCache? _addressCache;
    private readonly byte[] _handshakeScratch = new byte[HandshakeScratchLength];

    // Admission + quiescence for the operations that read the attempt deadline (`AttemptToken`).
    // The scope is linked to the caller's lifetime token, so it is canceled once the connection is
    // disposed; it is also what makes the epoch CTS release ordered after every reader (D-C4-7).
    private readonly QuiescenceScope _scope;
    private int _disposeStarted;

    // The deferred handshake's one-shot state. A connection opened by the synchronous ConnectAsync is
    // born DeferredNothingToComplete — its replies were read inline — while the pipelined dial arms
    // the value that records exactly what it wrote. The completion claims the armed value with a CAS
    // back to DeferredNothingToComplete, so a second call, or a call on a synchronously authenticated
    // connection, is refused before any byte is read.
    private const int DeferredNothingToComplete = 0;
    private const int DeferredArmedGreetingOnly = 1;
    private const int DeferredArmedWithCredentials = 2;
    private int _deferredHandshake;

    /// <summary>How much of the SOCKS5 negotiation an attempt performs before the dial returns.</summary>
    private enum HandshakeMode
    {
        /// <summary>The whole negotiation: the greeting (and, on a method-2 selection, the credential message) is written and every reply is read and validated.</summary>
        Synchronous,

        /// <summary>The write half only: the greeting <c>[+ credentials]</c> is written and the connection is armed for a later completion that reads the replies.</summary>
        Deferred,
    }

    private Socks5ControlConnection(Socket socket, IDisposable? loopPrevention, CancellationTokenSource attemptCancellation, Socks5AddressCache? addressCache, CancellationToken connectCancellation)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _loopPrevention = loopPrevention;
        _attemptCancellation = attemptCancellation;
        _connectCancellation = connectCancellation;
        _addressCache = addressCache;
        _scope = new QuiescenceScope(connectCancellation);
    }

    /// <summary>
    /// Opens a SOCKS5 control connection. <paramref name="onSocketReady"/> is invoked after the
    /// socket is bound to a wildcard local endpoint (so the local port is already known) but before
    /// the SYN leaves the host; it returns an optional loop-prevention registration that the
    /// connection owns and disposes with itself. Registering before the SYN closes the race where a
    /// catch-all proxy rule could capture WinForward's own SOCKS5 control traffic (design §10).
    /// Attempts are capped by <paramref name="maxAttempts"/> and each attempt is bounded by
    /// <paramref name="perAttemptTimeout"/>; a failed attempt waits out of the loop to the next
    /// candidate, exhausting candidates fails closed (L1).
    /// </summary>
    public static ValueTask<Socks5ControlConnection> ConnectAsync(
        Socks5Server server,
        CancellationToken cancellationToken,
        Func<IPEndPoint, IPEndPoint, IDisposable?>? onSocketReady = null,
        int maxAttempts = MaxConnectionAttempts,
        TimeSpan? perAttemptTimeout = null,
        Socks5AddressCache? addressCache = null)
        => ConnectAsync(server, cancellationToken, resolveAddresses: null, socketFactory: null, onSocketReady, maxAttempts, perAttemptTimeout, addressCache);

    /// <summary>
    /// Opens a SOCKS5 control connection with a deferred handshake: the same attempt loop, address
    /// resolution and address cache, per-attempt deadline, self-traffic callback (invoked with the
    /// bound local endpoint before the SYN leaves the host) and socket setup
    /// (<see cref="Socket.NoDelay"/>, per-attempt timeouts) as
    /// <see cref="ConnectAsync(Socks5Server, CancellationToken, Func{IPEndPoint, IPEndPoint, IDisposable?}?, int, TimeSpan?, Socks5AddressCache?)"/>,
    /// but the dial writes the greeting — and, when the server carries credentials, the RFC 1929
    /// username/password message — and returns <b>without reading a single reply byte</b>. The
    /// credential message therefore precedes the method-selection reply, the pipelining's deliberate
    /// protocol rudeness (design §3). The caller therefore writes its first request flight (and, for
    /// UDP-over-TCP, the flow's first datagram) before any handshake round trip completes.
    /// A failure that aborts an attempt today — DNS, socket creation, connect, a write fault, caller
    /// cancellation, the per-attempt deadline — aborts this dial exactly the same way; only the
    /// failures the unwritten replies could reveal are unobserved until
    /// <see cref="CompleteDeferredHandshakeAsync(CancellationToken)"/>.
    /// <para>
    /// Ordering: the dial is the first step. The completion must then run before <b>any other read</b>
    /// of the stream, because it consumes the replies the server writes ahead of every later byte; the
    /// caller's own writes may happen before, between, or after it (TCP directions are independent).
    /// Taking the stream hand-off (<see cref="GetUpstreamStream"/>) and switching the socket to
    /// non-blocking before the completion is supported: the completion reads asynchronously, and an
    /// asynchronous read is unaffected both by the socket's <see cref="Socket.ReceiveTimeout"/> /
    /// <see cref="Socket.SendTimeout"/> and by its blocking mode.
    /// </para>
    /// </summary>
    public static ValueTask<Socks5ControlConnection> ConnectDeferredHandshakeAsync(
        Socks5Server server,
        CancellationToken cancellationToken,
        Func<IPEndPoint, IPEndPoint, IDisposable?>? onSocketReady = null,
        int maxAttempts = MaxConnectionAttempts,
        TimeSpan? perAttemptTimeout = null,
        Socks5AddressCache? addressCache = null)
        => ConnectDeferredHandshakeAsync(server, cancellationToken, resolveAddresses: null, socketFactory: null, onSocketReady, maxAttempts, perAttemptTimeout, addressCache);

    /// <summary>
    /// The injectable-seam form of <see cref="ConnectAsync(Socks5Server, CancellationToken, Func{IPEndPoint, IPEndPoint, IDisposable?}?, int, TimeSpan?, Socks5AddressCache?)"/>:
    /// <paramref name="resolveAddresses"/> (the address-list provider) and <paramref name="socketFactory"/>
    /// (the socket constructor) let tests exercise the attempt cap, deadline, and disposal with
    /// fakes instead of real DNS and sockets. Production callers use the public overload.
    /// </summary>
#pragma warning disable CA1068 // Deliberate shape: this injectable-seam overload mirrors the public ConnectAsync overload (CancellationToken immediately after the required server argument); the required resolveAddresses seam function cannot follow the token after the optional parameters, and reordering just this overload would diverge the documented pair for a style-only gain.
    internal static ValueTask<Socks5ControlConnection> ConnectAsync(
        Socks5Server server,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolveAddresses,
        Func<AddressFamily, Socket>? socketFactory = null,
        Func<IPEndPoint, IPEndPoint, IDisposable?>? onSocketReady = null,
        int maxAttempts = MaxConnectionAttempts,
        TimeSpan? perAttemptTimeout = null,
        Socks5AddressCache? addressCache = null)
#pragma warning restore CA1068
        => ConnectCoreAsync(server, resolveAddresses, socketFactory, onSocketReady, maxAttempts, perAttemptTimeout, addressCache, HandshakeMode.Synchronous, cancellationToken);

    /// <summary>
    /// The injectable-seam form of
    /// <see cref="ConnectDeferredHandshakeAsync(Socks5Server, CancellationToken, Func{IPEndPoint, IPEndPoint, IDisposable?}?, int, TimeSpan?, Socks5AddressCache?)"/>:
    /// the same <paramref name="resolveAddresses"/> and <paramref name="socketFactory"/> seams, so a
    /// test can exercise the deferred dial's attempt loop without real DNS or sockets.
    /// </summary>
#pragma warning disable CA1068 // Deliberate shape: this injectable-seam overload mirrors the public ConnectDeferredHandshakeAsync overload (CancellationToken immediately after the required server argument); the required resolveAddresses seam function cannot follow the token after the optional parameters, and reordering just this overload would diverge the documented pair for a style-only gain.
    internal static ValueTask<Socks5ControlConnection> ConnectDeferredHandshakeAsync(
        Socks5Server server,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolveAddresses,
        Func<AddressFamily, Socket>? socketFactory = null,
        Func<IPEndPoint, IPEndPoint, IDisposable?>? onSocketReady = null,
        int maxAttempts = MaxConnectionAttempts,
        TimeSpan? perAttemptTimeout = null,
        Socks5AddressCache? addressCache = null)
#pragma warning restore CA1068
        => ConnectCoreAsync(server, resolveAddresses, socketFactory, onSocketReady, maxAttempts, perAttemptTimeout, addressCache, HandshakeMode.Deferred, cancellationToken);

    /// <summary>
    /// The single attempt loop both entry points run: resolve the server address (through the cache
    /// when one is supplied), then try one candidate address per attempt, bounded by
    /// <paramref name="maxAttempts"/> and each candidate's <paramref name="perAttemptTimeout"/>. The
    /// only difference between the entry points is <paramref name="handshakeMode"/>, which decides
    /// whether the connection's negotiation finishes inside the attempt (synchronous) or is armed for
    /// the caller to complete later (deferred).
    /// </summary>
    private static async ValueTask<Socks5ControlConnection> ConnectCoreAsync(
        Socks5Server server,
        Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolveAddresses,
        Func<AddressFamily, Socket>? socketFactory,
        Func<IPEndPoint, IPEndPoint, IDisposable?>? onSocketReady,
        int maxAttempts,
        TimeSpan? perAttemptTimeout,
        Socks5AddressCache? addressCache,
        HandshakeMode handshakeMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

        var socketCtor = socketFactory ?? (family => new Socket(family, SocketType.Stream, ProtocolType.Tcp));
        var timeout = perAttemptTimeout ?? s_connectAttemptTimeout;
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(perAttemptTimeout));
        var timeoutMs = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);

        IPAddress[] addresses;
        if (addressCache is not null && addressCache.TryGet(server.Host, out var cached))
        {
            // Steady state: the configured endpoint was resolved at startup (or on a prior
            // failure), so no DNS call happens on the connection-setup path.
            addresses = [cached.ToIPAddress()];
        }
        else
        {
            var addressProvider = resolveAddresses ?? s_defaultAddressResolver;
            addresses = await ResolveAddressesAsync(addressProvider, server.Host, timeout, cancellationToken).ConfigureAwait(false);
            if (addressCache is not null && addresses.Length > 0) addressCache.Set(server.Host, IPAddressValue.From(addresses[0]));
        }
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        Exception? lastConnectionError = null;
        var attempts = 0;
        foreach (var address in addresses)
        {
            if (attempts >= maxAttempts) break;
            attempts++;

            var outcome = await ConnectOnceAsync(
                server, address, socketCtor, onSocketReady, timeout, timeoutMs, addressCache, handshakeMode, cancellationToken).ConfigureAwait(false);
            if (outcome.Connection is not null) return outcome.Connection;

            // A socket-creation failure or a per-attempt timeout records a representative error so
            // the next candidate is tried; only the final aggregate error is surfaced.
            lastConnectionError ??= outcome.Error ?? new SocketException((int)SocketError.TimedOut);
            if (outcome.IsFatal)
            {
                break;
            }
        }

        // Connection failure marks the cached endpoint dirty: the next setup attempt re-resolves on
        // the setup worker (cold path) so a moved/renumbered relay recovers without a restart.
        addressCache?.Invalidate(server.Host);
        throw new IOException("Unable to connect to the configured SOCKS5 server.", lastConnectionError);
    }

    /// <summary>
    /// Performs one connect attempt to a single candidate address. On success the owned connection
    /// (and registration) are returned and ownership transfers to the caller; the negotiation itself
    /// is finished inline for <see cref="HandshakeMode.Synchronous"/> and only written (leaving the
    /// connection armed) for <see cref="HandshakeMode.Deferred"/>. On failure this helper releases
    /// every acquired resource before returning. The single attempt deadline spans TCP connect and the
    /// negotiation's write half; the successful connection retains it for the SOCKS command and
    /// reply-domain resolution that immediately follow setup, and for the deferred handshake's reply
    /// reads.
    /// </summary>
    private static async ValueTask<ConnectAttempt> ConnectOnceAsync(
        Socks5Server server,
        IPAddress address,
        Func<AddressFamily, Socket> socketCtor,
        Func<IPEndPoint, IPEndPoint, IDisposable?>? onSocketReady,
        TimeSpan timeout,
        int timeoutMs,
        Socks5AddressCache? addressCache,
        HandshakeMode handshakeMode,
        CancellationToken cancellationToken)
    {
        Socket? socket;
        IDisposable? registration = null;
        Socks5ControlConnection? connection = null;
        CancellationTokenSource? attemptCancellation = null;
        try
        {
            socket = socketCtor(address.AddressFamily);
        }
        catch (SocketException exception)
        {
            return new ConnectAttempt(Connection: null, Error: exception, IsFatal: false);
        }

        try
        {
            socket.ReceiveTimeout = timeoutMs;
            socket.SendTimeout = timeoutMs;
            var bindAddress = address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
            socket.Bind(new IPEndPoint(bindAddress, 0));
            registration = onSocketReady?.Invoke((IPEndPoint)socket.LocalEndPoint!, new IPEndPoint(address, server.Port));

            attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(timeout);
            await socket.ConnectAsync(new IPEndPoint(address, server.Port), attemptCancellation.Token).ConfigureAwait(false);
            // The upstream leg is a byte pipe: Nagle x delayed-ACK would stall small proxied
            // writes 40-200 ms (X4), so TCP_NODELAY goes on as soon as the connect succeeds.
            socket.NoDelay = true;

            connection = new Socks5ControlConnection(socket, registration, attemptCancellation, addressCache, cancellationToken);
            socket = null;
            registration = null;
            attemptCancellation = null;
            await NegotiateAsync(connection, server, handshakeMode).ConfigureAwait(false);
            return new ConnectAttempt(Connection: connection, Error: null, IsFatal: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeFailedAttemptAsync(connection, socket, registration, attemptCancellation).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The per-attempt timeout elapsed; this candidate failed and the next is tried.
            await DisposeFailedAttemptAsync(connection, socket, registration, attemptCancellation).ConfigureAwait(false);
            return new ConnectAttempt(Connection: null, Error: new SocketException((int)SocketError.TimedOut), IsFatal: false);
        }
        catch (SocketException exception)
        {
            await DisposeFailedAttemptAsync(connection, socket, registration, attemptCancellation).ConfigureAwait(false);
            return new ConnectAttempt(Connection: null, Error: exception, IsFatal: false);
        }
        catch (Exception exception)
        {
            await DisposeFailedAttemptAsync(connection, socket, registration, attemptCancellation).ConfigureAwait(false);
            return new ConnectAttempt(Connection: null, Error: exception, IsFatal: true);
        }
    }

    private CancellationToken AttemptToken => _attemptCancellation.Token;

    /// <summary>
    /// Runs the negotiation an attempt owes inside its deadline: the full greeting/method/[credentials]
    /// exchange, or the deferred write half that leaves the connection armed for
    /// <see cref="CompleteDeferredHandshakeAsync(CancellationToken)"/>.
    /// </summary>
    private static ValueTask NegotiateAsync(Socks5ControlConnection connection, Socks5Server server, HandshakeMode handshakeMode)
        => handshakeMode == HandshakeMode.Deferred
            ? connection.WriteDeferredGreetingAsync(server, connection.AttemptToken)
            : connection.AuthenticateAsync(server, connection.AttemptToken);

    private sealed record ConnectAttempt(Socks5ControlConnection? Connection, Exception? Error, bool IsFatal);

    public ValueTask<IPEndPoint> UdpAssociateAsync(CancellationToken cancellationToken) =>
        RunWithinAttemptAsync(async token =>
        {
            var controlAddressFamily = ((IPEndPoint)_socket.LocalEndPoint!).AddressFamily;
            var unspecifiedAddress = controlAddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
            var requestLength = Socks5Messages.WriteRequest(Socks5Command.UdpAssociate, unspecifiedAddress, 0, _handshakeScratch);
            await _stream.WriteAsync(_handshakeScratch.AsMemory(0, requestLength), token).ConfigureAwait(false);
            return await ReadEndpointReplyAsync(Socks5Command.UdpAssociate, token).ConfigureAwait(false);
        }, cancellationToken);

    public ValueTask ConnectDestinationAsync(IPEndPoint destination, CancellationToken cancellationToken) =>
        RunWithinAttemptAsync(async token =>
        {
            var requestLength = Socks5Messages.WriteRequest(Socks5Command.Connect, destination.Address, (ushort)destination.Port, _handshakeScratch);
            await _stream.WriteAsync(_handshakeScratch.AsMemory(0, requestLength), token).ConfigureAwait(false);
            _ = await ReadEndpointReplyAsync(Socks5Command.Connect, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Reads and validates the replies the deferred dial left unread: the method-selection reply and,
    /// when the dial wrote the RFC 1929 credential message, the credential reply. It must run
    /// <b>before any other read</b> of the stream — it consumes the bytes the server wrote ahead of
    /// every later reply and frame — and it may run before or after the caller takes
    /// <see cref="GetUpstreamStream"/> and switches the socket to non-blocking, because its reads are
    /// asynchronous: neither the socket timeouts nor its blocking mode govern them.
    /// <para>
    /// Exactly once: the call claims the connection's armed deferred handshake, so a second call, and
    /// a call on a connection opened by
    /// <see cref="ConnectAsync(Socks5Server, CancellationToken, Func{IPEndPoint, IPEndPoint, IDisposable?}?, int, TimeSpan?, Socks5AddressCache?)"/>,
    /// throws <see cref="InvalidOperationException"/> without reading a byte. The caller's token is the
    /// deadline (the attempt's own remaining budget is an earlier bound when it is smaller, as for
    /// every operation on this connection), and the read holds a quiescence lease, so
    /// <see cref="DisposeAsync"/> joins it and refuses a late completion with
    /// <see cref="ObjectDisposedException"/>.
    /// </para>
    /// <para>
    /// A server refusal — a method selection other than "no authentication" or the configured
    /// username/password method, or a non-success credential reply — throws the same
    /// <see cref="IOException"/> the synchronous handshake throws for that condition, so the caller
    /// maps one exception type to its setup-failure path.
    /// </para>
    /// </summary>
    public async ValueTask CompleteDeferredHandshakeAsync(CancellationToken cancellationToken)
    {
        var claimed = ClaimDeferredHandshake();
        if (claimed == DeferredNothingToComplete)
        {
            throw new InvalidOperationException("The deferred SOCKS5 handshake is completed exactly once, on a connection opened by ConnectDeferredHandshakeAsync.");
        }

        var credentialsWritten = claimed == DeferredArmedWithCredentials;
        await RunWithinAttemptAsync(async token =>
        {
            var method = await ReadMethodSelectionAsync(token).ConfigureAwait(false);
            if (method == 0) return;
            if (method != 2 || !credentialsWritten) throw new IOException("SOCKS5 server did not accept a configured authentication method.");
            await ReadCredentialsReplyAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Claims the deferred handshake for a completion call: the armed state (which records whether the
    /// dial wrote the credential message) is exchanged for
    /// <see cref="DeferredNothingToComplete"/>, so exactly one caller wins — a second call, and a call
    /// on a synchronously authenticated connection, observe the same "nothing to complete" state.
    /// </summary>
    private int ClaimDeferredHandshake()
    {
        while (true)
        {
            var state = Volatile.Read(ref _deferredHandshake);
            // The claim is exactly-once: the volatile read settles "nothing to claim" without an
            // interlocked write, the CAS settles the armed value, and a lost race retries into the
            // winner's zero.
            if (state == DeferredNothingToComplete
                || Interlocked.CompareExchange(ref _deferredHandshake, DeferredNothingToComplete, state) == state)
            {
                return state;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // D11: one caller owns the teardown; every other caller joins the same drain.
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _scope.DrainAsync().ConfigureAwait(false);
            return;
        }

        // Sealing first is what makes the late-reader refusal meaningful: a reader is either
        // admitted below (and the drain then waits for its lease) or refused before it can touch
        // AttemptToken. The epoch deadline is released last, after the drain, so no admitted reader
        // can evaluate AttemptToken on a disposed source (D-C4-7).
        var drain = _scope.DrainAsync();
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _loopPrevention?.Dispose();
            }
            finally
            {
                await DrainAndReleaseDeadlineAsync(drain).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask DrainAndReleaseDeadlineAsync(Task drain)
    {
        try
        {
            await drain.ConfigureAwait(false);
        }
        finally
        {
            _attemptCancellation.Dispose();
        }
    }

    /// <summary>
    /// Returns the authenticated, CONNECT-negotiated upstream stream for byte relaying. The caller
    /// does not take ownership; disposing the <see cref="Socks5ControlConnection"/> closes the stream.
    /// <para>
    /// The hand-off resets the per-attempt socket timeouts to infinite, which is exactly right for the
    /// long-lived relay phase and does not disturb a deferred handshake: only synchronous socket
    /// operations read those timeouts, and
    /// <see cref="CompleteDeferredHandshakeAsync(CancellationToken)"/> reads asynchronously. The
    /// hand-off may therefore be taken before or after the completion, and the socket may be switched
    /// to non-blocking for the caller's own send fast path in the same window — a non-blocking socket
    /// does not affect the completion's asynchronous reads either. The completion's deadline — its
    /// caller's token, additionally bounded by the attempt's remaining budget — is not touched by this
    /// reset.
    /// </para>
    /// </summary>
    internal Stream GetUpstreamStream()
    {
        // The per-attempt socket timeouts must not survive into the long-lived relay phase: the
        // relay's own 30-minute stall window is the only idle guard from here on, and an idle
        // upstream would otherwise be killed by a stale 30s connect timeout (R1).
        _socket.ReceiveTimeout = Timeout.Infinite;
        _socket.SendTimeout = Timeout.Infinite;
        return _stream;
    }

    private async ValueTask<T> RunWithinAttemptAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken)
    {
        // Admission precedes the AttemptToken read: a reader refused here never evaluates the
        // epoch deadline, and an admitted reader's lease keeps the deadline alive until it exits.
        var admitted = _scope.TryEnter(out var lease);
        ObjectDisposedException.ThrowIf(!admitted, this);
        try
        {
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(AttemptToken, cancellationToken);
            try
            {
                return await operation(operationCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
            {
                ThrowForAttemptCancellation(cancellationToken);
                throw;
            }
        }
        finally
        {
            lease.Dispose();
        }
    }

    private async ValueTask RunWithinAttemptAsync(Func<CancellationToken, ValueTask> operation, CancellationToken cancellationToken)
    {
        var admitted = _scope.TryEnter(out var lease);
        ObjectDisposedException.ThrowIf(!admitted, this);
        try
        {
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(AttemptToken, cancellationToken);
            try
            {
                await operation(operationCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
            {
                ThrowForAttemptCancellation(cancellationToken);
                throw;
            }
        }
        finally
        {
            lease.Dispose();
        }
    }

    private void ThrowForAttemptCancellation(CancellationToken operationCancellation)
    {
        operationCancellation.ThrowIfCancellationRequested();
        _connectCancellation.ThrowIfCancellationRequested();
        if (_attemptCancellation.IsCancellationRequested) throw new IOException("SOCKS5 control connection attempt timed out.");
    }

    /// <summary>
    /// The synchronous negotiation <see cref="ConnectAsync(Socks5Server, CancellationToken, Func{IPEndPoint, IPEndPoint, IDisposable?}?, int, TimeSpan?, Socks5AddressCache?)"/>
    /// performs inside its attempt: the greeting, then the method-selection reply, then — only when the
    /// server selected the username/password method — the credential message and its reply. The write
    /// half is shared with <see cref="WriteDeferredGreetingAsync"/>; this sequence is what keeps the
    /// synchronous surface byte-for-byte what it was before the deferred variant existed.
    /// </summary>
    private async ValueTask AuthenticateAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        var credentials = server.Username is not null;
        await WriteGreetingAsync(credentials, cancellationToken).ConfigureAwait(false);
        var method = await ReadMethodSelectionAsync(cancellationToken).ConfigureAwait(false);
        if (method == 0) return;
        if (method != 2 || server.Username is null || server.Password is null) throw new IOException("SOCKS5 server did not accept a configured authentication method.");

        await WriteCredentialsAsync(server, cancellationToken).ConfigureAwait(false);
        await ReadCredentialsReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The write half of the negotiation, for the deferred (pipelined) dial: the greeting and, when
    /// the server carries a complete credential pair, the RFC 1929 username/password message — and
    /// then the connection is armed for <see cref="CompleteDeferredHandshakeAsync(CancellationToken)"/>.
    /// Nothing is read. A username without a password (unrepresentable through configuration
    /// validation, which requires the pair) writes the greeting only and leaves the credential message
    /// unwritten; the completion then fails closed exactly as the synchronous path does when the server
    /// selects that method.
    /// </summary>
    private async ValueTask WriteDeferredGreetingAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        await WriteGreetingAsync(server.Username is not null, cancellationToken).ConfigureAwait(false);
        var credentialsWritten = false;
        if (server.Username is not null && server.Password is not null)
        {
            await WriteCredentialsAsync(server, cancellationToken).ConfigureAwait(false);
            credentialsWritten = true;
        }

        // Published before the connection can leave the attempt: the value is what the completion
        // claims, and it records which reply sequence the server was given reason to write.
        Volatile.Write(ref _deferredHandshake, credentialsWritten ? DeferredArmedWithCredentials : DeferredArmedGreetingOnly);
    }

    private ValueTask WriteGreetingAsync(bool credentials, CancellationToken cancellationToken) =>
        _stream.WriteAsync(Socks5Messages.Greeting(credentials), cancellationToken);

    private ValueTask WriteCredentialsAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        var credentialsLength = Socks5Messages.WriteUsernamePassword(server.Username!, server.Password!, _handshakeScratch);
        return _stream.WriteAsync(_handshakeScratch.AsMemory(0, credentialsLength), cancellationToken);
    }

    private async ValueTask<byte> ReadMethodSelectionAsync(CancellationToken cancellationToken)
    {
        await _stream.ReadExactlyAsync(_handshakeScratch.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
        // ReSharper disable once ConvertIfStatementToReturnStatement // Guard-clause + throw reads failure-first; the suggested `cond ? throw ... : value` form has no precedent in this repo (B1 disposition).
        if (_handshakeScratch[0] != 5) throw new IOException("SOCKS5 server returned an invalid greeting version.");
        return _handshakeScratch[1];
    }

    private async ValueTask ReadCredentialsReplyAsync(CancellationToken cancellationToken)
    {
        await _stream.ReadExactlyAsync(_handshakeScratch.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
        if (_handshakeScratch[0] != 1 || _handshakeScratch[1] != 0) throw new IOException("SOCKS5 username/password authentication failed.");
    }

    private async ValueTask<IPEndPoint> ReadEndpointReplyAsync(Socks5Command command, CancellationToken cancellationToken)
    {
        await _stream.ReadExactlyAsync(_handshakeScratch.AsMemory(0, 5), cancellationToken).ConfigureAwait(false);
        // Validate only the VER/REP prefix: a success prefix is 5 bytes and carries no bound
        // address yet, so the full-reply parser (which requires >= 8 bytes) must not be used here.
        if (!Socks5Messages.TryParseReplyPrefix(_handshakeScratch.AsSpan(0, 5), out var status))
        {
            throw new IOException("SOCKS5 server returned an invalid reply prefix.");
        }
        if (status != 0)
        {
            throw new IOException($"SOCKS5 command failed: {Socks5Messages.DescribeReplyStatus(status)} (REP {status}).");
        }
        if (!Socks5Messages.TryGetReplyLength(_handshakeScratch.AsSpan(0, 5), out var totalLength)) throw new IOException("SOCKS5 server returned an invalid reply address.");
        // The scratch buffer holds the largest handshake frame (513), so every reply length the
        // prefix parser computes (<= 262) fits; the body lands in place directly after the prefix.
        await _stream.ReadExactlyAsync(_handshakeScratch.AsMemory(5, totalLength - 5), cancellationToken).ConfigureAwait(false);
        // L2: the full-reply parser is now discriminated. A success reply must parse exactly as
        // Success; a truncated or malformed success body is rejected, never parsed into garbage.
        if (Socks5Messages.TryParseReply(_handshakeScratch.AsSpan(0, totalLength), out _, out var addressType, out var port) != Socks5ReplyKind.Success)
        {
            throw new IOException("SOCKS5 command returned a malformed reply.");
        }

        var address = addressType switch
        {
            1 => new IPAddress(_handshakeScratch.AsSpan(4, 4)),
            4 => new IPAddress(_handshakeScratch.AsSpan(4, 16)),
            // RFC 1928 domain names are ASCII; non-ASCII bytes decode as '?' rather than throwing (R5).
            3 => await ResolveDomainAsync(System.Text.Encoding.ASCII.GetString(_handshakeScratch.AsSpan(5, _handshakeScratch[4])), cancellationToken).ConfigureAwait(false),
            _ => throw new IOException("SOCKS5 server returned an unsupported address type."),
        };

        // M1: only a UDP ASSOCIATE reply may substitute an unspecified wildcard with the control
        // peer; the server-provided BND port is always preserved. M2: a genuine IPv6 reply inherits
        // the control peer's interface scope so a link-local relay routes on the correct interface.
        var controlPeer = ((IPEndPoint)_socket.RemoteEndPoint!).Address;
        address = Socks5Messages.NormalizeBndAddress(address, controlPeer, command);
        return new IPEndPoint(address, port);
    }

    private async ValueTask<IPAddress> ResolveDomainAsync(string domain, CancellationToken cancellationToken)
    {
        if (_addressCache is not null && _addressCache.TryGet(domain, out var cached)) return cached.ToIPAddress();
        var addresses = await Dns.GetHostAddressesAsync(domain, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0) throw new IOException("SOCKS5 reply domain resolved to no addresses.");
        _addressCache?.Set(domain, IPAddressValue.From(addresses[0]));
        return addresses[0];
    }

    private static async ValueTask<IPAddress[]> ResolveAddressesAsync(Func<string, CancellationToken, ValueTask<IPAddress[]>> addressProvider, string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var resolutionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        resolutionCancellation.CancelAfter(timeout);
        var resolution = addressProvider(host, resolutionCancellation.Token).AsTask();
        try
        {
            return await resolution.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TimeoutException)
        {
            await resolutionCancellation.CancelAsync().ConfigureAwait(false);
            throw new IOException("SOCKS5 server address resolution timed out.", new SocketException((int)SocketError.TimedOut));
        }
        catch (OperationCanceledException)
        {
            throw new IOException("SOCKS5 server address resolution failed.", new SocketException((int)SocketError.TimedOut));
        }
    }

    private static async ValueTask DisposeFailedAttemptAsync(Socks5ControlConnection? connection, Socket? socket, IDisposable? registration, CancellationTokenSource? attemptCancellation)
    {
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        socket?.Dispose();
        registration?.Dispose();
        attemptCancellation?.Dispose();
    }
}
