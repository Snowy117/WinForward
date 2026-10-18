using System.Net;
using System.Net.Sockets;

namespace WinForward.E2E.Target;

/// <summary>
/// One stream connection's response writes, each delayed from the instant it was queued and kept in
/// queue order. Reads are never blocked by the delay: a frame read while an earlier response waits
/// joins the queue, so a 1 s injection costs a connection 1 s of response latency instead of
/// serialising its request stream behind that delay. The delay sets a timer before it waits for the
/// earlier write, so a steady request stream is delayed by exactly the injection rather than by the
/// queue in front of it.
/// </summary>
internal sealed class DelayedStreamWrites
{
    private readonly Socket _socket;
    private readonly TimeSpan _delay;
    private readonly CancellationToken _cancellationToken;
    private Task _tail = Task.CompletedTask;

    internal DelayedStreamWrites(Socket socket, TimeSpan delay, CancellationToken cancellationToken)
    {
        _socket = socket;
        _delay = delay;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Queues one response and returns the write that carries it. The caller awaits the returned task
    /// when an outcome may be affected by the write, and <see cref="ObserveAsync"/> covers an exit
    /// that a read failure already decided.
    /// </summary>
    internal Task EnqueueAsync(ReadOnlyMemory<byte> data)
    {
        // The frame reader reuses its buffer on the next read, so the delay cannot hold its slice.
        var copy = data.ToArray();
        var previous = _tail;
        _tail = SendAfterPreviousAsync(previous, copy);
        return _tail;
    }

    /// <summary>
    /// Waits for the queue to drain and surfaces a write failure. Every normal return from a protocol
    /// loop awaits this before closing the socket, so a response the peer never received cannot be
    /// reported as a clean echo.
    /// </summary>
    internal async ValueTask CompleteAsync()
    {
        await _tail.ConfigureAwait(false);
    }

    /// <summary>
    /// Observes the queue on an exit whose outcome is already decided, so a write faulted by the same
    /// teardown that ended the read loop does not become an unobserved task exception.
    /// </summary>
    internal async ValueTask ObserveAsync()
    {
        try
        {
            await _tail.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* the connection's own shutdown cancelled the timer */
        }
        catch (ObjectDisposedException)
        {
            /* the teardown that ended the read loop closed the socket first */
        }
        catch (SocketException)
        {
            /* the peer the read loop already lost is the same peer this write lost */
        }
        catch (IOException)
        {
            /* the peer closed under the queued write; the read path already booked the outcome */
        }
    }

    private async Task SendAfterPreviousAsync(Task previous, byte[] data)
    {
        var delayed = Task.Delay(_delay, _cancellationToken);
        await delayed.ConfigureAwait(false);
        await previous.ConfigureAwait(false);
        if (!await SocketIO.TrySendAllAsync(_socket, data, _cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The peer closed while a delayed response was being sent.");
        }
    }
}

/// <summary>
/// One datagram listener's delayed responses. Each datagram gets its own timer and send task, so a
/// delayed reply does not stop the receive loop from reading the next datagram; the tasks are drained
/// before the listener's summary is written. Only created when the target is started with a nonzero
/// injection: a zero injection keeps the direct send path and allocates nothing.
/// </summary>
internal sealed class DelayedDatagrams
{
    private readonly Socket _socket;
    private readonly TimeSpan _delay;
    private readonly List<Task> _pending = [];
    private readonly Lock _gate = new();

    internal DelayedDatagrams(Socket socket, TimeSpan delay)
    {
        _socket = socket;
        _delay = delay;
    }

    internal void Send(ReadOnlyMemory<byte> datagram, EndPoint remoteEndPoint, Action onSocketError, CancellationToken cancellationToken)
    {
        // The receive loop reuses its datagram buffer on the next read, so the delay owns its copy.
        var copy = datagram.ToArray();
        var send = SendAfterDelayAsync(copy, remoteEndPoint, onSocketError, cancellationToken);
        lock (_gate)
        {
            _pending.Add(send);
        }
    }

    /// <summary>
    /// Waits for every queued send. The listener's cancellation token cancels the timers first, so a
    /// shutdown does not hold the summary open for the injection delay.
    /// </summary>
    internal async ValueTask DrainAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _pending];
            _pending.Clear();
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private async Task SendAfterDelayAsync(byte[] datagram, EndPoint remoteEndPoint, Action onSocketError, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            await _socket.SendToAsync(datagram, SocketFlags.None, remoteEndPoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* the listener's shutdown cancelled the pending response */
        }
        catch (ObjectDisposedException)
        {
            /* the listener's teardown closed the socket first */
        }
        catch (SocketException)
        {
            onSocketError();
        }
        catch (Exception exception)
        {
            await TargetLog.ReportAsync($"e2e target: a delayed datagram could not be sent: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
        }
    }
}
