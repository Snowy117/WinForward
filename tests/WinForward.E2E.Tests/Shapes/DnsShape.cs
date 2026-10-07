using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Tests.Shapes;

/// <summary>
/// The <c>dns</c> kind's shape contract: one flat metrics object whose <c>rcodes</c> and
/// <c>queryTypes</c> members are named by the run.
/// </summary>
internal static class DnsShape
{
    /// <summary>How many notes the factory below publishes.</summary>
    private const int Notes = 2;

    private const string ParametersPrefix = ArmKeys.Common.Record.Parameters;

    /// <summary>The parameter members the arm publishes, which is what the contract declares.</summary>
    private static readonly string[] s_parameterNames =
    [
        ArmKeys.Common.Parameters.Seconds,
        ArmKeys.Common.Parameters.RatePerSecond,
        ArmKeys.Common.Parameters.TcpPercent,
        ArmKeys.Common.Parameters.CnameEvery,
        ArmKeys.Common.Parameters.DnsPort,
        ArmKeys.Common.Parameters.DrainWindowMs,
    ];

    internal static readonly KindContract s_contract = new(
        "dns",
        static flags => new ArmOutcome
        {
            Parameters = new ArmParameters
            {
                Seconds = 8.0,
                RatePerSecond = 50,
                TcpPercent = 20,
                CnameEvery = 0,
                DnsPort = 5301,
                DrainWindowMs = 1000,
            },
            Metrics = new DnsMetrics
            {
                Sent = 400,
                UdpSent = 320,
                TcpSent = 80,
                Unsent = 0,
                UdpUnsent = 0,
                TcpUnsent = 0,
                Offered = 400,
                Answered = 399,
                Servfail = 0,
                Timeout = 1,
                Other = 0,
                Unanswered = 1,
                SocketErrors = 0,
                EmptyAnswers = 0,
                Malformed = 0,
                Unmatched = 0,
                AnswerRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 0.9975,
                AchievedRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 49.875,
                Rcodes = ShapeData.Counts(flags, [("0", 399)]),
                QueryTypes = ShapeData.Counts(flags, [("A", 216), ("AAAA", 96), ("HTTPS", 80), ("TXT", 8)]),
            },
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = 0L,
                [ArmKeys.Common.Gates.WindowMs] = 0L,
            },
            Notes =
            {
                "every query written to the socket is terminal in exactly one of answered (rcode 0), servfail (rcode != 0), timeout (still unmatched when the drain window closed) or other (tcp only: a response consumed a queued query but carried a different transaction id).",
                "queryTypes counts the queries actually written to the socket, per DNS type, so the mix is measured from the query the arm built rather than declared.",
            },
        },
        new MetricsContract(
            Prefix: "metrics",
            PropertyCount: DeclaredKeys.PropertyCount(typeof(DnsMetrics)),
            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(DnsMetrics)),
            Declared: DeclaredKeys.Under(typeof(ArmKeys.Dns), "metrics"),
            Nullable:
            [
                $"metrics/{ArmKeys.Dns.AnswerRate}",
                $"metrics/{ArmKeys.Dns.AchievedRate}",
            ],
            Conditional: [],
            Arrays: [new ArrayArity(ArmKeys.Common.Record.Notes, Notes)],
            Blocks: [],
            Dynamic:
            [
                new DynamicMembers($"metrics/{ArmKeys.Dns.Rcodes}", ShapeData.IsRcodeName),
                new DynamicMembers($"metrics/{ArmKeys.Dns.QueryTypes}", ShapeData.IsQueryTypeName),
            ]),
        Parameters: [.. s_parameterNames.Select(name => $"{ParametersPrefix}/{name}")]);
}
