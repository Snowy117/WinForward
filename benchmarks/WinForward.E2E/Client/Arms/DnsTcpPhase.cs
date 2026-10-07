using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

/// <summary>
/// The dns arm's tcp half: length-prefixed queries on one connection, and the queue that pairs each
/// response with the query it was sent for. The queue order alone is not the pairing — a stream that
/// loses, duplicates or reorders a message would misattribute every answer after it — so the id
/// queued with each query is what decides which query a response is credited to.
/// </summary>
internal static class DnsTcpPhase
{
    private const int TcpWindow = 64;
    private const int TcpIdStride = 2;

    /// <summary>
    /// One query the TCP lane has written and not yet matched. The id is kept from the send because the
    /// stream position alone does not say which query an answer belongs to.
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PendingQuery(ushort Id, long Intended);

    internal static async Task RunTcpAsync(
        ArmContext context,
        DnsCounters counters,
        IPEndPoint dnsEndPoint,
        double rate,
        int cnameEvery,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        using var socket = context.CreateTcpSocket();
        if (!(await SocketOps.TryConnectAsync(socket, dnsEndPoint, cancellationToken).ConfigureAwait(false)).Ok)
        {
            counters._socketErrors++;
            return;
        }

        var pending = new ConcurrentQueue<PendingQuery>();
        var receiveBuffer = new byte[4096];

        try
        {
            var receive = ReceiveLoopAsync(context, socket, pending, counters, receiveBuffer, cancellationToken);
            await SendTcpPhaseAsync(socket, pending, counters, rate, cnameEvery, startTicks, deadlineTicks, cancellationToken).ConfigureAwait(false);

            // Claiming each remaining query one at a time keeps the partition exact: a response that is still
            // in flight can only be counted as unmatched, never as an answer and a timeout for the same query.
            while (pending.TryDequeue(out _))
            {
                counters._timeout++;
            }

            SocketOps.ShutdownQuietly(socket, SocketShutdown.Both);
            await receive.ConfigureAwait(false);
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
    }

    private static async Task SendTcpPhaseAsync(
        Socket socket,
        ConcurrentQueue<PendingQuery> pending,
        DnsCounters counters,
        double rate,
        int cnameEvery,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        var pacer = new Pacer(rate, startTicks);
        var sendBuffer = new byte[512];
        var lengthPrefix = new byte[2];
        long index = 0;
        ushort transactionId = 1;

        while (Clock.Now < deadlineTicks)
        {
#pragma warning disable S6966, VSTHRD103, MA0042 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
            // ReSharper disable once MethodHasAsyncOverload // The pacing must block: WaitUntilAsync's Task.Delay resolves to the 15.6 ms Windows timer tick and cannot hold these instants.
            Pacer.WaitUntil(pacer.IntendedTicks(index), cancellationToken);
#pragma warning restore S6966, VSTHRD103, MA0042
            var intended = pacer.IntendedTicks(index);
            index++;

            if (pending.Count >= TcpWindow)
            {
                counters._unsent++;
                continue;
            }

            transactionId = (ushort)(transactionId + TcpIdStride);
            if (!await SendTcpQueryAsync(socket, sendBuffer, lengthPrefix, transactionId, index, cnameEvery, cancellationToken).ConfigureAwait(false))
            {
                counters._malformed++;
                continue;
            }

            counters._sent++;
            counters._queryTypes[DnsQueryBuilder.QueryTypeFor(index, cnameEvery)]++;
            pending.Enqueue(new PendingQuery(transactionId, intended));
        }

        var drainUntil = Clock.Now + Clock.FromSeconds(DnsArm.DrainWindowMilliseconds / 1000.0);
        while (!pending.IsEmpty && Clock.Now < drainUntil)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<bool> SendTcpQueryAsync(
        Socket socket,
        byte[] sendBuffer,
        byte[] lengthPrefix,
        ushort transactionId,
        long index,
        int cnameEvery,
        CancellationToken cancellationToken)
    {
        var length = DnsQueryBuilder.BuildQuery(sendBuffer, transactionId, index, cnameEvery);
        if (length <= 0)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt16BigEndian(lengthPrefix, (ushort)length);
        await socket.SendAsync(lengthPrefix, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        await socket.SendAsync(sendBuffer.AsMemory(0, length), SocketFlags.None, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void HandleTcpResponse(
        ArmContext context,
        ConcurrentQueue<PendingQuery> pending,
        DnsCounters counters,
        long now,
        ReadOnlySpan<byte> message)
    {
        if (!DnsWire.TryParseResponse(message, out var responseId, out var rcode, out var answerCount))
        {
            counters._malformed++;
            return;
        }

        if (!pending.TryDequeue(out var query))
        {
            counters._unmatched++;
            return;
        }

        // An answer on this stream is only in the same position as its query while nothing is lost,
        // duplicated or reordered, so the id queued with the query is what decides the pairing. A mismatched
        // response still consumes the query it was dequeued against: that query was sent and got no answer of
        // its own, which is a failure to answer rather than silence.
        if (query.Id != responseId)
        {
            counters._unmatched++;
            counters._other++;
            return;
        }

        context.Latency.DnsRtt.Record(Clock.ToNanoseconds(now - query.Intended));
        DnsArm.Classify(counters, rcode, answerCount);
    }

    private static async Task ReceiveLoopAsync(
        ArmContext context,
        Socket socket,
        ConcurrentQueue<PendingQuery> pending,
        DnsCounters counters,
        byte[] receiveBuffer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadExactAsync(socket, receiveBuffer.AsMemory(0, 2), cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(receiveBuffer);
                if (length is 0 or > 4096)
                {
                    counters._malformed++;
                    return;
                }

                if (!await ReadExactAsync(socket, receiveBuffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                HandleTcpResponse(context, pending, counters, Clock.Now, receiveBuffer.AsSpan(0, length));
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

    private static async ValueTask<bool> ReadExactAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
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
