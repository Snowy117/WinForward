using System.Net.Sockets;
using WinForward.E2E.Cli;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;
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

        var tracker = new UdpReliabilityTracker();
        var startTicks = Clock.Now;
        var deadlineTicks = context.DeadlineTicks(startTicks);
        using var linked = context.CreateLinkedTokenSource();
        var cancellationToken = linked.Token;
        var observationEndTicks = await DedicatedThread
            .RunOnOwnThreadAsync(() => RunUdpPhaseAsync(context, tracker, rate, payloadBytes, window, windowTicks, startTicks, deadlineTicks, cancellationToken))
            .ConfigureAwait(false);

        var counts = tracker.Classify(windowTicks, observationEndTicks);
        var clientSendLoss = ClientSendLoss(tracker, counts);
        var outcome = new ArmOutcome
        {
            Parameters = new ArmParameters
            {
                Seconds = spec.Seconds,
                RatePerSecond = rate,
                PayloadBytes = payloadBytes,
                LossWindowMs = windowMs,
            },
            Metrics = BuildMetrics(tracker, counts, windowMs, clientSendLoss, Clock.Now - startTicks),
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = clientSendLoss,
                [ArmKeys.Common.Gates.WindowMs] = windowMs,
            },
        };
        outcome.Notes.Add("W is the plan's lossWindowMs, 200 ms when the plan does not declare one: it is declared and published as metrics.window rather than derived, so every row's arrived/late/never split is reproducible from the record alone (RFC 2680 style, a datagram is lost if it has not arrived W after its intended send instant).");
        outcome.Notes.Add("a slot leaves the in-flight window W after its intended send instant even without an arrival, so a datagram the path drops cannot wedge the offer loop and stop the client sending.");
        outcome.Notes.Add("every sent datagram is classified exactly once, so arrived + late + never + abandonedAtTeardown + corruptDatagrams == sent by construction; corruptDatagrams counts the sent datagrams booked corrupt, while corrupt counts corrupt arrivals and so also counts replay of a frame already booked corrupt.");
        outcome.Notes.Add("the drain runs until W after the last real send, so no datagram is called lost before its whole W has passed; a drain cut short leaves the datagrams still inside their window in abandonedAtTeardown rather than in never, and they are excluded from lossRate.");
        outcome.Notes.Add("reordered counts arrivals that follow the arrival of a higher sequence, the RFC 4737 definition; arriving after a higher sequence was merely sent does not count.");
        outcome.Notes.Add("corrupt frames whose header decoded are removed from the never bucket; the strict-loss rate adds corrupt back on top of loss, as RFC 2680 requires.");
        outcome.Notes.Add("a reply carrying another flow's connection id validates against that flow's own filler, so it is rejected by connection id before the filler check and booked as foreignConnection; a reply for a sequence this socket never sent is booked as unmatchedReplies.");
        outcome.Notes.Add("sendWouldBlock counts sends the kernel did not accept synchronously; a send that failed synchronously is counted in sendFailures only, because it completes rather than blocks and never enters the sent population.");
        outcome.Notes.Add("outOfRangeSequences counts sequences a received datagram named that the tracker refused as outside its bounded sequence space, and sentOutOfRangeSequences counts offered slots the tracker refused to send for the same reason; the refused slot reached no socket, so it is counted as client send loss and lands in no classification bucket, and a non-zero value of either means this record covers fewer datagrams than it claims to have measured.");
        outcome.Notes.Add("the offer phase and the drain phase share one socket, so a reply is never delivered to a socket nobody reads.");
        outcome.Notes.Add("a rate or ratio whose denominator is zero is written as null rather than 0: nothing was sent, so there is no rate to report.");
        return outcome;
    }

    /// <summary>
    /// The arm's client send loss: every datagram the client destroyed itself instead of handing it to
    /// the socket. The four terms are the four ways that happens -- a send that threw, a slot the
    /// in-flight window refused, a datagram still inside its window when the drain was cut short, and a
    /// slot the tracker refused as outside its bounded sequence space (D2/D7). A datagram the path
    /// dropped is in none of them, so this value and <c>lossRate</c> count disjoint populations.
    /// </summary>
    internal static long ClientSendLoss(UdpReliabilityTracker tracker, in LossCounts counts) =>
        tracker.SendFailure + tracker.WindowOverflow + counts.Undetermined + tracker.SentOutOfRange;

    /// <summary>
    /// The record a finished run publishes: every counter the tracker and the classification ended
    /// with, and the rates derived from them in one place.
    /// </summary>
    private static LossMetrics BuildMetrics(
        UdpReliabilityTracker tracker,
        LossCounts counts,
        int windowMs,
        long clientSendLoss,
        long elapsedTicks)
    {
        var loss = counts.Late + counts.Never;
        return new LossMetrics
        {
            Sent = tracker.SentOk,
            Supplied = tracker.Supplied,
            Arrived = counts.Arrived,
            Late = counts.Late,
            Never = counts.Never,
            Corrupt = counts.Corrupt,
            CorruptDatagrams = counts.CorruptDatagrams,
            Duplicate = counts.Duplicate,
            Reordered = counts.Reordered,
            UnmatchedReplies = tracker.UnmatchedReplies,
            ForeignConnection = tracker.ForeignConnection,
            ReceivedDatagrams = tracker.ReceivedDatagrams,
            ReceivedBytes = tracker.ReceivedBytes,
            ClientSendLoss = clientSendLoss,
            SendWouldBlock = tracker.SendWouldBlock,
            SendFailures = tracker.SendFailure,
            WindowOverflow = tracker.WindowOverflow,
            AbandonedAtTeardown = counts.Undetermined,
            Window = windowMs,
            LossRate = JsonRate.Rate(loss, tracker.SentOk),
            StrictLossRate = JsonRate.Rate(loss + counts.Corrupt, tracker.SentOk),
            LateRate = JsonRate.Rate(counts.Late, tracker.SentOk),
            CorruptRate = JsonRate.Rate(counts.Corrupt, tracker.SentOk),
            DuplicateRate = JsonRate.Rate(counts.Duplicate, tracker.SentOk),
            ReorderRate = JsonRate.Rate(counts.Reordered, tracker.SentOk),
            ClientSendLossRate = JsonRate.Rate(clientSendLoss, tracker.Supplied),
            OutOfRangeSequences = tracker.OutOfRange,
            SentOutOfRangeSequences = tracker.SentOutOfRange,
            AchievedRate = JsonPerSecond.PerSecond(tracker.SentOk, elapsedTicks, System.Diagnostics.Stopwatch.Frequency),
        };
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
        if (!(await SocketOps.TryConnectAsync(socket, context.UdpEndPoint, cancellationToken).ConfigureAwait(false)).Ok)
        {
            // The lane never reached its target, so none of the offered schedule was handed to a
            // socket: booked as client send loss -- the client itself destroyed the whole run -- and
            // the arm publishes an empty observation instead of failing the record.
            tracker.MarkSendFailure();
            return Clock.Now;
        }

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
                if (!LossWindow.Admit(tracker, Clock.Now, windowTicks, window))
                {
                    continue;
                }

                lastSendTicks = await SendDatagramAsync(context, tracker, socket, frame, payloadBytes, index, intended, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Hands one datagram to the socket and answers the instant the send started, which is the horizon
    /// the observation is extended from; a send the kernel did not accept synchronously is still a send
    /// that completes, so it is reported and not counted as loss.
    /// </summary>
    private static async ValueTask<long> SendDatagramAsync(
        ArmContext context,
        UdpReliabilityTracker tracker,
        Socket socket,
        FrameBuffer frame,
        int payloadBytes,
        long index,
        long intended,
        CancellationToken cancellationToken)
    {
        var length = frame.Build(ConnectionId, (ulong)index, intended);
        length = ApplyFaultInjection(context.Options, frame, payloadBytes, index, length);
        var sendTicks = Clock.Now;
        var send = socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken);
        if (!send.IsCompleted)
        {
            tracker.MarkWouldBlock();
        }

        await send.ConfigureAwait(false);
        return sendTicks;
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

            // The three udp arms share one classifier and each keeps its own book (D18.3): what the
            // bytes are comes from the classifier, whether this socket ever sent that sequence comes
            // from the tracker, and the WasSent step folds the two together.
            var verdict = ReplyClassifier.Classify(receiveBuffer.AsSpan(0, received), ConnectionId);
            if (verdict.Kind == ReplyKind.Arrived)
            {
                verdict = verdict.Resolve(tracker.WasSent(verdict.Sequence));
            }

            Book(tracker, verdict, now);
        }
    }

    /// <summary>Books one classified datagram in the tracker that owns this phase's accounting.</summary>
    private static void Book(UdpReliabilityTracker tracker, in ReplyVerdict verdict, long now)
    {
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
