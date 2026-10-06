using System.Net.Sockets;
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

internal static class SocketOps
{
    internal static async ValueTask SendCommandAsync(Socket socket, uint connectionId, TcpMode mode, uint expectedBytes, CancellationToken cancellationToken)
    {
        var frame = new byte[FrameCodec.HeaderSize + TcpCommand.PayloadLength + FrameCodec.TrailerSize];
        TcpCommand.Write(frame.AsSpan(FrameCodec.HeaderSize), mode, expectedBytes);
        FrameCodec.WriteFrameInPlace(frame, connectionId, FrameCodec.CommandSequence, (ulong)Clock.Now, TcpCommand.PayloadLength);
        await socket.SendAsync(frame, SocketFlags.None, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<bool> TryConnectAsync(Socket socket, System.Net.EndPoint endPoint, CancellationToken cancellationToken)
    {
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
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
