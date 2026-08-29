using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WinForward.Protocols;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// A minimal loopback SOCKS5 server exercising the real protocol surface the production client
/// stack speaks: no-auth greeting, UDP ASSOCIATE with a per-connection UDP relay socket, SOCKS5
/// UDP datagram decode/encode via the production codec, forwarding to a fixed echo destination,
/// and routing echo replies back to each client's last source endpoint. The decoded destination
/// is deliberately overridden by the constructor's echo destination so synthetic flow-key
/// addresses never leave the host.
/// </summary>
internal sealed class LoopbackSocks5UdpServer : IAsyncDisposable
{
    private const int MaximumControlConnections = 4096;

    private readonly IPEndPoint _echoDestination;
    private readonly TcpListener _controlListener;
    private readonly ConcurrentDictionary<RelayConnection, byte> _connections = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private int _connectionCount;
    private int _disposed;

    public LoopbackSocks5UdpServer(IPEndPoint echoDestination)
    {
        _echoDestination = echoDestination;
        _controlListener = new TcpListener(IPAddress.Loopback, 0);
        _controlListener.Start(1024);
        ControlEndpoint = (IPEndPoint)_controlListener.LocalEndpoint!;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    public IPEndPoint ControlEndpoint { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _controlListener.Stop();
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
                socket = await _controlListener.AcceptSocketAsync(cancellation).ConfigureAwait(false);
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

            var connection = new RelayConnection(socket, _echoDestination, cancellation);
            _connections.TryAdd(connection, 0);
            _ = connection.RunAsync().ContinueWith(
                completed => { _ = completed; _connections.TryRemove(connection, out _); Interlocked.Decrement(ref _connectionCount); },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private sealed class RelayConnection : IAsyncDisposable
    {
        private static readonly byte[] NoAuthMethodReply = [5, 0];
        private static readonly byte[] RequestFailureReply = [5, 1, 0, 1, 0, 0, 0, 0, 0, 0];

        private readonly Socket _control;
        private readonly Socket _relay;
        private readonly IPEndPoint _echoDestination;
        private readonly CancellationToken _shutdown;
        private IPEndPoint? _lastClient;
        private IPAddress? _lastDestinationAddress;
        private ushort _lastDestinationPort;
        private int _disposed;

        public RelayConnection(Socket control, IPEndPoint echoDestination, CancellationToken shutdown)
        {
            _control = control;
            _echoDestination = echoDestination;
            _shutdown = shutdown;
            _relay = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _relay.ReceiveBufferSize = 1 << 20;
            _relay.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        }

        public IPEndPoint RelayEndpoint => (IPEndPoint)_relay.LocalEndPoint!;

        public async Task RunAsync()
        {
            _ = RelayLoopAsync();
            try
            {
                await HandleControlAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException or EndOfStreamException or IOException)
            {
                // Soak-harness server side: client disconnects and shutdown races end the
                // connection quietly; the finally below still releases its relay socket.
            }
            finally
            {
                await DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task HandleControlAsync()
        {
            using var stream = new NetworkStream(_control, ownsSocket: true);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, _shutdown).ConfigureAwait(false);
            if (greeting[0] != 5 || greeting[1] == 0) return;
            var methods = new byte[greeting[1]];
            await stream.ReadExactlyAsync(methods, _shutdown).ConfigureAwait(false);
            await stream.WriteAsync(NoAuthMethodReply, _shutdown).ConfigureAwait(false);

            var request = new byte[4];
            await stream.ReadExactlyAsync(request, _shutdown).ConfigureAwait(false);
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
                await stream.ReadExactlyAsync(domainLength, _shutdown).ConfigureAwait(false);
                addressLength = domainLength[0];
            }

            if (addressLength < 0)
            {
                await stream.WriteAsync(RequestFailureReply, _shutdown).ConfigureAwait(false);
                return;
            }

            var remainder = new byte[addressLength + 2];
            await stream.ReadExactlyAsync(remainder, _shutdown).ConfigureAwait(false);
            if (request[1] == (byte)Socks5Command.UdpAssociate)
            {
                var reply = new byte[10];
                reply[0] = 5;
                reply[1] = 0;
                reply[2] = 0;
                reply[3] = 1;
                _ = IPAddress.Loopback.TryWriteBytes(reply.AsSpan(4, 4), out _);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8, 2), checked((ushort)RelayEndpoint.Port));
                await stream.WriteAsync(reply, _shutdown).ConfigureAwait(false);
            }
            else
            {
                await stream.WriteAsync(RequestFailureReply, _shutdown).ConfigureAwait(false);
            }

            var scratch = new byte[4096];
            while (!_shutdown.IsCancellationRequested)
            {
                if (await stream.ReadAsync(scratch, _shutdown).ConfigureAwait(false) == 0) return;
            }
        }

        private async Task RelayLoopAsync()
        {
            var buffer = new byte[65_536];
            EndPoint anySource = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _relay.ReceiveFromAsync(buffer, SocketFlags.None, anySource).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var sender = (IPEndPoint)result.RemoteEndPoint!;
                var payload = buffer.AsMemory(0, result.ReceivedBytes);
                try
                {
                    if (IsEchoSource(sender))
                    {
                        await SendReplyAsync(payload).ConfigureAwait(false);
                    }
                    else if (Socks5UdpCodec.TryDecode(payload, out var request))
                    {
                        await ForwardAsync(sender, request).ConfigureAwait(false);
                    }
                }
                catch (SocketException)
                {
                    // The peer vanished mid-datagram; the loop keeps serving the soak.
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        private bool IsEchoSource(IPEndPoint sender) => sender.Port == _echoDestination.Port && sender.Address.Equals(_echoDestination.Address);

        private async Task ForwardAsync(IPEndPoint client, Socks5UdpDatagram request)
        {
            _lastClient = client;
            _lastDestinationAddress = request.DestinationAddress;
            _lastDestinationPort = request.DestinationPort;
            _ = await _relay.SendToAsync(request.Payload, SocketFlags.None, _echoDestination).ConfigureAwait(false);
        }

        private async Task SendReplyAsync(ReadOnlyMemory<byte> payload)
        {
            if (_lastClient is null) return;
            var address = _lastDestinationAddress ?? _echoDestination.Address;
            var port = _lastDestinationAddress is not null ? _lastDestinationPort : checked((ushort)_echoDestination.Port);
            var datagram = Socks5UdpCodec.Encode(address, port, payload.Span);
            _ = await _relay.SendToAsync(datagram, SocketFlags.None, _lastClient).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            _control.Dispose();
            _relay.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
