using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WinForward.Protocols;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// A minimal loopback SOCKS5 TCP server exercising the real protocol surface the production
/// client stack speaks: no-auth greeting, CONNECT request parsing, a success reply with an
/// IPv4 wildcard bound address, and a bidirectional byte echo afterwards. Wire bytes follow
/// RFC 1928 exactly; <c>Socks5ControlConnection</c> validates the reply format, so any drift
/// here surfaces immediately as a handshake failure in the benchmarks that use this server.
/// Serves the SOCKS5 handshake benchmark and the TCP throughput soak.
/// </summary>
internal sealed class LoopbackSocks5TcpServer : IAsyncDisposable
{
    private const int MaximumControlConnections = 4096;

    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private long _connectReplies;
    private long _bytesEchoed;
    private int _connectionCount;
    private int _disposed;

    public LoopbackSocks5TcpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1024);
        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), _shutdown.Token);
    }

    public IPEndPoint Endpoint { get; }

    /// <summary>Raw total (includes warmup) of successful CONNECT replies sent to clients.</summary>
    public long ConnectReplies => Interlocked.Read(ref _connectReplies);

    /// <summary>Raw total of post-reply payload bytes received (and echoed back) across connections.</summary>
    public long BytesEchoed => Interlocked.Read(ref _bytesEchoed);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach (var connection in _connections.Keys)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        await _acceptLoop.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (Interlocked.Increment(ref _connectionCount) > MaximumControlConnections)
            {
                Interlocked.Decrement(ref _connectionCount);
                socket.Dispose();
                continue;
            }

            var connection = new Connection(socket, this, cancellation);
            _connections.TryAdd(connection, 0);
            _ = connection.RunAsync().ContinueWith(
                completed => { _ = completed; _connections.TryRemove(connection, out _); Interlocked.Decrement(ref _connectionCount); },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private sealed class Connection(Socket socket, LoopbackSocks5TcpServer owner, CancellationToken shutdown) : IAsyncDisposable
    {
        private static readonly byte[] s_noAuthMethodReply = [5, 0];
        private static readonly byte[] s_connectSuccessReply = [5, 0, 0, 1, 0, 0, 0, 0, 0, 0];
        private static readonly byte[] s_requestFailureReply = [5, 1, 0, 1, 0, 0, 0, 0, 0, 0];

        private int _disposed;

        public async Task RunAsync()
        {
            try
            {
                await HandleControlAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException or EndOfStreamException or IOException)
            {
                // Soak-harness server side: client disconnects and shutdown races end the
                // connection quietly; the finally below still releases its socket.
            }
            finally
            {
                await DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task HandleControlAsync()
        {
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, shutdown).ConfigureAwait(false);
            if (greeting[0] != 5 || greeting[1] == 0) return;
            var methods = new byte[greeting[1]];
            await stream.ReadExactlyAsync(methods, shutdown).ConfigureAwait(false);
            // Always select NO AUTHENTICATION: the production client accepts method 0 without
            // further negotiation even when it offered username/password as an alternative.
            await stream.WriteAsync(s_noAuthMethodReply, shutdown).ConfigureAwait(false);

            var request = new byte[4];
            await stream.ReadExactlyAsync(request, shutdown).ConfigureAwait(false);
            if (request[0] != 5) return;
            var addressLength = request[3] switch
            {
                1 => 4,
                4 => 16,
                3 => -1,
                _ => int.MinValue,
            };
            if (addressLength == -1)
            {
                var domainLength = new byte[1];
                await stream.ReadExactlyAsync(domainLength, shutdown).ConfigureAwait(false);
                addressLength = domainLength[0];
            }

            if (addressLength < 0)
            {
                await stream.WriteAsync(s_requestFailureReply, shutdown).ConfigureAwait(false);
                return;
            }

            var remainder = new byte[addressLength + 2];
            await stream.ReadExactlyAsync(remainder, shutdown).ConfigureAwait(false);
            if (request[1] == (byte)Socks5Command.Connect)
            {
                await stream.WriteAsync(s_connectSuccessReply, shutdown).ConfigureAwait(false);
                Interlocked.Increment(ref owner._connectReplies);
                await EchoLoopAsync(stream).ConfigureAwait(false);
            }
            else
            {
                await stream.WriteAsync(s_requestFailureReply, shutdown).ConfigureAwait(false);
            }
        }

        private async Task EchoLoopAsync(NetworkStream stream)
        {
            var buffer = new byte[65_536];
            while (!shutdown.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer, shutdown).ConfigureAwait(false);
                if (count == 0) return;
                Interlocked.Add(ref owner._bytesEchoed, count);
                await stream.WriteAsync(buffer.AsMemory(0, count), shutdown).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
