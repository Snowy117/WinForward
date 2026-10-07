using System.Net.Sockets;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client;

internal sealed class FrameBuffer
{
    private readonly byte[] _buffer;

    internal FrameBuffer(int payloadBytes)
    {
        PayloadBytes = payloadBytes;
        _buffer = new byte[FrameCodec.HeaderSize + payloadBytes + FrameCodec.TrailerSize];
    }

    private int PayloadBytes { get; }

    internal Memory<byte> Memory => _buffer;

    internal int Build(uint connectionId, ulong sequence, long sendTicks) => Build(connectionId, sequence, sendTicks, PayloadBytes);

    internal int Build(uint connectionId, ulong sequence, long sendTicks, int payloadBytes)
    {
        Filler.Fill(connectionId, sequence, _buffer.AsSpan(FrameCodec.HeaderSize, payloadBytes));
        return FrameCodec.WriteFrameInPlace(_buffer, connectionId, sequence, (ulong)sendTicks, payloadBytes);
    }

    internal void FlipPayloadByte(int payloadBytes, int offset)
    {
        _buffer[FrameCodec.HeaderSize + (offset % payloadBytes)] ^= 0x5A;
    }

    internal int RecomputeChecksum(int payloadBytes) => FrameCodec.FinishFrame(_buffer, payloadBytes);
}

/// <summary>
/// The socket operations every TCP arm shares. A connect failure is a result rather than an exception
/// (D18.4): an arm that cannot reach its target records that and keeps going instead of unwinding the
/// whole run, and the deadline still bounds whatever it does next.
/// </summary>
internal static class SocketOps
{
    internal static async ValueTask SendCommandAsync(Socket socket, uint connectionId, TcpMode mode, uint expectedBytes, CancellationToken cancellationToken)
    {
        var frame = new byte[FrameCodec.HeaderSize + TcpCommand.PayloadLength + FrameCodec.TrailerSize];
        TcpCommand.Write(frame.AsSpan(FrameCodec.HeaderSize), mode, expectedBytes);
        FrameCodec.WriteFrameInPlace(frame, connectionId, FrameCodec.CommandSequence, (ulong)Clock.Now, TcpCommand.PayloadLength);
        await socket.SendAsync(frame, SocketFlags.None, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<LaneOpenResult> TryConnectAsync(Socket socket, System.Net.EndPoint endPoint, CancellationToken cancellationToken)
    {
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            return new LaneOpenResult(Ok: true, Error: null);
        }
        catch (SocketException exception)
        {
            return new LaneOpenResult(Ok: false, Error: exception.Message);
        }
        catch (OperationCanceledException)
        {
            return new LaneOpenResult(Ok: false, Error: "the connect was cancelled");
        }
        catch (ObjectDisposedException)
        {
            return new LaneOpenResult(Ok: false, Error: "the socket was already closed");
        }
    }

    internal static void ShutdownQuietly(Socket socket, SocketShutdown direction)
    {
        try
        {
            socket.Shutdown(direction);
        }
        catch (SocketException)
        {
            /* the peer is already gone; the outcome was recorded before this point */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
    }
}
