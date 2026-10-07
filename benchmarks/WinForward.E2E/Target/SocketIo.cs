using System.Net.Sockets;

namespace WinForward.E2E.Target;

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

    /// <summary>Reads exactly <c>buffer.Length</c> bytes, answering false at the end of the stream.</summary>
    internal static async ValueTask<bool> ReadExactAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var received = await socket.ReceiveAsync(buffer[offset..], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (received == 0)
            {
                return false;
            }

            offset += received;
        }

        return true;
    }
}
