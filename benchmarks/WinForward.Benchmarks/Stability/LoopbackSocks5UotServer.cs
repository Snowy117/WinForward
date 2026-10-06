using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinForward.Protocols;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The benchmark harness's UDP-over-TCP v2 (connect mode) server: the sibling of
/// <see cref="LoopbackSocks5UdpServer"/> for the <c>--target uot</c> column, serving the wire
/// sequence the product's UoT transport speaks — greeting (plus the RFC 1929 message when the
/// scenario constructed it with a credential pair), a <c>CONNECT</c> to
/// <see cref="UotCodec.MagicAddress"/>, the UoT request header (<c>isConnect</c> set), and then
/// <c>u16be length | payload</c> frames — bridged through one upstream UDP socket per connection
/// toward the scenario's echo destination.
/// <para>
/// Its defining property is the order it writes the CONNECT reply in: <b>the reply is written only
/// after the first frame has been read</b>. A client that waits for a handshake reply before sending
/// its first datagram is refused rather than served, which makes the product's pipelined flight an
/// observation instead of a construction. The first frame is forwarded toward the echo destination
/// before <see cref="LoopbackSocks5UotServer"/>'s configured reply delay, so the flow's first
/// datagram really does ride the flight the reply is still owing.
/// </para>
/// <para>
/// There is no last-sender source field here, unlike its native sibling: connect mode binds the
/// connection to one destination, so a reply arriving on a connection's upstream socket belongs to
/// that connection's flow and to no other by construction.
/// </para>
/// <para>
/// Protocol violations — a CONNECT for another target, a UoT header that is not connect mode, a
/// greeting or credential message the configured target cannot answer — are counted
/// (<see cref="ProtocolViolations"/>) and reported on stderr, so a fixture that never served the
/// sequence the product is supposed to speak cannot pass as a zero-traffic row.
/// </para>
/// </summary>
internal sealed class LoopbackSocks5UotServer : IAsyncDisposable
{
    /// <summary>
    /// The bound on concurrent connections, set to the product's own UDP session capacity ceiling so
    /// the harness can serve every concurrent flow a default-capacity run can hold.
    /// </summary>
    private const int MaximumConnections = 16_384;

    /// <summary>
    /// The largest payload one frame can carry (<c>u16be</c> length prefix) and therefore the size
    /// of each connection's frame buffers: one managed 64 KiB read scratch and one
    /// 64 KiB + 2 header write scratch per live connection, never pooled across connections —
    /// sharing a buffer would serialize flows on the harness instead of measuring the product.
    /// </summary>
    private const int MaximumDatagramBytes = 65_535;

    private readonly IPEndPoint _echoDestination;
    private readonly string? _username;
    private readonly string? _password;
    private readonly TimeSpan _connectReplyDelay;
    private readonly TcpListener _controlListener;
    private readonly ConcurrentDictionary<UotConnection, byte> _connections = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private long _controlConnections;
    private long _connectReplies;
    private long _framesReceived;
    private long _framesReplied;
    private long _protocolViolations;
    private int _connectionCount;
    private int _disposed;

    /// <summary>Starts the listener on an ephemeral loopback port and serves every connection it accepts.</summary>
    /// <param name="echoDestination">The UDP echo target every frame is forwarded to and every reply is framed back from.</param>
    /// <param name="connectReplyDelay">The delay applied to the deferred CONNECT reply, modelling a remote server's round trip.</param>
    /// <param name="username">The username the flow must present; null serves the no-authentication method.</param>
    /// <param name="password">The password paired with <paramref name="username"/>.</param>
    public LoopbackSocks5UotServer(IPEndPoint echoDestination, TimeSpan connectReplyDelay = default, string? username = null, string? password = null)
    {
        _echoDestination = echoDestination;
        _connectReplyDelay = connectReplyDelay;
        _username = username;
        _password = password;
        _controlListener = new TcpListener(IPAddress.Loopback, 0);
        _controlListener.Start(1024);
        ControlEndpoint = (IPEndPoint)_controlListener.LocalEndpoint;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), _shutdown.Token);
    }

    public IPEndPoint ControlEndpoint { get; }

    /// <summary>
    /// TCP connections accepted and served. One connection per flow is the shape the UoT column
    /// measures, so this is the mode's establishment half, the analogue of the native server's
    /// <see cref="LoopbackSocks5UdpServer.ControlConnections"/>.
    /// </summary>
    public long ControlConnections => Interlocked.Read(ref _controlConnections);

    /// <summary>CONNECT replies written, one per connection whose first frame arrived; the analogue of the native server's <c>UDP ASSOCIATE</c> replies.</summary>
    public long ConnectReplies => Interlocked.Read(ref _connectReplies);

    /// <summary>Datagram frames read off the connections, i.e. datagrams the client sent over the streams.</summary>
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>Datagram frames written back onto the connections, i.e. the echo replies the flows' first datagrams produced.</summary>
    public long FramesReplied => Interlocked.Read(ref _framesReplied);

    /// <summary>Wire sequences the product must never produce, counted so a fixture that refused a connection is visible in the row instead of reading as a silent zero.</summary>
    public long ProtocolViolations => Interlocked.Read(ref _protocolViolations);

    /// <summary>
    /// Live connections; each owns one accepted TCP socket and one upstream UDP socket, so this is
    /// the harness's own two-socket contribution to the hosting process's descriptor count.
    /// </summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

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

            if (Interlocked.Increment(ref _connectionCount) > MaximumConnections)
            {
                Interlocked.Decrement(ref _connectionCount);
                socket.Dispose();
                continue;
            }

            var connection = new UotConnection(socket, this, cancellation);
            Interlocked.Increment(ref _controlConnections);
            _connections.TryAdd(connection, 0);
            _ = connection.RunAsync().ContinueWith(
                completed => { _ = completed; _connections.TryRemove(connection, out _); Interlocked.Decrement(ref _connectionCount); },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>Counts a wire sequence the product must never produce and reports it, so a run whose fixture refused connections is loud rather than empty.</summary>
    private void RecordProtocolViolation(string message)
    {
        Interlocked.Increment(ref _protocolViolations);
        Console.Error.WriteLine($"LoopbackSocks5UotServer: {message}");
    }

    private sealed class UotConnection : IAsyncDisposable
    {
        /// <summary>The RFC 1929 version byte this fixture answers with (the credential sub-negotiation, not the SOCKS5 greeting).</summary>
        private const byte CredentialVersion = 1;

        /// <summary>The no-authentication and username/password methods of the SOCKS5 greeting.</summary>
        private const byte NoAuthenticationMethod = 0;
        private const byte UsernamePasswordMethod = 2;

        private static readonly byte[] s_noAuthMethodReply = [5, NoAuthenticationMethod];
        private static readonly byte[] s_credentialsMethodReply = [5, UsernamePasswordMethod];
        private static readonly byte[] s_credentialsAcceptedReply = [CredentialVersion, 0];
        private static readonly byte[] s_credentialsRefusedReply = [CredentialVersion, 1];

        private readonly Socket _control;
        private readonly Socket _upstream;
        private readonly LoopbackSocks5UotServer _owner;
        private readonly CancellationToken _shutdown;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly byte[] _frameBuffer = new byte[MaximumDatagramBytes];
        private readonly byte[] _replyBuffer = new byte[UotCodec.FrameHeaderSize + MaximumDatagramBytes];
        private Task? _replyLoop;
        private int _disposed;

        public UotConnection(Socket control, LoopbackSocks5UotServer owner, CancellationToken shutdown)
        {
            // The stream is a byte pipe carrying small handshake replies and datagram frames back to
            // back: Nagle x delayed-ACK would stall each one ~40 ms, a harness artifact the product
            // already excludes on its own side of the same connection
            // (Socks5ControlConnection sets TCP_NODELAY as soon as the connect succeeds).
            control.NoDelay = true;
            _control = control;
            _owner = owner;
            _shutdown = shutdown;
            _upstream = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveBufferSize = 4 << 20,
                Blocking = false,
            };
            _upstream.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        }

        public async Task RunAsync()
        {
            try
            {
                await using var stream = new NetworkStream(_control, ownsSocket: true);
                await ServeAsync(stream).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException or EndOfStreamException or IOException)
            {
                // Soak-harness server side: client disconnects, shutdown races and a client that
                // never finishes its handshake end the connection quietly; the finally below still
                // releases its sockets.
            }
            finally
            {
                await DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Serves one flow: the greeting negotiation, the magic CONNECT, the UoT request header, and
        /// then frames until the client closes. The CONNECT reply is written on the first frame —
        /// after it has been forwarded — and the reply loop only starts then, so no frame can reach
        /// the client before the reply the client parses first.
        /// </summary>
        private async Task ServeAsync(NetworkStream stream)
        {
            if (!await NegotiateAsync(stream).ConfigureAwait(false)) return;
            if (!await ReadConnectRequestAsync(stream).ConfigureAwait(false)) return;
            if (!await ReadUotRequestAsync(stream).ConfigureAwait(false)) return;

            var replyWritten = false;
            while (await ReadFrameAsync(stream).ConfigureAwait(false) is { } frameLength)
            {
                Interlocked.Increment(ref _owner._framesReceived);
                await ForwardAsync(frameLength).ConfigureAwait(false);
                if (replyWritten) continue;
                if (_owner._connectReplyDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_owner._connectReplyDelay, _shutdown).ConfigureAwait(false);
                }

                await WriteConnectReplyAsync(stream).ConfigureAwait(false);
                replyWritten = true;
                // Started inline, like the native fixture's relay loop: the call runs to its first
                // await on this thread, so the echo reply's write does not wait for a thread-pool
                // work item — a real server has its receive loop running before the reply too.
                _replyLoop = ReplyLoopAsync(stream);
            }
        }

        /// <summary>
        /// The SOCKS5 greeting: reads the offered methods and answers the one the fixture's own
        /// configuration serves, then completes the RFC 1929 exchange when a credential pair was
        /// configured. False means the connection must not be served (and the refusal has been
        /// counted or answered).
        /// </summary>
        private async ValueTask<bool> NegotiateAsync(NetworkStream stream)
        {
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, _shutdown).ConfigureAwait(false);
            if (greeting[0] != 5)
            {
                _owner.RecordProtocolViolation($"The greeting declared version {greeting[0]}, not 5.");
                return false;
            }

            var methods = new byte[greeting[1]];
            await stream.ReadExactlyAsync(methods, _shutdown).ConfigureAwait(false);
            if (_owner._username is null || _owner._password is null)
            {
                if (Array.IndexOf(methods, NoAuthenticationMethod) < 0)
                {
                    _owner.RecordProtocolViolation("The client did not offer the no-authentication method this fixture serves.");
                    return false;
                }

                await WriteAsync(stream, s_noAuthMethodReply).ConfigureAwait(false);
                return true;
            }

            if (Array.IndexOf(methods, UsernamePasswordMethod) < 0)
            {
                _owner.RecordProtocolViolation("The client did not offer the username/password method this fixture serves.");
                return false;
            }

            await WriteAsync(stream, s_credentialsMethodReply).ConfigureAwait(false);
            return await ReadCredentialsAsync(stream).ConfigureAwait(false);
        }

        private async ValueTask<bool> ReadCredentialsAsync(NetworkStream stream)
        {
            var header = new byte[2];
            await stream.ReadExactlyAsync(header, _shutdown).ConfigureAwait(false);
            if (header[0] != CredentialVersion)
            {
                _owner.RecordProtocolViolation($"The credential message declared version {header[0]}, not {CredentialVersion}.");
                return false;
            }

            var user = new byte[header[1]];
            await stream.ReadExactlyAsync(user, _shutdown).ConfigureAwait(false);
            var secretLength = new byte[1];
            await stream.ReadExactlyAsync(secretLength, _shutdown).ConfigureAwait(false);
            var secret = new byte[secretLength[0]];
            await stream.ReadExactlyAsync(secret, _shutdown).ConfigureAwait(false);
            var accepted = string.Equals(Encoding.UTF8.GetString(user), _owner._username, StringComparison.Ordinal)
                && string.Equals(Encoding.UTF8.GetString(secret), _owner._password, StringComparison.Ordinal);
            await WriteAsync(stream, accepted ? s_credentialsAcceptedReply : s_credentialsRefusedReply).ConfigureAwait(false);
            return accepted;
        }

        /// <summary>
        /// Reads the request the UoT client's flight opens with and asserts it is the domain-typed
        /// <c>CONNECT</c> to <see cref="UotCodec.MagicAddress"/>: any other command or target means
        /// the column did not measure the mode it is stamped with, so it is counted as a violation.
        /// </summary>
        private async ValueTask<bool> ReadConnectRequestAsync(NetworkStream stream)
        {
            var prefix = new byte[4];
            await stream.ReadExactlyAsync(prefix, _shutdown).ConfigureAwait(false);
            if (prefix[0] != 5 || prefix[1] != (byte)Socks5Command.Connect || prefix[2] != 0 || prefix[3] != 3)
            {
                _owner.RecordProtocolViolation(
                    $"The request was not a domain-typed SOCKS5 CONNECT (VER {prefix[0]}, CMD {prefix[1]}, RSV {prefix[2]}, ATYP {prefix[3]}).");
                return false;
            }

            var domainLength = new byte[1];
            await stream.ReadExactlyAsync(domainLength, _shutdown).ConfigureAwait(false);
            var domain = new byte[domainLength[0]];
            await stream.ReadExactlyAsync(domain, _shutdown).ConfigureAwait(false);
            var port = new byte[2];
            await stream.ReadExactlyAsync(port, _shutdown).ConfigureAwait(false);
            var target = Encoding.ASCII.GetString(domain);
            if (!string.Equals(target, UotCodec.MagicAddress, StringComparison.Ordinal))
            {
                _owner.RecordProtocolViolation($"The UoT CONNECT target was '{target}', not the magic address '{UotCodec.MagicAddress}'.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads the UoT request header and asserts connect mode. The destination is decoded with the
        /// ordinary SOCKS address mapping — the mapping the pinned server reads the request with
        /// (<c>0x01</c> IPv4, <c>0x04</c> IPv6, <c>0x03</c> domain) — not with a mirror of the
        /// client's codec, and an address family that mapping does not define is refused as
        /// <c>unknown address family</c>. The decoded destination is not used: the fixture forwards
        /// every frame to the scenario's echo destination instead, so the synthetic flow-key addresses
        /// the soak builds never leave the host.
        /// </summary>
        private async ValueTask<bool> ReadUotRequestAsync(NetworkStream stream)
        {
            var header = new byte[2];
            await stream.ReadExactlyAsync(header, _shutdown).ConfigureAwait(false);
            if (header[0] != 1)
            {
                _owner.RecordProtocolViolation($"The UoT request header declared isConnect={header[0]}; connect mode requires 1.");
                return false;
            }

            if (header[1] == Socks5Messages.AddressTypeDomain)
            {
                _owner.RecordProtocolViolation($"The UoT request destination used the SOCKS domain address type {header[1]}; connect mode carries an IP destination.");
                return false;
            }

            var addressFieldLength = Socks5Messages.AddressFieldLength(header[1]);
            if (addressFieldLength < 0)
            {
                _owner.RecordProtocolViolation($"unknown address family: {header[1]}");
                return false;
            }

            var remainder = new byte[addressFieldLength + 2];
            await stream.ReadExactlyAsync(remainder, _shutdown).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Reads one frame into the connection's read scratch and returns its payload length, or null
        /// when the client closed the connection between frames. The payload starts at
        /// <see cref="UotCodec.FrameHeaderSize"/> in the same buffer, which keeps the read path
        /// allocation-free.
        /// </summary>
        private async ValueTask<int?> ReadFrameAsync(NetworkStream stream)
        {
            try
            {
                await stream.ReadExactlyAsync(_frameBuffer.AsMemory(0, UotCodec.FrameHeaderSize), _shutdown).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(_frameBuffer);
            if (length == 0) return 0;
            await stream.ReadExactlyAsync(_frameBuffer.AsMemory(UotCodec.FrameHeaderSize, length), _shutdown).ConfigureAwait(false);
            return length;
        }

        private async ValueTask ForwardAsync(int frameLength)
        {
            var payload = _frameBuffer.AsMemory(UotCodec.FrameHeaderSize, frameLength);
            try
            {
                _ = _upstream.SendTo(payload.Span, SocketFlags.None, _owner._echoDestination);
                return;
            }
            catch (SocketException)
            {
                // WouldBlock: the kernel send queue is momentarily full; use the overlapped send below.
            }

            _ = await _upstream.SendToAsync(payload, SocketFlags.None, _owner._echoDestination, _shutdown).ConfigureAwait(false);
        }

        /// <summary>
        /// Frames every echo the upstream socket receives back onto the connection's stream. The
        /// socket only ever talks to the echo destination, so no source check is needed: the
        /// connection's stream is bound to that one destination by construction.
        /// </summary>
        private async Task ReplyLoopAsync(NetworkStream stream)
        {
            EndPoint anySource = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _upstream.ReceiveFromAsync(_replyBuffer.AsMemory(UotCodec.FrameHeaderSize), SocketFlags.None, anySource, _shutdown).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!UotCodec.TryWriteFrameHeader(checked((ushort)result.ReceivedBytes), _replyBuffer, out _)) break;
                try
                {
                    await WriteAsync(stream, _replyBuffer.AsMemory(0, UotCodec.FrameHeaderSize + result.ReceivedBytes)).ConfigureAwait(false);
                    _ = Interlocked.Increment(ref _owner._framesReplied);
                }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    // The client is gone; the connection's read loop owns the end of the flow.
                    break;
                }
            }
        }

        private async ValueTask WriteConnectReplyAsync(NetworkStream stream)
        {
            var addressBytes = _owner.ControlEndpoint.Address.GetAddressBytes();
            var reply = new byte[4 + addressBytes.Length + 2];
            reply[0] = 5;
            reply[1] = 0;
            reply[2] = 0;
            reply[3] = addressBytes.Length == 4 ? (byte)1 : (byte)4;
            addressBytes.CopyTo(reply, 4);
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4 + addressBytes.Length), checked((ushort)_owner.ControlEndpoint.Port));
            await WriteAsync(stream, reply).ConfigureAwait(false);
            _ = Interlocked.Increment(ref _owner._connectReplies);
        }

        /// <summary>
        /// Writes one message under the connection's write gate: the CONNECT reply and every framed
        /// echo share the stream, and one connection's frames must never interleave.
        /// </summary>
        private async ValueTask WriteAsync(NetworkStream stream, ReadOnlyMemory<byte> message)
        {
            await _writeGate.WaitAsync(_shutdown).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(message, _shutdown).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        /// <summary>
        /// Releases the connection's upstream socket — which ends its reply loop — and then the write
        /// gate, exactly once.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _upstream.Dispose();
            if (_replyLoop is { } replyLoop)
            {
                try
                {
                    await replyLoop.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    GC.KeepAlive(exception);
                }
            }

            _writeGate.Dispose();
        }
    }
}
