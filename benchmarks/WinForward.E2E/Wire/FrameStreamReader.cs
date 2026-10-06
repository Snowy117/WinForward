using System.Net.Sockets;

namespace WinForward.E2E.Wire;

internal enum FrameReadStatus
{
    Frame,
    EndOfStream,
    BadMagic,
    BadLength,
    BadChecksum,
}

internal sealed class FrameStreamReader
{
    private const int DefaultCapacity = 64 * 1024;

    private readonly Func<Memory<byte>, CancellationToken, ValueTask<int>> _read;
    private byte[] _buffer;
    private int _start;
    private int _end;
    private bool _endOfStream;
    private bool _hasFrame;

    internal FrameStreamReader(Socket socket, int capacity = DefaultCapacity)
        : this((buffer, cancellationToken) => socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken), capacity)
    {
    }

    internal FrameStreamReader(Func<Memory<byte>, CancellationToken, ValueTask<int>> read, int capacity = DefaultCapacity)
    {
        _read = read;
        _buffer = new byte[Math.Max(capacity, DefaultCapacity)];
    }

    internal FrameHeader Header { get; private set; }

    internal ReadOnlyMemory<byte> Raw { get; private set; }

    internal ReadOnlyMemory<byte> Payload { get; private set; }

    internal async ValueTask<FrameReadStatus> ReadAsync(CancellationToken cancellationToken)
    {
        if (_hasFrame)
        {
            _start += Raw.Length;
            _hasFrame = false;
        }

        while (true)
        {
            var available = _end - _start;
            if (available >= FrameCodec.HeaderSize)
            {
                if (!FrameCodec.TryReadHeader(_buffer.AsSpan(_start, available), out var header, out var headerError))
                {
                    return headerError == FrameDecodeError.BadMagic ? FrameReadStatus.BadMagic : FrameReadStatus.BadLength;
                }

                var frameLength = header.FrameLength;
                if (available >= frameLength)
                {
                    if (!FrameCodec.TryDecode(_buffer.AsSpan(_start, frameLength), out var decoded, out var payload, out var decodeError))
                    {
                        _start += frameLength;
                        return decodeError == FrameDecodeError.BadChecksum ? FrameReadStatus.BadChecksum : FrameReadStatus.BadMagic;
                    }

                    Header = decoded;
                    Raw = new ReadOnlyMemory<byte>(_buffer, _start, frameLength);
                    Payload = new ReadOnlyMemory<byte>(_buffer, _start + FrameCodec.HeaderSize, payload.Length);
                    _hasFrame = true;
                    return FrameReadStatus.Frame;
                }
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return FrameReadStatus.EndOfStream;
            }
        }
    }

    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_endOfStream)
        {
            return false;
        }

        if (_start > 0 && (_end == _start || _buffer.Length - _end < DefaultCapacity))
        {
            var remaining = _end - _start;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, remaining);
            }

            _start = 0;
            _end = remaining;
        }

        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        var received = await _read(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        if (received == 0)
        {
            _endOfStream = true;
            return false;
        }

        _end += received;
        return true;
    }
}
