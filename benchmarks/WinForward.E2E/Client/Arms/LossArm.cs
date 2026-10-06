using System.Net.Sockets;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal static class LossArm
{
    private const int DefaultRatePerSecond = 500;
    private const int DefaultPayloadBytes = 200;
    private const int DefaultWindow = 4096;
    private const uint ConnectionId = 0x1055_0001u;

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var spec = context.Spec;
        var rate = spec.RatePerSecond > 0 ? spec.RatePerSecond : DefaultRatePerSecond;
        var payloadBytes = spec.PayloadBytes > 0 ? spec.PayloadBytes : DefaultPayloadBytes;
        var window = spec.Window > 0 ? spec.Window : DefaultWindow;
        var windowMs = spec.LossWindowMs > 0 ? spec.LossWindowMs : UdpLossMath.DefaultWindowMilliseconds;
        var windowTicks = UdpLossMath.WindowTicks(windowMs);

        var metrics = new DictionaryMetrics();
        var outcome = new ArmOutcome
        {
            Parameters =
            {
                ["seconds"] = spec.Seconds,
                ["ratePerSecond"] = rate,
                ["payloadBytes"] = payloadBytes,
                ["lossWindowMs"] = windowMs,
            },
            Metrics = metrics,
        };

        var tracker = new UdpReliabilityTracker();
        var startTicks = Clock.Now;
        var deadlineTicks = context.DeadlineTicks(startTicks);
        using var linked = context.CreateLinkedTokenSource();
        var cancellationToken = linked.Token;
        var observationEndTicks = await Dedicated
            .RunOnOwnThreadAsync(() => RunUdpPhaseAsync(context, tracker, rate, payloadBytes, window, windowTicks, startTicks, deadlineTicks, cancellationToken))
            .ConfigureAwait(false);

        var counts = tracker.Classify(windowTicks, observationEndTicks);
        var clientSendLoss = tracker.SendFailure + tracker.WindowOverflow + counts.Undetermined;
        WriteMetrics(metrics, tracker, counts, windowMs, clientSendLoss, Clock.Now - startTicks);
        outcome.Gates["clientSendLoss"] = clientSendLoss;
        outcome.Gates["windowMs"] = (double)windowMs;
        outcome.Notes.Add("W is the plan's lossWindowMs, 200 ms when the plan does not declare one: it is declared and published as metrics.window rather than derived, so every row's arrived/late/never split is reproducible from the record alone (RFC 2680 style, a datagram is lost if it has not arrived W after its intended send instant).");
        outcome.Notes.Add("a slot leaves the in-flight window W after its intended send instant even without an arrival, so a datagram the path drops cannot wedge the offer loop and stop the client sending.");
        outcome.Notes.Add("every sent datagram is classified exactly once, so arrived + late + never + abandonedAtTeardown + corruptDatagrams == sent by construction; corruptDatagrams counts the sent datagrams booked corrupt, while corrupt counts corrupt arrivals and so also counts replay of a frame already booked corrupt.");
        outcome.Notes.Add("the drain runs until W after the last real send, so no datagram is called lost before its whole W has passed; a drain cut short leaves the datagrams still inside their window in abandonedAtTeardown rather than in never, and they are excluded from lossRate.");
        outcome.Notes.Add("reordered counts arrivals that follow the arrival of a higher sequence, the RFC 4737 definition; arriving after a higher sequence was merely sent does not count.");
        outcome.Notes.Add("corrupt frames whose header decoded are removed from the never bucket; the strict-loss rate adds corrupt back on top of loss, as RFC 2680 requires.");
        outcome.Notes.Add("a reply carrying another flow's connection id validates against that flow's own filler, so it is rejected by connection id before the filler check and booked as foreignConnection; a reply for a sequence this socket never sent is booked as unmatchedReplies.");
        outcome.Notes.Add("sendWouldBlock counts sends the kernel did not accept synchronously; a send that failed synchronously is counted in sendFailures only, because it completes rather than blocks and never enters the sent population.");
        outcome.Notes.Add("outOfRangeSequences counts sequences the tracker refused as outside its bounded sequence space; a non-zero value means part of the offered schedule was never tracked, so this record's classification covers fewer datagrams than sent and must not be read as a complete loss measurement.");
        outcome.Notes.Add("the offer phase and the drain phase share one socket, so a reply is never delivered to a socket nobody reads.");
        outcome.Notes.Add("a rate or ratio whose denominator is zero is written as null rather than 0: nothing was sent, so there is no rate to report.");
        return outcome;
    }

    private static void WriteMetrics(
        DictionaryMetrics metrics,
        UdpReliabilityTracker tracker,
        LossCounts counts,
        int windowMs,
        long clientSendLoss,
        long elapsedTicks)
    {
        var loss = counts.Late + counts.Never;
        metrics["sent"] = tracker.SentOk;
        metrics["supplied"] = tracker.Supplied;
        metrics["arrived"] = counts.Arrived;
        metrics["late"] = counts.Late;
        metrics["never"] = counts.Never;
        metrics["corrupt"] = counts.Corrupt;
        metrics["corruptDatagrams"] = counts.CorruptDatagrams;
        metrics["duplicate"] = counts.Duplicate;
        metrics["reordered"] = counts.Reordered;
        metrics["unmatchedReplies"] = tracker.UnmatchedReplies;
        metrics["foreignConnection"] = tracker.ForeignConnection;
        metrics["receivedDatagrams"] = tracker.ReceivedDatagrams;
        metrics["receivedBytes"] = tracker.ReceivedBytes;
        metrics["clientSendLoss"] = clientSendLoss;
        metrics["sendWouldBlock"] = tracker.SendWouldBlock;
        metrics["sendFailures"] = tracker.SendFailure;
        metrics["windowOverflow"] = tracker.WindowOverflow;
        metrics["abandonedAtTeardown"] = counts.Undetermined;
        metrics["window"] = (double)windowMs;
        metrics["lossRate"] = JsonRate.Rate(loss, tracker.SentOk);
        metrics["strictLossRate"] = JsonRate.Rate(loss + counts.Corrupt, tracker.SentOk);
        metrics["lateRate"] = JsonRate.Rate(counts.Late, tracker.SentOk);
        metrics["corruptRate"] = JsonRate.Rate(counts.Corrupt, tracker.SentOk);
        metrics["duplicateRate"] = JsonRate.Rate(counts.Duplicate, tracker.SentOk);
        metrics["reorderRate"] = JsonRate.Rate(counts.Reordered, tracker.SentOk);
        metrics["clientSendLossRate"] = JsonRate.Rate(clientSendLoss, tracker.Supplied);
        metrics["outOfRangeSequences"] = tracker.OutOfRange;
        metrics["achievedRate"] = JsonPerSecond.PerSecond(tracker.SentOk, elapsedTicks, System.Diagnostics.Stopwatch.Frequency);
    }

    private static int ApplyFaultInjection(ClientOptions options, FrameBuffer frame, int payloadBytes, long index, int length)
    {
        if (options.InjectCorruptEvery > 0 && index % options.InjectCorruptEvery == 0)
        {
            frame.FlipPayloadByte(payloadBytes, 0);
            return length;
        }

        if (options.InjectRewriteEvery > 0 && index % options.InjectRewriteEvery == 0)
        {
            frame.FlipPayloadByte(payloadBytes, 0);
            return frame.RecomputeChecksum(payloadBytes);
        }

        return length;
    }

    private static async Task<long> RunUdpPhaseAsync(
        ArmContext context,
        UdpReliabilityTracker tracker,
        int rate,
        int payloadBytes,
        int window,
        long windowTicks,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        using var socket = context.CreateUdpSocket();
        await socket.ConnectAsync(context.UdpEndPoint, cancellationToken).ConfigureAwait(false);
        var frame = new FrameBuffer(payloadBytes);
        var receiveBuffer = new byte[FrameCodec.HeaderSize + payloadBytes + FrameCodec.TrailerSize + 64];
        var pacer = new Pacer(rate, startTicks);
        var lastSendTicks = startTicks;
        long index = 0;

        try
        {
            while (Clock.Now < deadlineTicks)
            {
                await DrainAsync(socket, receiveBuffer, tracker, cancellationToken).ConfigureAwait(false);
                var intended = pacer.IntendedTicks(index);
#pragma warning disable S6966, VSTHRD103, MA0042 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
                // ReSharper disable once MethodHasAsyncOverload // The pacing must block: WaitUntilAsync's Task.Delay resolves to the 15.6 ms Windows timer tick and cannot hold these instants.
                Pacer.WaitUntil(intended, cancellationToken);
#pragma warning restore S6966, VSTHRD103, MA0042
                index++;
                tracker.MarkSupplied();
                tracker.Retire(Clock.Now, windowTicks);

                if (tracker.Outstanding >= window)
                {
                    tracker.MarkWindowOverflow();
                    continue;
                }

                var length = frame.Build(ConnectionId, (ulong)index, intended);
                length = ApplyFaultInjection(context.Options, frame, payloadBytes, index, length);
                lastSendTicks = Clock.Now;
                var send = socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken);
                if (!send.IsCompleted)
                {
                    tracker.MarkWouldBlock();
                }

                await send.ConfigureAwait(false);
                tracker.MarkSent(index, intended);
            }
        }
        catch (SocketException)
        {
            tracker.MarkSendFailure();
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }

        // The horizon starts at the last real send rather than at the last intended one, so a client
        // stall can only widen the observation and never cut a datagram's own W short.
        var horizonTicks = lastSendTicks + windowTicks;
        await DrainUntilAsync(socket, receiveBuffer, tracker, horizonTicks, cancellationToken).ConfigureAwait(false);
        return Math.Min(Clock.Now, horizonTicks);
    }

    private static async Task DrainUntilAsync(
        Socket socket,
        byte[] receiveBuffer,
        UdpReliabilityTracker tracker,
        long classifyAtTicks,
        CancellationToken cancellationToken)
    {
        try
        {
            while (Clock.Now < classifyAtTicks)
            {
                await DrainAsync(socket, receiveBuffer, tracker, cancellationToken).ConfigureAwait(false);
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
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

    private static async ValueTask DrainAsync(
        Socket socket,
        byte[] receiveBuffer,
        UdpReliabilityTracker tracker,
        CancellationToken cancellationToken)
    {
        while (socket.Available > 0)
        {
            var received = await socket.ReceiveAsync(receiveBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            var now = Clock.Now;
            var datagram = receiveBuffer.AsSpan(0, received);

            if (!FrameCodec.TryDecode(datagram, out var header, out var payload, out var error))
            {
                if (error == FrameDecodeError.BadChecksum && FrameCodec.TryReadHeader(datagram, out var partial, out _))
                {
                    tracker.MarkCorruptWithKnownSequence((long)partial.Sequence);
                }
                else
                {
                    tracker.MarkCorrupt();
                }

                continue;
            }

            if (header.ConnectionId != ConnectionId)
            {
                tracker.MarkForeignConnection();
                continue;
            }

            if (!Filler.Matches(header.ConnectionId, header.Sequence, payload))
            {
                tracker.MarkCorruptWithKnownSequence((long)header.Sequence);
                continue;
            }

            if (!tracker.WasSent((long)header.Sequence))
            {
                tracker.MarkUnmatchedReply();
                continue;
            }

            tracker.MarkArrival((long)header.Sequence, now, payload.Length);
        }
    }
}
