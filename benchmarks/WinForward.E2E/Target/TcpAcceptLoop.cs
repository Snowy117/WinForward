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
    private readonly Func<Socket, CancellationToken, ValueTask<Socket>> _accept;
    private readonly List<Task> _connections = [];
    private long _acceptErrors;

    internal TcpAcceptLoop(Socket listener)
        : this(listener, static (socket, cancellationToken) => socket.AcceptAsync(cancellationToken))
    {
    }

    /// <summary>
    /// The loop over the caller's accept call. Whether an accept is refused is the kernel's decision,
    /// so the only way to pin what the loop does with a refusal is to script the call that produces it:
    /// the test that drives <see cref="AcceptErrors"/> refuses the first calls and then hands the loop
    /// the real one, which is also what shows that a refusal does not end its service.
    /// </summary>
    internal TcpAcceptLoop(Socket listener, Func<Socket, CancellationToken, ValueTask<Socket>> accept)
    {
        _listener = listener;
        _accept = accept;
    }

    /// <summary>
    /// The accepts this listener refused. A refused accept is retried rather than ending the loop, so
    /// without the count a listener that can no longer accept anything -- a descriptor table that is
    /// full, say -- would look exactly like an idle one.
    /// </summary>
    internal long AcceptErrors => Interlocked.Read(ref _acceptErrors);

    /// <summary>
    /// Accepts until <paramref name="cancellationToken"/> ends, handing each connection to
    /// <paramref name="handle"/>. An accept that failed on a live listener is counted and retried; one
    /// that failed because the listener is going away ends the loop.
    /// </summary>
    internal async Task RunAsync(Func<Socket, Task> handle, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _accept(_listener, cancellationToken).ConfigureAwait(false);
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
                Interlocked.Increment(ref _acceptErrors);
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
