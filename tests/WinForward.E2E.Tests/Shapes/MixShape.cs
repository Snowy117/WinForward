using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Tests.Shapes;

/// <summary>
/// The <c>mix</c> kind's shape contract: a nested <c>classes</c> object with four flow-class blocks,
/// the arm-wide counters, and one lane witness per desktop. Every block is unconditional, so the only
/// shape that follows the plan is the arity of the two arrays.
/// </summary>
internal static class MixShape
{
    /// <summary>The desktops the factory's record carries: the arity of both of its arrays.</summary>
    private const int Desktops = 4;

    /// <summary>How many notes the factory below publishes.</summary>
    private const int Notes = 2;

    private const string ParametersPrefix = ArmKeys.Common.Record.Parameters;

    /// <summary>The parameter members the arm publishes, which is what the contract declares.</summary>
    private static readonly string[] s_parameterNames =
    [
        ArmKeys.Common.Parameters.Seconds,
        ArmKeys.Common.Parameters.Desktops,
        ArmKeys.Common.Parameters.PageIntervalSeconds,
        ArmKeys.Common.Parameters.PageConnections,
        ArmKeys.Common.Parameters.PageRequestsTotal,
        ArmKeys.Common.Parameters.PageMessageBytes,
        ArmKeys.Common.Parameters.BulkBitsPerSecondPerDesktop,
        ArmKeys.Common.Parameters.DnsQueriesPerPage,
        ArmKeys.Common.Parameters.UdpPacketsPerSecondPerDesktop,
        ArmKeys.Common.Parameters.UdpPayloadBytes,
        ArmKeys.Common.Parameters.LossWindowMs,
    ];

    // The container paths every key of this record hangs off, composed from the same constants the
    // writer uses, so a container that is renamed moves the whole shape with it.
    private const string Metrics = "metrics";
    private const string Classes = $"{Metrics}/{ArmKeys.Mix.Classes}";
    private const string Page = $"{Classes}/{ArmKeys.Mix.ClassNames.Page}";
    private const string Bulk = $"{Classes}/{ArmKeys.Mix.ClassNames.Bulk}";
    private const string Dns = $"{Classes}/{ArmKeys.Mix.ClassNames.Dns}";
    private const string Udp = $"{Classes}/{ArmKeys.Mix.ClassNames.Udp}";
    private const string Lanes = $"{Metrics}/{ArmKeys.Mix.Desktops}";

    internal static readonly KindContract s_contract = new(
        "mix",
        Outcome,
        new MetricsContract(
            Prefix: Metrics,
            PropertyCount: DeclaredKeys.PropertyCount(typeof(MixMetrics)),
            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(MixMetrics)),
            Declared: Declared(),
            Nullable:
            [
                $"{Metrics}/{ArmKeys.Mix.UdpLossRate}",
                $"{Page}/{ArmKeys.Mix.PageClass.BytesPerPage}",
                $"{Udp}/{ArmKeys.Mix.UdpClass.LossRate}",
            ],
            Conditional: [],
            Arrays:
            [
                new ArrayArity($"{Udp}/{ArmKeys.Mix.UdpClass.SentPerDesktop}", Desktops),
                new ArrayArity(Lanes, Desktops),
                new ArrayArity(ArmKeys.Common.Record.Notes, Notes),
            ],
            Blocks:
            [
                new MetricsBlock(
                    Prefix: $"{Classes}/",
                    PropertyCount: DeclaredKeys.PropertyCount(typeof(MixClassesMetrics)),
                    NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(MixClassesMetrics)),
                    OmittedWhen: ShapeFlags.None,
                    PublishesContainerKey: true,
                    Blocks:
                    [
                        new MetricsBlock(
                            Prefix: $"{Page}/",
                            PropertyCount: DeclaredKeys.PropertyCount(typeof(MixPageClassMetrics)),
                            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(MixPageClassMetrics)),
                            OmittedWhen: ShapeFlags.None,
                            PublishesContainerKey: true,
                            Blocks: []),
                        new MetricsBlock(
                            Prefix: $"{Bulk}/",
                            PropertyCount: DeclaredKeys.PropertyCount(typeof(MixBulkClassMetrics)),
                            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(MixBulkClassMetrics)),
                            OmittedWhen: ShapeFlags.None,
                            PublishesContainerKey: true,
                            Blocks: []),
                        new MetricsBlock(
                            Prefix: $"{Dns}/",
                            PropertyCount: DeclaredKeys.PropertyCount(typeof(MixDnsClassMetrics)),
                            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(MixDnsClassMetrics)),
                            OmittedWhen: ShapeFlags.None,
                            PublishesContainerKey: true,
                            Blocks: []),
                        new MetricsBlock(
                            Prefix: $"{Udp}/",
                            PropertyCount: DeclaredKeys.PropertyCount(typeof(MixUdpClassMetrics)),
                            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(MixUdpClassMetrics)),
                            OmittedWhen: ShapeFlags.None,
                            PublishesContainerKey: true,
                            Blocks: []),
                    ]),
                new MetricsBlock(
                    Prefix: $"{Lanes}/",
                    PropertyCount: DeclaredKeys.PropertyCount(typeof(MixDesktopMetrics)),
                    NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(MixDesktopMetrics)),
                    OmittedWhen: ShapeFlags.None,
                    PublishesContainerKey: true,
                    Blocks: []),
            ],
            Dynamic: []),
        Parameters: [.. s_parameterNames.Select(name => $"{ParametersPrefix}/{name}")]);

    /// <summary>
    /// The declared paths of this record in document order: each class container ahead of its own block,
    /// which is the order the writer opens and closes the objects in.
    /// </summary>
    private static List<string> Declared() =>
    [
        Classes,
        Page,
        .. DeclaredKeys.Under(typeof(ArmKeys.Mix.PageClass), Page),
        Bulk,
        .. DeclaredKeys.Under(typeof(ArmKeys.Mix.BulkClass), Bulk),
        Dns,
        .. DeclaredKeys.Under(typeof(ArmKeys.Mix.DnsClass), Dns),
        Udp,
        .. DeclaredKeys.Under(typeof(ArmKeys.Mix.UdpClass), Udp),
        $"{Metrics}/{ArmKeys.Mix.Pages}",
        $"{Metrics}/{ArmKeys.Mix.PageConnections}",
        $"{Metrics}/{ArmKeys.Mix.PageBytes}",
        $"{Metrics}/{ArmKeys.Mix.BulkBytes}",
        $"{Metrics}/{ArmKeys.Mix.DnsSent}",
        $"{Metrics}/{ArmKeys.Mix.UdpSent}",
        $"{Metrics}/{ArmKeys.Mix.UdpLossRate}",
        $"{Metrics}/{ArmKeys.Mix.ClientSendLoss}",
        Lanes,
        .. DeclaredKeys.Under(typeof(ArmKeys.Mix.DesktopLane), Lanes),
    ];

    /// <summary>
    /// The record a measured four-desktop run publishes. The class totals are the sums of the lane
    /// witnesses below and the UDP classification identity holds, so the fixture reads as one run
    /// rather than as numbers that only have to differ from each other.
    /// </summary>
    private static ArmOutcome Outcome(ShapeFlags flags)
    {
        var outcome = new ArmOutcome
        {
            Parameters = ParametersOf(),
            Metrics = flags.HasFlag(ShapeFlags.UnknownReadings) ? UnknownMetrics() : MeasuredMetrics(),
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = 0L,
                [ArmKeys.Common.Gates.IdleLanes] = 0L,
                [ArmKeys.Common.Gates.WindowMs] = 200L,
            },
            Notes =
            {
                "every flow class carries the shared frame layout, so the per-class byte and latency numbers come from the same measurement path as the dedicated arms.",
                "gates.idleLanes counts the witnesses that stayed at zero: a lane that never ran is a harness failure, not a smaller aggregate.",
            },
        };
        return outcome;
    }

    /// <summary>The load the arm declared for this run.</summary>
    private static ArmParameters ParametersOf() => new()
    {
        Seconds = 10.0,
        Desktops = Desktops,
        PageIntervalSeconds = 20.0,
        PageConnections = 13,
        PageRequestsTotal = 73,
        PageMessageBytes = 38_000,
        BulkBitsPerSecondPerDesktop = 5_000_000L,
        DnsQueriesPerPage = 4,
        UdpPacketsPerSecondPerDesktop = 30,
        UdpPayloadBytes = 120,
        LossWindowMs = 200,
    };

    private static MixMetrics MeasuredMetrics() => new()
    {
        Classes = new MixClassesMetrics
        {
            Page = new MixPageClassMetrics
            {
                Pages = 4,
                Connections = 52,
                Messages = 292,
                Bytes = 11_096_000,
                Errors = 0,
                BytesPerPage = 2_774_000,
            },
            Bulk = new MixBulkClassMetrics
            {
                Bytes = 25_059_200,
                BytesSent = 25_100_000,
                Frames = 764,
                Errors = 0,
                GoodputBps = 1_249_710.485,
            },
            Dns = new MixDnsClassMetrics
            {
                Sent = 16,
                Answered = 16,
                Servfail = 0,
                Timeout = 0,
                Other = 0,
            },
            Udp = new MixUdpClassMetrics
            {
                Sent = 1_200,
                Arrived = 1_198,
                Late = 1,
                Never = 0,
                Corrupt = 0,
                CorruptDatagrams = 1,
                Duplicate = 0,
                Reordered = 0,
                UnmatchedReplies = 0,
                ForeignConnection = 0,
                AbandonedAtTeardown = 0,
                SendFailures = 0,
                WindowOverflow = 0,
                OutOfRangeSequences = 0,
                SentOutOfRangeSequences = 0,
                Bytes = 182_400,
                ClientSendLoss = 0,
                LossRate = 0.000833,
                Window = 200,
                SentPerDesktop = [301, 301, 300, 298],
            },
        },
        Pages = 4,
        PageConnections = 52,
        PageBytes = 11_096_000,
        BulkBytes = 25_059_200,
        DnsSent = 16,
        UdpSent = 1_200,
        UdpLossRate = 0.000833,
        ClientSendLoss = 0,
        Desktops =
        [
            Lane(0, 301, 301),
            Lane(1, 301, 300),
            Lane(2, 300, 300),
            Lane(3, 298, 297),
        ],
    };

    /// <summary>
    /// The same run with the three readings that can have no measurement behind them at all: a lost
    /// datagram population of zero, no page completed, and no elapsed time to derive a goodput from.
    /// Nothing else moves, because an unknown reading removes no key.
    /// </summary>
    private static MixMetrics UnknownMetrics()
    {
        var metrics = MeasuredMetrics();
        return metrics with
        {
            Classes = metrics.Classes with
            {
                Page = metrics.Classes.Page with { BytesPerPage = null },
                Udp = metrics.Classes.Udp with { LossRate = null },
            },
            UdpLossRate = null,
        };
    }

    private static MixDesktopMetrics Lane(long desktop, long udpSent, long udpArrived) => new()
    {
        Desktop = desktop,
        UdpSent = udpSent,
        UdpArrived = udpArrived,
        UdpNever = 0,
        UdpForeignConnection = 0,
        PageConnections = 13,
        BulkFrames = 191,
        DnsSent = 4,
    };
}
