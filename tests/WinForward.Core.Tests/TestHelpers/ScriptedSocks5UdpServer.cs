using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;

namespace WinForward.Core.Tests;

/// <summary>
/// A loopback SOCKS5 server that serves the greeting plus UDP ASSOCIATE on every accepted control
/// connection and then holds it open, so tests can drive many associations through the pool and
/// observe when the client's control connection ends. Each connection advertises the configured
/// relay endpoint unless <see cref="AdvertiseNext"/> overrides it for the next one (the
/// re-association shape), and <see cref="DropControlConnections"/> closes the live connections the
/// way a restarting SOCKS5 server does.
/// </summary>
internal sealed class ScriptedSocks5UdpServer : IAsyncDisposable
{
    private readonly IPEndPoint _relayEndpoint;
    private readonly Func<int, IPEndPoint>? _relayEndpointFactory;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Socket, byte> _controls = new();
    private readonly Queue<IPEndPoint> _advertised = new();
    private readonly Lock _gate = new();
    private readonly Task _acceptLoop;
    private int _connections;
    private int _liveConnections;
    private int _associateReplies;
    private int _disposed;

    /// <summary>Starts the listener on an ephemeral loopback port.</summary>
    /// <param name="relayEndpoint">The relay advertised on every connection without an override.</param>
    /// <param name="relayEndpointFactory">Optional per-connection relay (the argument is the zero-based control-connection ordinal), so tests can tell associations apart.</param>
    public ScriptedSocks5UdpServer(IPEndPoint relayEndpoint, Func<int, IPEndPoint>? relayEndpointFactory = null)
        : this(relayEndpoint, relayEndpointFactory, controlPort: 0)
    {
    }

    private ScriptedSocks5UdpServer(IPEndPoint relayEndpoint, Func<int, IPEndPoint>? relayEndpointFactory, int controlPort)
    {
        _relayEndpoint = relayEndpoint;
        _relayEndpointFactory = relayEndpointFactory;
        _listener = new TcpListener(IPAddress.Loopback, controlPort);
        _listener.Start();
        ControlEndpoint = (IPEndPoint)_listener.LocalEndpoint;
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    /// <summary>
    /// Restarts a server on a stopped instance's control port (the caller stopped it), so a test can
    /// prove a flow re-establishes immediately after its association was lost.
    /// </summary>
    public static async ValueTask<ScriptedSocks5UdpServer> StartOnPortAsync(int controlPort, IPEndPoint relayEndpoint)
    {
        for (var attempt = 0; attempt <= 20; attempt++)
        {
            try
            {
                return new ScriptedSocks5UdpServer(relayEndpoint, relayEndpointFactory: null, controlPort);
            }
            catch (SocketException) when (attempt < 20)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
        }

        throw new IOException(string.Create(CultureInfo.InvariantCulture, $"The SOCKS5 control port {controlPort} did not become bindable again."));
    }

    public IPEndPoint ControlEndpoint { get; }

    public Socks5Server Server => new("scripted", "127.0.0.1", checked((ushort)ControlEndpoint.Port), Username: null, Password: null);

    /// <summary>Total control connections accepted (one per dial; the recovery count is this minus one).</summary>
    public int ConnectionCount => Volatile.Read(ref _connections);

    /// <summary>Control connections still open; zero after every client-side association is gone.</summary>
    public int LiveConnectionCount => Volatile.Read(ref _liveConnections);

    /// <summary>Completed greeting + UDP ASSOCIATE exchanges.</summary>
    public int AssociateReplyCount => Volatile.Read(ref _associateReplies);

    /// <summary>Advertises <paramref name="relayEndpoint"/> on the next control connection only.</summary>
    public void AdvertiseNext(IPEndPoint relayEndpoint)
    {
        lock (_gate) _advertised.Enqueue(relayEndpoint);
    }

    /// <summary>Closes every live control connection without stopping the listener: the peers observe EOF.</summary>
    public void DropControlConnections()
    {
        foreach (var control in _controls.Keys) control.Dispose();
    }

    /// <summary>Stops accepting and closes every live control connection, modelling a dead SOCKS5 server.</summary>
    public async ValueTask StopAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        DropControlConnections();
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

            _controls.TryAdd(socket, 0);
            var ordinal = Interlocked.Increment(ref _connections) - 1;
            Interlocked.Increment(ref _liveConnections);
            _ = ServeAsync(socket, ordinal, token);
        }
    }

    private async Task ServeAsync(Socket socket, int ordinal, CancellationToken token)
    {
        try
        {
            using (socket)
            {
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                var greeting = new byte[2];
                await stream.ReadExactlyAsync(greeting, token).ConfigureAwait(false);
                var methods = new byte[greeting[1]];
                await stream.ReadExactlyAsync(methods, token).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 5, 0 }, token).ConfigureAwait(false);

                var prefix = new byte[4];
                await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
                var addressLength = prefix[3] switch
                {
                    1 => 4,
                    4 => 16,
                    3 => -1,
                    _ => -2,
                };
                if (addressLength == -1)
                {
                    var domainLength = new byte[1];
                    await stream.ReadExactlyAsync(domainLength, token).ConfigureAwait(false);
                    addressLength = domainLength[0];
                }

                if (addressLength >= 0)
                {
                    var remainder = new byte[addressLength + 2];
                    await stream.ReadExactlyAsync(remainder, token).ConfigureAwait(false);
                }

                var relay = TakeAdvertised(ordinal);
                var addressBytes = relay.Address.GetAddressBytes();
                var reply = new byte[4 + addressBytes.Length + 2];
                reply[0] = 5;
                reply[1] = 0;
                reply[3] = relay.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
                addressBytes.CopyTo(reply, 4);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4 + addressBytes.Length), checked((ushort)relay.Port));
                await stream.WriteAsync(reply, token).ConfigureAwait(false);
                Interlocked.Increment(ref _associateReplies);

                // Hold the association open: the client's watchdog blocks on this read and observes
                // EOF the moment the connection is dropped or the client closes it. A server that
                // sent bytes on a UDP association's control connection would be violating RFC 1928,
                // so anything read here is drained and ignored.
                var probe = new byte[1];
                var read = await stream.ReadAsync(probe, token).ConfigureAwait(false);
                while (read > 0) read = await stream.ReadAsync(probe, token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            GC.KeepAlive(exception);
        }
        finally
        {
            Interlocked.Decrement(ref _liveConnections);
            _controls.TryRemove(socket, out _);
        }
    }

    private IPEndPoint TakeAdvertised(int ordinal)
    {
        lock (_gate)
        {
            if (_advertised.Count > 0) return _advertised.Dequeue();
        }

        return _relayEndpointFactory?.Invoke(ordinal) ?? _relayEndpoint;
    }
}
