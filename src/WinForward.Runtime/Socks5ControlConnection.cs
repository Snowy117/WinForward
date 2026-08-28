using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Protocols;

namespace WinForward.Runtime;

public sealed class Socks5ControlConnection : IAsyncDisposable
{
    // L1: the connect-attempt loop is bounded by a global attempt cap and a per-attempt socket
    // timeout so DNS resolution plus sequential connects cannot hang the capture path indefinitely.
    // Each candidate address is one attempt; exhausted candidates fail closed (exception -> blocked)
    // without changing policy.
    private const int MaxConnectionAttempts = 4;
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(30);

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly IDisposable? _loopPrevention;
    private readonly CancellationTokenSource _attemptCancellation;
    private readonly CancellationToken _connectCancellation;

    private Socks5ControlConnection(Socket socket, IDisposable? loopPrevention, CancellationTokenSource attemptCancellation, CancellationToken connectCancellation)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _loopPrevention = loopPrevention;
        _attemptCancellation = attemptCancellation;
        _connectCancellation = connectCancellation;
    }

    /// <summary>
    /// Opens a SOCKS5 control connection. <paramref name="onSocketReady"/> is invoked after the
    /// socket is bound to a wildcard local endpoint (so the local port is already known) but before
    /// the SYN leaves the host; it returns an optional loop-prevention registration that the
    /// connection owns and disposes with itself. Registering before the SYN closes the race where a
    /// catch-all proxy rule could capture WinForward's own SOCKS5 control traffic (design §10).
    /// <paramref name="resolveAddresses"/> and <paramref name="socketFactory"/> are injectable seams
    /// (address-list provider and socket ctor) so the attempt cap/deadline/disposal are testable with
    /// fakes without real sockets or DNS. Attempts are capped by <paramref name="maxAttempts"/> and
    /// each attempt is bounded by <paramref name="perAttemptTimeout"/>; a failed attempt waits out of
    /// the loop to the next candidate, exhausting candidates fails closed (L1).
    /// </summary>
    public static async ValueTask<Socks5ControlConnection> ConnectAsync(
        Socks5Server server,
        CancellationToken cancellationToken,
        Func<IPEndPoint, IPEndPoint, IDisposable?>? onSocketReady = null,
        Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolveAddresses = null,
        Func<AddressFamily, Socket>? socketFactory = null,
        int maxAttempts = MaxConnectionAttempts,
        TimeSpan? perAttemptTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

        var addressProvider = resolveAddresses ?? ((host, token) => new ValueTask<IPAddress[]>(Dns.GetHostAddressesAsync(host, token)));
        var socketCtor = socketFactory ?? (family => new Socket(family, SocketType.Stream, ProtocolType.Tcp));
        var timeout = perAttemptTimeout ?? ConnectAttemptTimeout;
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(perAttemptTimeout));
        var timeoutMs = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);

        var addresses = await ResolveAddressesAsync(addressProvider, server.Host, cancellationToken, timeout).ConfigureAwait(false);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        Exception? lastConnectionError = null;
        var attempts = 0;
        foreach (var address in addresses)
        {
            if (attempts >= maxAttempts) break;
            attempts++;

            var outcome = await ConnectOnceAsync(
                server, address, socketCtor, onSocketReady, cancellationToken, timeout, timeoutMs).ConfigureAwait(false);
            if (outcome.Connection is not null) return outcome.Connection;

            // A socket-creation failure or a per-attempt timeout records a representative error so
            // the next candidate is tried; only the final aggregate error is surfaced.
            lastConnectionError ??= outcome.Error ?? new SocketException((int)SocketError.TimedOut);
            if (outcome.IsFatal)
            {
                break;
            }
        }

        throw new IOException("Unable to connect to the configured SOCKS5 server.", lastConnectionError);
    }

    /// <summary>
    /// Performs one connect attempt to a single candidate address. On success the owned connection
    /// (and registration) are returned and ownership transfers to the caller. On failure this helper
    /// releases every acquired resource before returning. The single attempt deadline spans TCP
    /// connect and authentication; the successful connection retains it for the SOCKS command and
    /// reply-domain resolution that immediately follow setup.
    /// </summary>
    private static async ValueTask<ConnectAttempt> ConnectOnceAsync(
        Socks5Server server,
        IPAddress address,
        Func<AddressFamily, Socket> socketCtor,
        Func<IPEndPoint, IPEndPoint, IDisposable?>? onSocketReady,
        CancellationToken cancellationToken,
        TimeSpan timeout,
        int timeoutMs)
    {
        Socket? socket = null;
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

            connection = new Socks5ControlConnection(socket, registration, attemptCancellation, cancellationToken);
            socket = null;
            registration = null;
            attemptCancellation = null;
            await connection.AuthenticateAsync(server, connection.AttemptToken).ConfigureAwait(false);
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

    private sealed record ConnectAttempt(Socks5ControlConnection? Connection, Exception? Error, bool IsFatal);

    public ValueTask<IPEndPoint> UdpAssociateAsync(CancellationToken cancellationToken) =>
        RunWithinAttemptAsync(async token =>
        {
            var controlAddressFamily = ((IPEndPoint)_socket.LocalEndPoint!).AddressFamily;
            var unspecifiedAddress = controlAddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
            var request = Socks5Messages.Request(Socks5Command.UdpAssociate, unspecifiedAddress, 0);
            await _stream.WriteAsync(request, token).ConfigureAwait(false);
            return await ReadEndpointReplyAsync(Socks5Command.UdpAssociate, token).ConfigureAwait(false);
        }, cancellationToken);

    public ValueTask ConnectDestinationAsync(IPEndPoint destination, CancellationToken cancellationToken) =>
        RunWithinAttemptAsync(async token =>
        {
            var request = Socks5Messages.Request(Socks5Command.Connect, destination.Address, (ushort)destination.Port);
            await _stream.WriteAsync(request, token).ConfigureAwait(false);
            _ = await ReadEndpointReplyAsync(Socks5Command.Connect, token).ConfigureAwait(false);
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
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
                _attemptCancellation.Dispose();
            }
        }
    }

    /// <summary>
    /// Returns the authenticated, CONNECT-negotiated upstream stream for byte relaying. The caller
    /// does not take ownership; disposing the <see cref="Socks5ControlConnection"/> closes the stream.
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

    private async ValueTask RunWithinAttemptAsync(Func<CancellationToken, ValueTask> operation, CancellationToken cancellationToken)
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

    private void ThrowForAttemptCancellation(CancellationToken operationCancellation)
    {
        if (operationCancellation.IsCancellationRequested) throw new OperationCanceledException(operationCancellation);
        if (_connectCancellation.IsCancellationRequested) throw new OperationCanceledException(_connectCancellation);
        if (_attemptCancellation.IsCancellationRequested) throw new IOException("SOCKS5 control connection attempt timed out.");
    }

    private async ValueTask AuthenticateAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        var credentials = server.Username is not null;
        await _stream.WriteAsync(Socks5Messages.Greeting(credentials), cancellationToken).ConfigureAwait(false);
        var methodReply = new byte[2];
        await _stream.ReadExactlyAsync(methodReply, cancellationToken).ConfigureAwait(false);
        if (methodReply[0] != 5) throw new IOException("SOCKS5 server returned an invalid greeting version.");
        if (methodReply[1] == 0) return;
        if (methodReply[1] != 2 || server.Username is null || server.Password is null) throw new IOException("SOCKS5 server did not accept a configured authentication method.");

        await _stream.WriteAsync(Socks5Messages.UsernamePassword(server.Username, server.Password), cancellationToken).ConfigureAwait(false);
        var authReply = new byte[2];
        await _stream.ReadExactlyAsync(authReply, cancellationToken).ConfigureAwait(false);
        if (authReply[0] != 1 || authReply[1] != 0) throw new IOException("SOCKS5 username/password authentication failed.");
    }

    private async ValueTask<IPEndPoint> ReadEndpointReplyAsync(Socks5Command command, CancellationToken cancellationToken)
    {
        var prefix = new byte[5];
        await _stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        // Validate only the VER/REP prefix: a success prefix is 5 bytes and carries no bound
        // address yet, so the full-reply parser (which requires >= 8 bytes) must not be used here.
        if (!Socks5Messages.TryParseReplyPrefix(prefix, out var status))
        {
            throw new IOException("SOCKS5 server returned an invalid reply prefix.");
        }
        if (status != 0)
        {
            throw new IOException($"SOCKS5 command failed: {Socks5Messages.DescribeReplyStatus(status)} (REP {status}).");
        }
        if (!Socks5Messages.TryGetReplyLength(prefix, out var totalLength)) throw new IOException("SOCKS5 server returned an invalid reply address.");
        var reply = new byte[totalLength];
        prefix.CopyTo(reply, 0);
        await _stream.ReadExactlyAsync(reply.AsMemory(prefix.Length), cancellationToken).ConfigureAwait(false);
        // L2: the full-reply parser is now discriminated. A success reply must parse exactly as
        // Success; a truncated or malformed success body is rejected, never parsed into garbage.
        if (Socks5Messages.TryParseReply(reply, out _, out var addressType, out var port) != Socks5ReplyKind.Success)
        {
            throw new IOException("SOCKS5 command returned a malformed reply.");
        }

        IPAddress address = addressType switch
        {
            1 => new IPAddress(reply.AsSpan(4, 4)),
            4 => new IPAddress(reply.AsSpan(4, 16)),
            // RFC 1928 domain names are ASCII; non-ASCII bytes decode as '?' rather than throwing (R5).
            3 => await ResolveDomainAsync(System.Text.Encoding.ASCII.GetString(reply.AsSpan(5, reply[4])), cancellationToken).ConfigureAwait(false),
            _ => throw new IOException("SOCKS5 server returned an unsupported address type.")
        };

        // M1: only a UDP ASSOCIATE reply may substitute an unspecified wildcard with the control
        // peer; the server-provided BND port is always preserved. M2: a genuine IPv6 reply inherits
        // the control peer's interface scope so a link-local relay routes on the correct interface.
        var controlPeer = ((IPEndPoint)_socket.RemoteEndPoint!).Address;
        address = Socks5Messages.NormalizeBndAddress(address, controlPeer, command);
        return new IPEndPoint(address, port);
    }

    private static async ValueTask<IPAddress> ResolveDomainAsync(string domain, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(domain, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault() ?? throw new IOException("SOCKS5 reply domain resolved to no addresses.");
    }

    private static async ValueTask<IPAddress[]> ResolveAddressesAsync(Func<string, CancellationToken, ValueTask<IPAddress[]>> addressProvider, string host, CancellationToken cancellationToken, TimeSpan timeout)
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
