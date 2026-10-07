using System.Net.Sockets;

namespace WinForward.E2E.Target;

/// <summary>
/// How a fixed-length stream read ended. The DNS listener reads a two-byte length prefix and then that
/// many bytes, so it has to tell the two ends of a stream apart: a peer that stopped between messages
/// closed cleanly, while one that stopped inside either read cut a message in half (D19.3 C).
/// </summary>
internal enum ReadExactOutcome
{
    /// <summary>The whole buffer arrived.</summary>
    Complete = 0,

    /// <summary>The stream ended before any of the buffer arrived.</summary>
    EndOfStream = 1,

    /// <summary>The stream ended after part of the buffer arrived.</summary>
    Short = 2,
}

/// <summary>
/// The two stream operations the target's TCP servers share, so a short send and a short read are
/// handled in one place. Neither helper decides what a closed peer means: that is the caller's
/// protocol verdict, and it stays at the call site.
/// </summary>
internal static class SocketIo
{
    /// <summary>
    /// Sends every byte of <paramref name="data"/>, answering false when the peer stopped accepting
    /// them. A caller that treats that as a protocol error throws its own; a caller whose loop ends on
    /// the next read ignores the answer.
    /// </summary>
    internal static async ValueTask<bool> TrySendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        while (!data.IsEmpty)
        {
            var sent = await socket.SendAsync(data, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (sent <= 0)
            {
                return false;
            }

            data = data[sent..];
        }

        return true;
    }

    /// <summary>
    /// Reads exactly <c>buffer.Length</c> bytes, reporting where the stream ended when it did not
    /// carry them all.
    /// </summary>
    internal static async ValueTask<ReadExactOutcome> ReadExactAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var received = await socket.ReceiveAsync(buffer[offset..], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (received == 0)
            {
                return offset == 0 ? ReadExactOutcome.EndOfStream : ReadExactOutcome.Short;
            }

            offset += received;
        }

        return ReadExactOutcome.Complete;
    }
}
