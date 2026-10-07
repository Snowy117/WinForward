using System.Collections.Concurrent;
using System.Net.Sockets;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

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
        if (!await TryOpenAsync(context, socket, tracker, observationEnds, desktopIndex, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var frame = new FrameBuffer(UdpPayloadBytes);
        var receiveBuffer = new byte[FrameCodec.HeaderSize + UdpPayloadBytes + FrameCodec.TrailerSize + 64];
        var pending = new ConcurrentDictionary<ulong, long>();
        var connectionId = 0x4D55_0000u + (uint)desktopIndex;
        using var laneCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = ReceiveUdpLoopAsync(context, socket, connectionId, pending, tracker, receiveBuffer, laneCancellation.Token);
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
                index++;
                drainUntilTicks = await SendDatagramAsync(counters, tracker, socket, frame, pending, connectionId, index, intended, windowTicks, laneCancellation.Token).ConfigureAwait(false);
            }

            await DrainPendingAsync(pending, drainUntilTicks, laneCancellation.Token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            tracker.MarkSendRefused(index);
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

        await laneCancellation.CancelAsync().ConfigureAwait(false);
        await receive.ConfigureAwait(false);
    }

    /// <summary>
    /// Connects the lane's socket, booking a failure as client send loss and ending this desktop's
    /// observation where it stands: the lane offered nothing, so the record says the client destroyed
    /// its own share of the schedule rather than failing the whole arm (D18.4's connect ruling).
    /// </summary>
    private static async ValueTask<bool> TryOpenAsync(
        ArmContext context,
        Socket socket,
        UdpReliabilityTracker tracker,
        long[] observationEnds,
        int desktopIndex,
        CancellationToken cancellationToken)
    {
        if ((await SocketOps.TryConnectAsync(socket, context.UdpEndPoint, cancellationToken).ConfigureAwait(false)).Ok)
        {
            return true;
        }

        tracker.MarkSendFailure();
        observationEnds[desktopIndex] = Clock.Now;
        return false;
    }

    /// <summary>
    /// Hands one datagram to the socket and answers the instant its observation horizon starts at. The
    /// datagram is registered before the socket call, so a concurrent receive lane can never see a reply
    /// for a sequence the socket has not sent; a refused send is un-booked in the caller's catch.
    /// </summary>
    private static async ValueTask<long> SendDatagramAsync(
        MixCounters counters,
        UdpReliabilityTracker tracker,
        Socket socket,
        FrameBuffer frame,
        ConcurrentDictionary<ulong, long> pending,
        uint connectionId,
        long index,
        long intended,
        long windowTicks,
        CancellationToken cancellationToken)
    {
        tracker.MarkSupplied();
        tracker.MarkSent(index, intended);
        pending[(ulong)index] = intended;
        var length = frame.Build(connectionId, (ulong)index, intended);
        await socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref counters._udpBytes, length);
        return Clock.Now + windowTicks;
    }

    private static async Task DrainPendingAsync(ConcurrentDictionary<ulong, long> pending, long drainUntilTicks, CancellationToken cancellationToken)
    {
        while (!pending.IsEmpty && Clock.Now < drainUntilTicks)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveUdpLoopAsync(
        ArmContext context,
        Socket socket,
        uint connectionId,
        ConcurrentDictionary<ulong, long> pending,
        UdpReliabilityTracker tracker,
        byte[] receiveBuffer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(receiveBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                BookDatagram(context, tracker, pending, connectionId, receiveBuffer.AsSpan(0, received), Clock.Now);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (SocketException)
        {
            tracker.MarkSendFailure();
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
    }

    /// <summary>
    /// Books one datagram: the shared classifier answers what it is, this arm's tracker answers whether
    /// the sequence was ever sent, and the arm's own window book adds the round trip when the reply
    /// consumed a pending request (D18.3).
    /// </summary>
    private static void BookDatagram(
        ArmContext context,
        UdpReliabilityTracker tracker,
        ConcurrentDictionary<ulong, long> pending,
        uint connectionId,
        ReadOnlySpan<byte> datagram,
        long now)
    {
        var verdict = ReplyClassifier.Classify(datagram, connectionId);
        if (verdict.Kind == ReplyKind.Arrived)
        {
            verdict = verdict.Resolve(tracker.WasSent(verdict.Sequence));
            if (verdict.Kind == ReplyKind.Arrived && pending.TryRemove((ulong)verdict.Sequence, out var intended))
            {
                context.Latency.UdpRtt.Record(Clock.ToNanoseconds(now - intended));
            }
        }

        switch (verdict.Kind)
        {
            case ReplyKind.Arrived:
                tracker.MarkArrival(verdict.Sequence, now, verdict.PayloadBytes);
                break;

            case ReplyKind.Corrupt:
            case ReplyKind.CorruptKnownSequence:
                tracker.MarkCorruptWithKnownSequence(verdict.Sequence);
                break;

            case ReplyKind.ForeignConnection:
                tracker.MarkForeignConnection();
                break;

            case ReplyKind.Unmatched:
                tracker.MarkUnmatchedReply();
                break;

            case ReplyKind.Undecodable:
                tracker.MarkCorrupt();
                break;

            default:
                // Every verdict the classifier can produce is named above, so a future one has to be
                // wired here rather than booked by whichever arm happened to be last.
                throw new InvalidOperationException($"Unhandled reply kind '{verdict.Kind}'.");
        }
    }
}
