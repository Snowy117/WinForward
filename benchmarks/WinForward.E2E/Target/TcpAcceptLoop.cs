using System.Net.Sockets;

namespace WinForward.E2E.Target;

/// <summary>
/// The accept loop the target's two TCP listeners share: one table of live connection tasks, pruned
/// once the table grows, and the drain that joins them. The listener is owned here; when the
/// connections are stopped is the caller's policy, which is why the drain is its own step.
/// </summary>
internal sealed class TcpAcceptLoop : IDisposable
{
    /// <summary>The table size at which completed connections are pruned from it.</summary>
    private const int PruneThreshold = 256;

    private readonly Socket _listener;
    private readonly List<Task> _connections = [];

    internal TcpAcceptLoop(Socket listener)
    {
        _listener = listener;
    }

    /// <summary>
    /// Accepts until <paramref name="cancellationToken"/> ends, handing each connection to
    /// <paramref name="handle"/>. An accept that failed on a live listener is retried; one that failed
    /// because the listener is going away ends the loop.
    /// </summary>
    internal async Task RunAsync(Func<Socket, Task> handle, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            socket.NoDelay = true;
            _connections.Add(handle(socket));
            if (_connections.Count >= PruneThreshold)
            {
                _connections.RemoveAll(static task => task.IsCompleted);
            }
        }
    }

    /// <summary>Joins every connection accepted so far.</summary>
    internal async Task DrainAsync()
    {
        await Task.WhenAll(_connections).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }
}
