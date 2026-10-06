using System.Net;
using System.Net.Sockets;
using WinForward.Core;

namespace WinForward.TestSupport;

/// <summary>
/// A loopback UDP endpoint that answers every datagram it receives and counts what arrived: the
/// local hop's stand-in for a terminating service on this host (a resolver, for instance), with no
/// wire header, no control channel, and no routing of its own. The default reply echoes the request.
/// </summary>
internal sealed class LoopbackUdpResponder : IAsyncDisposable
{
    /// <summary>The default reply: the request echoed back unchanged.</summary>
    private static readonly Func<ReadOnlyMemory<byte>, byte[]> s_echo = static payload => payload.ToArray();

    private readonly Socket _socket;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<ReadOnlyMemory<byte>, byte[]> _reply;
    private readonly Task _loop;
    private readonly Lock _gate = new();
    private byte[]? _lastPayload;
    private int _received;

    internal LoopbackUdpResponder(Func<ReadOnlyMemory<byte>, byte[]>? reply = null, AddressFamily family = AddressFamily.InterNetwork)
    {
        _reply = reply ?? s_echo;
        _socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        var loopback = family == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        _socket.Bind(new IPEndPoint(loopback, 0));
        var ipEndpoint = (IPEndPoint)_socket.LocalEndPoint!;
        Endpoint = Endpoint.From(ipEndpoint.Address, checked((ushort)ipEndpoint.Port));
        _loop = LoopAsync(_shutdown.Token);
    }

    /// <summary>The bound endpoint as the runtime's endpoint primitive, for configuration and flow keys.</summary>
    internal Endpoint Endpoint { get; }

    /// <summary>How many datagrams this endpoint received.</summary>
    internal int ReceivedCount => Volatile.Read(ref _received);

    /// <summary>The most recent datagram's payload, or null when nothing arrived.</summary>
    internal byte[]? LastPayload
    {
        get
        {
            lock (_gate) return _lastPayload;
        }
    }

    /// <summary>Sends one datagram from this endpoint, the shape a reply takes.</summary>
    internal ValueTask<int> SendToAsync(EndPoint target, byte[] payload, CancellationToken cancellationToken) =>
        _socket.SendToAsync(payload, SocketFlags.None, target, cancellationToken);

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65_535];
        EndPoint sender = _socket.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            // The payload is published before the count: a fact that waits for ReceivedCount and then
            // reads LastPayload would otherwise be able to observe the count with the payload unset.
            lock (_gate) _lastPayload = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
            Interlocked.Increment(ref _received);

            try
            {
                await _socket.SendToAsync(_reply(buffer.AsMemory(0, result.ReceivedBytes)), SocketFlags.None, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancellation is this helper's normal teardown path.
        }

        _shutdown.Dispose();
    }
}

/// <summary>A loopback UDP socket that can send to any endpoint: the "some other host" shape a source-validation fact needs.</summary>
internal sealed class LoopbackUdpSender : IDisposable
{
    private readonly Socket _socket;

    internal LoopbackUdpSender(AddressFamily family = AddressFamily.InterNetwork)
    {
        _socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        var loopback = family == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        _socket.Bind(new IPEndPoint(loopback, 0));
    }

    internal ValueTask<int> SendToAsync(EndPoint target, byte[] payload, CancellationToken cancellationToken) =>
        _socket.SendToAsync(payload, SocketFlags.None, target, cancellationToken);

    public void Dispose() => _socket.Dispose();
}
