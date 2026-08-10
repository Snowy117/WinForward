using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
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

    private Socks5ControlConnection(Socket socket, IDisposable? loopPrevention)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _loopPrevention = loopPrevention;
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
        var timeoutMs = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);

        var addresses = await addressProvider(server.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        SocketException? lastConnectionError = null;
        var attempts = 0;
        foreach (var address in addresses)
        {
            if (attempts >= maxAttempts) break;
            attempts++;

            var outcome = await ConnectOnceAsync(
                server, address, socketCtor, onSocketReady, cancellationToken, timeout, timeoutMs).ConfigureAwait(false);
            if (outcome.Connection is not null) return outcome.Connection;

            outcome.Socket?.Dispose();
            outcome.Registration?.Dispose();
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
    /// (and registration) are returned and ownership transfers to the caller, so this helper does
    /// not dispose them. On failure the created socket/registration (if any) are handed back for the
    /// caller to dispose. <paramref name="timeoutMs"/> is applied as the socket Send/Receive timeout
    /// and as the per-attempt connect deadline; <paramref name="isFatal"/> distinguishes an abnormal
    /// failure (cancellation by the caller) from a normal per-attempt failure that can retry.
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
        Socket socket;
        try
        {
            socket = socketCtor(address.AddressFamily);
        }
        catch (SocketException exception)
        {
            return new ConnectAttempt(Socket: null, Registration: null, Connection: null, Error: exception, IsFatal: false);
        }

        IDisposable? registration = null;
        try
        {
            socket.ReceiveTimeout = timeoutMs;
            socket.SendTimeout = timeoutMs;
            var bindAddress = address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
            socket.Bind(new IPEndPoint(bindAddress, 0));
            registration = onSocketReady?.Invoke((IPEndPoint)socket.LocalEndPoint!, new IPEndPoint(address, server.Port));

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(timeout);
            await socket.ConnectAsync(new IPEndPoint(address, server.Port), attemptCts.Token).ConfigureAwait(false);

            var connection = new Socks5ControlConnection(socket, registration);
            await connection.AuthenticateAsync(server, cancellationToken).ConfigureAwait(false);
            return new ConnectAttempt(Socket: socket, Registration: registration, Connection: connection, Error: null, IsFatal: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per-attempt timeout elapsed; this candidate failed and the next is tried.
            return new ConnectAttempt(socket, registration, null, new SocketException((int)SocketError.TimedOut), IsFatal: false);
        }
        catch (SocketException exception)
        {
            return new ConnectAttempt(socket, registration, null, exception, IsFatal: false);
        }
        catch
        {
            return new ConnectAttempt(socket, registration, null, null, IsFatal: true);
        }
    }

    private sealed record ConnectAttempt(Socket? Socket, IDisposable? Registration, Socks5ControlConnection? Connection, SocketException? Error, bool IsFatal);

    public async ValueTask<IPEndPoint> UdpAssociateAsync(IPEndPoint localEndpoint, CancellationToken cancellationToken)
    {
        var request = Socks5Messages.Request(Socks5Command.UdpAssociate, localEndpoint.Address, (ushort)localEndpoint.Port);
        await _stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadEndpointReplyAsync(Socks5Command.UdpAssociate, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ConnectDestinationAsync(IPEndPoint destination, CancellationToken cancellationToken)
    {
        var request = Socks5Messages.Request(Socks5Command.Connect, destination.Address, (ushort)destination.Port);
        await _stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        _ = await ReadEndpointReplyAsync(Socks5Command.Connect, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _loopPrevention?.Dispose();
        return _stream.DisposeAsync();
    }

    /// <summary>
    /// Returns the authenticated, CONNECT-negotiated upstream stream for byte relaying. The caller
    /// does not take ownership; disposing the <see cref="Socks5ControlConnection"/> closes the stream.
    /// </summary>
    internal Stream GetUpstreamStream() => _stream;

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
            3 => await ResolveDomainAsync(System.Text.Encoding.UTF8.GetString(reply.AsSpan(5, reply[4])), cancellationToken).ConfigureAwait(false),
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
        var addresses = await Dns.GetHostAddressesAsync(domain, cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault() ?? throw new IOException("SOCKS5 reply domain resolved to no addresses.");
    }
}

public interface IUdpProxyTransport : IAsyncDisposable
{
    IPEndPoint RelayEndpoint { get; }
    IPEndPoint LocalEndpoint { get; }
    ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
    ValueTask<Socks5UdpDatagram> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

public interface IUdpProxyTransportFactory
{
    ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, AddressFamily addressFamily, CancellationToken cancellationToken);
}

public sealed class Socks5UdpTransportFactory : IUdpProxyTransportFactory
{
    private readonly SelfTrafficRegistry _selfTraffic;

    public Socks5UdpTransportFactory(SelfTrafficRegistry selfTraffic)
    {
        ArgumentNullException.ThrowIfNull(selfTraffic);
        _selfTraffic = selfTraffic;
    }

    public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, AddressFamily addressFamily, CancellationToken cancellationToken) =>
        await Socks5UdpTransport.CreateAsync(server, addressFamily, _selfTraffic, cancellationToken).ConfigureAwait(false);
}

public sealed class Socks5UdpTransport : IUdpProxyTransport
{
    private readonly Socket _socket;
    private readonly Socks5ControlConnection _control;
    private readonly SelfTrafficRegistry.SelfTrafficToken? _selfTrafficToken;

    private Socks5UdpTransport(Socket socket, Socks5ControlConnection control, IPEndPoint relayEndpoint, SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken)
    {
        _socket = socket;
        _control = control;
        RelayEndpoint = relayEndpoint;
        _selfTrafficToken = selfTrafficToken;
    }

    public IPEndPoint RelayEndpoint { get; }
    public IPEndPoint LocalEndpoint => (IPEndPoint)_socket.LocalEndPoint!;

    public static async ValueTask<Socks5UdpTransport> CreateAsync(Socks5Server server, AddressFamily addressFamily, SelfTrafficRegistry selfTraffic, CancellationToken cancellationToken)
    {
        var socket = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
        // Register the TCP control connection's exact tuple before its SYN leaves the host, so a
        // catch-all proxy rule never recursively intercepts the UDP ASSOCIATE control socket.
        var control = await Socks5ControlConnection.ConnectAsync(server, cancellationToken, (local, remote) =>
            selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(
                TransportProtocol.Tcp,
                Endpoint.From(local.Address, checked((ushort)local.Port)),
                Endpoint.From(remote.Address, checked((ushort)remote.Port))))).ConfigureAwait(false);
        try
        {
            socket.Bind(new IPEndPoint(addressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0));
            var relay = await control.UdpAssociateAsync((IPEndPoint)socket.LocalEndPoint!, cancellationToken).ConfigureAwait(false);
            // Register the relay transport tuple in the loop-prevention registry so catch-all proxy
            // rules never recursively intercept WinForward's own UDP relay traffic (design §10).
            var local = Endpoint.From(((IPEndPoint)socket.LocalEndPoint!).Address, checked((ushort)((IPEndPoint)socket.LocalEndPoint!).Port));
            var remote = Endpoint.From(relay.Address, checked((ushort)relay.Port));
            var token = selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Udp, local, remote));
            return new Socks5UdpTransport(socket, control, relay, token);
        }
        catch
        {
            socket.Dispose();
            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var framed = Socks5UdpCodec.Encode(destination.Address, (ushort)destination.Port, payload.Span);
        _ = await _socket.SendToAsync(framed, SocketFlags.None, RelayEndpoint, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Socks5UdpDatagram> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        EndPoint sender = RelayEndpoint.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
        var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
        if (!result.RemoteEndPoint.Equals(RelayEndpoint)) throw new IOException("SOCKS5 UDP packet came from an unexpected relay endpoint.");
        // M2: the SOCKS5 UDP wire format carries no interface scope, so propagate the relay
        // endpoint's IPv6 scope into reconstruction to keep a link-local decoded address routable.
        var scopeId = RelayEndpoint.Address.AddressFamily == AddressFamily.InterNetworkV6 ? RelayEndpoint.Address.ScopeId : 0;
        if (!Socks5UdpCodec.TryDecode(buffer.Span[..result.ReceivedBytes], out var datagram, scopeId)) throw new IOException("SOCKS5 UDP relay returned a malformed datagram.");
        return datagram;
    }

    public async ValueTask DisposeAsync()
    {
        _socket.Dispose();
        _selfTrafficToken?.Dispose();
        await _control.DisposeAsync().ConfigureAwait(false);
    }
}
