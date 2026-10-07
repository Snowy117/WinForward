using System.Diagnostics;
using System.Net;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Client.Arms;

internal static class DnsArm
{
    private const int DefaultRatePerSecond = 200;
    internal const int DrainWindowMilliseconds = 1000;

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
            tasks.Add(Dedicated.RunOnOwnThreadAsync(() => DnsUdpPhase.RunUdpAsync(context, udp, dnsEndPoint, udpRate, cnameEvery, startTicks, deadlineTicks, cancellationToken)));
        }

        if (tcpRate > 0)
        {
            tasks.Add(Dedicated.RunOnOwnThreadAsync(() => DnsTcpPhase.RunTcpAsync(context, tcp, dnsEndPoint, tcpRate, cnameEvery, startTicks, deadlineTicks, cancellationToken)));
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
                types[DnsQueryBuilder.TypeName((ushort)type)] = count;
            }
        }

        return types;
    }

    internal static void Classify(DnsCounters counters, int rcode, int answerCount)
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
}
