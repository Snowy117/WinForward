using System.Net.Sockets;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

/// <summary>
/// One desktop's UDP lane: a paced offer loop that hands datagrams to the socket, and a receive task
/// that books what comes back.
/// </summary>
/// <remarks>
/// The lane's threading contract is the MIX mirror of the latency arm's (D19.3 F): the same shape as
/// <c>ILanePolicy.Settle</c>, carried by <see cref="MixUdpBook"/>'s own types rather than by the lane
/// seam. The receive task classifies and enqueues only — the settlement carries the classifier's
/// <b>unresolved</b> verdict and the instant the datagram arrived — and the send side drains it at
/// the pacing point, after <c>Pacer.WaitUntil</c> and before the next send. <c>WasSent</c>, the
/// pending removal, the round-trip sample and every booking are the send side's, which is why the
/// receive task is handed the book and never the tracker.
/// </remarks>
internal static class MixUdpLoop
{
    internal const int UdpPayloadBytes = 120;

    internal static async Task UdpLoopAsync(
        ArmContext context,
        MixCounters counters,
        UdpReliabilityTracker tracker,
        long[] observationEnds,
        int desktopIndex,
        long startTicks,
        long deadlineTicks,
        long windowTicks,
        CancellationToken cancellationToken)
    {
        using var socket = context.CreateUdpSocket();
        var book = new MixUdpBook(tracker, context.Latency.UdpRtt);
        if (!await TryOpenAsync(context, socket, book, observationEnds, desktopIndex, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var frame = new FrameBuffer(UdpPayloadBytes);
        var receiveBuffer = new byte[FrameCodec.HeaderSize + UdpPayloadBytes + FrameCodec.TrailerSize + 64];
        var connectionId = 0x4D55_0000u + (uint)desktopIndex;
        using var laneCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = ReceiveUdpLoopAsync(socket, connectionId, book, receiveBuffer, laneCancellation.Token);
        var pacer = new Pacer(MixArm.UdpPacketsPerSecond, startTicks);
        // MaxValue until the send loop sets the horizon: a lane that dies before sending keeps
        // observing until the instant it dies, so nothing is called lost without its whole W.
        var drainUntilTicks = long.MaxValue;
        long index = 0;

        try
        {
            while (Clock.Now < deadlineTicks)
            {
                var intended = pacer.IntendedTicks(index);
#pragma warning disable S6966, VSTHRD103, MA0042 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
                // ReSharper disable once MethodHasAsyncOverload // The pacing must block: WaitUntilAsync's Task.Delay resolves to the 15.6 ms Windows timer tick and cannot hold these instants.
                Pacer.WaitUntil(intended, laneCancellation.Token);
#pragma warning restore S6966, VSTHRD103, MA0042
                // The lane's settlement point (D18.2 step 2): everything the receive task offered since
                // the last pace is booked here, on this side of the lane.
                book.Settle();
                index++;
                drainUntilTicks = await SendDatagramAsync(counters, book, socket, frame, connectionId, index, intended, windowTicks, laneCancellation.Token).ConfigureAwait(false);
            }

            await DrainPendingAsync(book, drainUntilTicks, laneCancellation.Token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            book.BookSendRefused(index);
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
        finally
        {
            // The instant observation stopped, capped by the horizon: Classify books a datagram still
            // inside its window at this instant as abandoned, so one datagram is never both client
            // loss and path loss.
            observationEnds[desktopIndex] = Math.Min(Clock.Now, drainUntilTicks);
        }

        await JoinAndSettleAsync(book, receive, laneCancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the lane: stop the receive task, join it, then settle what it had already offered. The
    /// final settlement is load-bearing — the replies that arrived between the offer loop's last pace
    /// and the socket's close are in the queue at this point, and dropping them would shrink the
    /// arrived population instead of cleaning anything up.
    /// </summary>
    private static async Task JoinAndSettleAsync(MixUdpBook book, Task receive, CancellationTokenSource laneCancellation)
    {
        await laneCancellation.CancelAsync().ConfigureAwait(false);
        await receive.ConfigureAwait(false);
        book.Settle();
    }

    /// <summary>
    /// Connects the lane's socket, booking a failure as client send loss and ending this desktop's
    /// observation where it stands: the lane offered nothing, so the record says the client destroyed
    /// its own share of the schedule rather than failing the whole arm (D18.4's connect ruling).
    /// </summary>
    private static async ValueTask<bool> TryOpenAsync(
        ArmContext context,
        Socket socket,
        MixUdpBook book,
        long[] observationEnds,
        int desktopIndex,
        CancellationToken cancellationToken)
    {
        if ((await SocketOps.TryConnectAsync(socket, context.UdpEndPoint, cancellationToken).ConfigureAwait(false)).Ok)
        {
            return true;
        }

        book.BookSendFailure();
        observationEnds[desktopIndex] = Clock.Now;
        return false;
    }

    /// <summary>
    /// Hands one datagram to the socket and answers the instant its observation horizon starts at. The
    /// datagram is registered before the socket call, so a concurrent receive task can never offer a
    /// reply for a sequence the socket has not sent; a refused send is un-booked in the caller's catch.
    /// </summary>
    private static async ValueTask<long> SendDatagramAsync(
        MixCounters counters,
        MixUdpBook book,
        Socket socket,
        FrameBuffer frame,
        uint connectionId,
        long index,
        long intended,
        long windowTicks,
        CancellationToken cancellationToken)
    {
        book.BookSupplied();
        book.BookSent(index, intended);
        var length = frame.Build(connectionId, (ulong)index, intended);
        await socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref counters._udpBytes, length);
        return Clock.Now + windowTicks;
    }

    /// <summary>
    /// Waits for the last reply to be booked, settling as it goes: the pending book only shrinks when
    /// the send side drains a settlement, so a drain that stopped reading its own queue would spin
    /// here for the whole horizon with the replies sitting next to it.
    /// </summary>
    private static async Task DrainPendingAsync(MixUdpBook book, long drainUntilTicks, CancellationToken cancellationToken)
    {
        while (book.Pending > 0 && Clock.Now < drainUntilTicks)
        {
            book.Settle();
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveUdpLoopAsync(
        Socket socket,
        uint connectionId,
        MixUdpBook book,
        byte[] receiveBuffer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(receiveBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                book.Offer(ReplyClassifier.Classify(receiveBuffer.AsSpan(0, received), connectionId), Clock.Now);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (SocketException)
        {
            book.OfferReceiveFailure();
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
    }
}
