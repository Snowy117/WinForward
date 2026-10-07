using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal sealed class DnsCounters
{
    internal long _sent;
    internal long _answered;
    internal long _servfail;
    internal long _timeout;
    internal long _other;
    internal long _unsent;
    internal long _socketErrors;
    internal long _emptyAnswers;
    internal long _malformed;
    internal long _unmatched;
    internal readonly long[] _rcodes = new long[16];

    /// <summary>
    /// Queries written to the socket, per DNS query type. Counted from the query the arm actually
    /// built, so the published mix stays right for a plan that interleaves CNAME queries.
    /// </summary>
    internal readonly long[] _queryTypes = new long[128];
}

internal static class DnsArm
{
    private const int DefaultRatePerSecond = 200;
    private const int DrainWindowMilliseconds = 1000;
    private const int UdpWindow = 256;
    private const int TcpWindow = 64;
    private const int TcpIdStride = 2;
    private const int UdpIdStride = 2;

    /// <summary>
    /// One query the TCP lane has written and not yet matched. The id is kept from the send because the
    /// stream position alone does not say which query an answer belongs to.
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PendingQuery(ushort Id, long Intended);

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var spec = context.Spec;
        var rate = spec.RatePerSecond > 0 ? spec.RatePerSecond : DefaultRatePerSecond;
        var tcpPercent = Math.Clamp(spec.TcpPercent, 0, 100);
        var cnameEvery = Math.Max(0, spec.CnameEvery);
        var tcpRate = rate * tcpPercent / 100.0;
        var udpRate = rate - tcpRate;
        var dnsPort = spec.DnsPort > 0 ? spec.DnsPort : context.Options.DnsPort;
        var dnsEndPoint = new IPEndPoint(context.TargetAddress, dnsPort);

        var startTicks = Clock.Now;
        var deadlineTicks = context.DeadlineTicks(startTicks);
        using var linked = context.CreateLinkedTokenSource();
        var cancellationToken = linked.Token;
        var udp = new DnsCounters();
        var tcp = new DnsCounters();
        var tasks = new List<Task>();

        if (udpRate > 0)
        {
            tasks.Add(Dedicated.RunOnOwnThreadAsync(() => RunUdpAsync(context, udp, dnsEndPoint, udpRate, cnameEvery, startTicks, deadlineTicks, cancellationToken)));
        }

        if (tcpRate > 0)
        {
            tasks.Add(Dedicated.RunOnOwnThreadAsync(() => RunTcpAsync(context, tcp, dnsEndPoint, tcpRate, cnameEvery, startTicks, deadlineTicks, cancellationToken)));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // The metrics value is built after both halves joined, so every counter it carries is the
        // total the run ended with rather than a snapshot taken while a lane was still counting.
        var outcome = new ArmOutcome
        {
            Parameters = new ArmParameters
            {
                Seconds = spec.Seconds,
                RatePerSecond = rate,
                TcpPercent = tcpPercent,
                CnameEvery = cnameEvery,
                DnsPort = dnsPort,
                DrainWindowMs = DrainWindowMilliseconds,
            },
            Metrics = MetricsOf(udp, tcp, Clock.Now - startTicks),
            Gates =
            {
                // A dns arm measures queries, not datagrams, so it has no loss of its own to gate.
                [ArmKeys.Common.Gates.ClientSendLoss] = 0L,
                [ArmKeys.Common.Gates.WindowMs] = 0L,
            },
        };

        outcome.Notes.Add("every query written to the socket is terminal in exactly one of answered (rcode 0), servfail (rcode != 0), timeout (still unmatched when the drain window closed) or other (tcp only: a response consumed a queued query but carried a different transaction id); answered + servfail + timeout + other == sent, and unanswered is timeout + other.");
        outcome.Notes.Add("drainWindowMs is not a per-query deadline. Once the send phase ends the arm keeps reading for that long and only then books every query still pending as timeout, so a query answered inside the window counts as answered however long it took, and a response that arrives after the window is closed can only be counted unmatched. cnameEvery > 0 replaces every cnameEvery-th query with a CNAME query, and because the udp path is keyed by transaction id, a udp response whose id matches no outstanding query is unmatched and can never be other.");
        outcome.Notes.Add("unsent counts pacing slots skipped because the in-flight window was full: those queries never reached the socket and are in no outcome, so offered is sent + unsent and neither answerRate nor achievedRate includes them; socketErrors counts socket-level failures (connect, send, receive) that belong to no single query.");
        outcome.Notes.Add("answerRate is answered / sent and achievedRate is sent per elapsed second; both divide the sent population, not the offered one.");
        outcome.Notes.Add("queryTypes counts the queries actually written to the socket, per DNS type, so the mix is measured from QueryTypeFor rather than declared: a plan interleaving CNAME queries shows them here instead of hiding them.");
        outcome.Notes.Add("transaction ids are reused on wraparound; a response matching no outstanding query is counted as unmatched, never as answered, and on TCP the response is matched against the id queued with the query it is credited to.");
        return outcome;
    }

    private static DnsMetrics MetricsOf(DnsCounters udp, DnsCounters tcp, long elapsedTicks)
    {
        var answered = udp._answered + tcp._answered;
        var servfail = udp._servfail + tcp._servfail;
        var timeout = udp._timeout + tcp._timeout;
        var other = udp._other + tcp._other;
        var sent = udp._sent + tcp._sent;
        var unsent = udp._unsent + tcp._unsent;

        return new DnsMetrics
        {
            Sent = sent,
            UdpSent = udp._sent,
            TcpSent = tcp._sent,
            Unsent = unsent,
            UdpUnsent = udp._unsent,
            TcpUnsent = tcp._unsent,
            Offered = sent + unsent,
            Answered = answered,
            Servfail = servfail,
            Timeout = timeout,
            Other = other,
            Unanswered = timeout + other,
            SocketErrors = udp._socketErrors + tcp._socketErrors,
            EmptyAnswers = udp._emptyAnswers + tcp._emptyAnswers,
            Malformed = udp._malformed + tcp._malformed,
            Unmatched = udp._unmatched + tcp._unmatched,
            AnswerRate = JsonRate.Rate(answered, sent),
            AchievedRate = JsonPerSecond.PerSecond(sent, elapsedTicks, Stopwatch.Frequency),
            Rcodes = BuildRcodes(udp, tcp),
            QueryTypes = BuildQueryTypes(udp, tcp),
        };
    }

    private static Dictionary<string, long> BuildRcodes(DnsCounters udp, DnsCounters tcp)
    {
        var rcodes = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var code = 0; code < udp._rcodes.Length; code++)
        {
            var count = udp._rcodes[code] + tcp._rcodes[code];
            if (count > 0)
            {
                rcodes[code.ToString(System.Globalization.CultureInfo.InvariantCulture)] = count;
            }
        }

        return rcodes;
    }

    private static Dictionary<string, long> BuildQueryTypes(DnsCounters udp, DnsCounters tcp)
    {
        var types = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var type = 0; type < udp._queryTypes.Length; type++)
        {
            var count = udp._queryTypes[type] + tcp._queryTypes[type];
            if (count > 0)
            {
                types[TypeName((ushort)type)] = count;
            }
        }

        return types;
    }

    private static string TypeName(ushort type) => type switch
    {
        DnsWire.TypeA => "A",
        DnsWire.TypeAaaa => "AAAA",
        DnsWire.TypeCname => "CNAME",
        DnsWire.TypeHttps => "HTTPS",
        DnsWire.TypeTxt => "TXT",

        // One key per unlisted type: a shared "other" bucket would let one type's count overwrite another's.
        _ => $"type{type}",
    };

    private static ushort QueryTypeFor(long index, int cnameEvery)
    {
        if (cnameEvery > 0 && index > 0 && index % cnameEvery == 0)
        {
            return DnsWire.TypeCname;
        }

        return (index % 100) switch
        {
            < 54 => DnsWire.TypeA,
            < 78 => DnsWire.TypeAaaa,
            < 98 => DnsWire.TypeHttps,
            _ => DnsWire.TypeTxt,
        };
    }

    private static int BuildQuery(Span<byte> destination, ushort transactionId, long index, int cnameEvery) =>
        DnsWire.BuildQuery(destination, transactionId, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"q{index}.bench.local"), QueryTypeFor(index, cnameEvery));

    private static void Classify(DnsCounters counters, int rcode, int answerCount)
    {
        if (rcode == 0)
        {
            counters._answered++;
            if (answerCount == 0)
            {
                counters._emptyAnswers++;
            }
        }
        else
        {
            counters._servfail++;
        }

        counters._rcodes[rcode & 0xF]++;
    }

    private static async Task RunUdpAsync(
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

        var until = Clock.Now + Clock.FromSeconds(DrainWindowMilliseconds / 1000.0);
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
            var length = BuildQuery(sendBuffer, transactionId, index, cnameEvery);
            if (length <= 0)
            {
                counters._malformed++;
                continue;
            }

            await socket.SendAsync(sendBuffer.AsMemory(0, length), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            counters._sent++;
            counters._queryTypes[QueryTypeFor(index, cnameEvery)]++;
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
                Classify(counters, rcode, answerCount);
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

    private static async Task RunTcpAsync(
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
            counters._queryTypes[QueryTypeFor(index, cnameEvery)]++;
            pending.Enqueue(new PendingQuery(transactionId, intended));
        }

        var drainUntil = Clock.Now + Clock.FromSeconds(DrainWindowMilliseconds / 1000.0);
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
        var length = BuildQuery(sendBuffer, transactionId, index, cnameEvery);
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
        Classify(counters, rcode, answerCount);
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
