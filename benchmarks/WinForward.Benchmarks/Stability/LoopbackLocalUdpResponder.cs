using System.Net;
using System.Net.Sockets;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The local hop's server side: one loopback UDP socket that answers every datagram it receives back
/// to that datagram's own sender, and counts what it received. The analogue of
/// <see cref="LoopbackSocks5UdpServer"/> with the SOCKS5 machinery removed — no TCP control channel,
/// no UDP ASSOCIATE, no datagram codec, and no last-sender state: the reply target is the endpoint
/// the receive call itself reported for that datagram, read and used inside the same loop iteration,
/// and the type holds no field a previous sender could be kept in. One socket serves every flow of
/// the run, which is what makes the absence of that field observable: the SOCKS5 server's
/// last-sender write path, driven by the same flows at the same pacing, misdelivers most of a wave.
/// The payload is echoed verbatim, so the harness's own datagram header survives the round trip and
/// the client's per-flow attribution still works.
/// </summary>
internal sealed class LoopbackLocalUdpResponder : IAsyncDisposable
{
    /// <summary>
    /// Receive loops sharing the one socket, mirroring <see cref="EchoReceiver"/>: the socket is the
    /// only one this process offers the flows, so a single loop would serialize every flow's answer.
    /// </summary>
    private const int ReceiveLoopCount = 4;

    private readonly Socket _socket;
    private readonly Task[] _loops;
    private long _datagramsReceived;
    private long _datagramsReplied;

    public LoopbackLocalUdpResponder()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 16 << 20,
            Blocking = false,
        };
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Endpoint = (IPEndPoint)_socket.LocalEndPoint!;
        _loops = new Task[ReceiveLoopCount];
        for (var index = 0; index < _loops.Length; index++)
        {
            _loops[index] = Task.Run(ReceiveLoopAsync);
        }
    }

    /// <summary>The endpoint a local target points at: replies leave from this address and port, which is what the client's transport validates them against.</summary>
    public IPEndPoint Endpoint { get; }

    /// <summary>Datagrams the responder received, i.e. the flows' payloads that arrived on the local hop.</summary>
    public long DatagramsReceived => Interlocked.Read(ref _datagramsReceived);

    /// <summary>Datagrams the responder answered; a shortfall against <see cref="DatagramsReceived"/> is a reply send that faulted.</summary>
    public long DatagramsReplied => Interlocked.Read(ref _datagramsReplied);

    public async ValueTask DisposeAsync()
    {
        _socket.Dispose();
        await Task.WhenAll(_loops).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65_536];
        EndPoint anySource = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anySource).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            Interlocked.Increment(ref _datagramsReceived);
            var payload = buffer.AsMemory(0, result.ReceivedBytes);
            // The reply's destination is this datagram's own sender, taken from the receive result:
            // no shared field, no "current client", nothing a concurrent flow could overwrite.
            try
            {
                _ = _socket.SendTo(payload.Span, SocketFlags.None, result.RemoteEndPoint);
                Interlocked.Increment(ref _datagramsReplied);
                continue;
            }
            catch (SocketException)
            {
                // WouldBlock: the kernel send queue is momentarily full; use the overlapped send below.
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            try
            {
                _ = await _socket.SendToAsync(payload, SocketFlags.None, result.RemoteEndPoint).ConfigureAwait(false);
                Interlocked.Increment(ref _datagramsReplied);
            }
            catch (SocketException)
            {
                // A flow socket that vanished mid-answer must not end the responder's loop.
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }
}
