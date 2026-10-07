using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

/// <summary>
/// The dns arm's udp half: a connected socket, a pending table keyed by transaction id, and the drain
/// that follows the send phase. The id key is what makes the pairing exact — a response whose id is not
/// outstanding can only be unmatched, never an answer for some other query.
/// </summary>
internal static class DnsUdpPhase
{
    private const int UdpWindow = 256;
    private const int UdpIdStride = 2;

    internal static async Task RunUdpAsync(
        ArmContext context,
        DnsCounters counters,
        IPEndPoint dnsEndPoint,
        double rate,
        int cnameEvery,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        using var socket = context.CreateUdpSocket();
        if (!(await SocketOps.TryConnectAsync(socket, dnsEndPoint, cancellationToken).ConfigureAwait(false)).Ok)
        {
            // The udp phase never reached the resolver, so none of its share of the schedule was handed
            // to a socket: booked as a socket error and the arm publishes the tcp phase's measurement
            // instead of failing the record.
            counters._socketErrors++;
            return;
        }

        var pending = new ConcurrentDictionary<ushort, long>();
        var receiveBuffer = new byte[4096];
        using var laneCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = ReceiveUdpLoopAsync(context, socket, pending, counters, receiveBuffer, laneCancellation.Token);

        try
        {
            await SendUdpPhaseAsync(socket, pending, counters, rate, cnameEvery, startTicks, deadlineTicks, laneCancellation.Token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            counters._socketErrors++;
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }

        var until = Clock.Now + Clock.FromSeconds(DnsArm.DrainWindowMilliseconds / 1000.0);
        try
        {
            while (!pending.IsEmpty && Clock.Now < until)
            {
                await Task.Delay(1, laneCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }

        // Claiming each remaining query one at a time keeps the partition exact: a response that is still in
        // flight can only be counted as unmatched, never as an answer and a timeout for the same query.
        foreach (var transactionId in pending.Keys)
        {
            if (pending.TryRemove(transactionId, out _))
            {
                counters._timeout++;
            }
        }

        await laneCancellation.CancelAsync().ConfigureAwait(false);
        await receive.ConfigureAwait(false);
    }

    private static async Task SendUdpPhaseAsync(
        Socket socket,
        ConcurrentDictionary<ushort, long> pending,
        DnsCounters counters,
        double rate,
        int cnameEvery,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        var pacer = new Pacer(rate, startTicks);
        var sendBuffer = new byte[512];
        long index = 0;
        ushort transactionId = 0;

        while (Clock.Now < deadlineTicks)
        {
#pragma warning disable S6966, VSTHRD103, MA0042 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
            // ReSharper disable once MethodHasAsyncOverload // The pacing must block: WaitUntilAsync's Task.Delay resolves to the 15.6 ms Windows timer tick and cannot hold these instants.
            Pacer.WaitUntil(pacer.IntendedTicks(index), cancellationToken);
#pragma warning restore S6966, VSTHRD103, MA0042
            var intended = pacer.IntendedTicks(index);
            index++;

            if (pending.Count >= UdpWindow)
            {
                counters._unsent++;
                continue;
            }

            transactionId = (ushort)(transactionId + UdpIdStride);
            var length = DnsQueryBuilder.BuildQuery(sendBuffer, transactionId, index, cnameEvery);
            if (length <= 0)
            {
                counters._malformed++;
                continue;
            }

            await socket.SendAsync(sendBuffer.AsMemory(0, length), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            counters._sent++;
            counters._queryTypes[DnsQueryBuilder.QueryTypeFor(index, cnameEvery)]++;
            pending[transactionId] = intended;
        }
    }

    private static async Task ReceiveUdpLoopAsync(
        ArmContext context,
        Socket socket,
        ConcurrentDictionary<ushort, long> pending,
        DnsCounters counters,
        byte[] receiveBuffer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(receiveBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                var now = Clock.Now;
                var message = receiveBuffer.AsSpan(0, received);
                if (!DnsWire.TryParseResponse(message, out var responseId, out var rcode, out var answerCount))
                {
                    counters._malformed++;
                    continue;
                }

                // The pending table is keyed by transaction id, so a hit is the id check: a response carrying
                // an id that is not outstanding is rejected here and never reaches the answer counters.
                if (!pending.TryRemove(responseId, out var intended))
                {
                    counters._unmatched++;
                    continue;
                }

                context.Latency.DnsRtt.Record(Clock.ToNanoseconds(now - intended));
                DnsArm.Classify(counters, rcode, answerCount);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (SocketException)
        {
            counters._socketErrors++;
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
    }
}
