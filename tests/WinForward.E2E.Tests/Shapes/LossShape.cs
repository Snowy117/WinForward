using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Tests.Shapes;

/// <summary>
/// The <c>loss</c> kind's shape contract: one flat metrics object whose eight rates are the readings
/// an empty population or duration leaves unknown, and the parameters the arm applied its own
/// defaults to.
/// </summary>
internal static class LossShape
{
    /// <summary>The declared loss window the factory's record carries.</summary>
    private const int WindowMs = 200;

    /// <summary>How many notes the factory below publishes.</summary>
    private const int Notes = 2;

    private const string MetricsPrefix = "metrics";
    private const string Parameters = ArmKeys.Common.Record.Parameters;

    /// <summary>The parameter members the arm publishes, which is what the contract declares.</summary>
    internal static readonly string[] s_parameterNames =
    [
        ArmKeys.Common.Parameters.Seconds,
        ArmKeys.Common.Parameters.RatePerSecond,
        ArmKeys.Common.Parameters.PayloadBytes,
        ArmKeys.Common.Parameters.LossWindowMs,
    ];

    /// <summary>The eight readings that are null over an empty population or duration.</summary>
    private static readonly string[] s_nullable =
    [
        $"{MetricsPrefix}/{ArmKeys.Loss.LossRate}",
        $"{MetricsPrefix}/{ArmKeys.Loss.StrictLossRate}",
        $"{MetricsPrefix}/{ArmKeys.Loss.LateRate}",
        $"{MetricsPrefix}/{ArmKeys.Loss.CorruptRate}",
        $"{MetricsPrefix}/{ArmKeys.Loss.DuplicateRate}",
        $"{MetricsPrefix}/{ArmKeys.Loss.ReorderRate}",
        $"{MetricsPrefix}/{ArmKeys.Loss.ClientSendLossRate}",
        $"{MetricsPrefix}/{ArmKeys.Loss.AchievedRate}",
    ];

    internal static readonly KindContract s_contract = new(
        "loss",
        Outcome,
        new MetricsContract(
            Prefix: MetricsPrefix,
            PropertyCount: DeclaredKeys.PropertyCount(typeof(LossMetrics)),
            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(LossMetrics)),
            Declared: DeclaredKeys.Under(typeof(ArmKeys.Loss), MetricsPrefix),
            Nullable: s_nullable,
            Conditional: [],
            Arrays: [new ArrayArity(ArmKeys.Common.Record.Notes, Notes)],
            Blocks: [],
            Dynamic: []),
        Parameters: [.. s_parameterNames.Select(name => $"{Parameters}/{name}")]);

    /// <summary>
    /// The record a measured run publishes: the classification identity holds (arrived + late + never
    /// + abandonedAtTeardown + corruptDatagrams == sent), and the rates are the ones those counters
    /// derive.
    /// </summary>
    internal static LossMetrics Metrics(ShapeFlags flags) => new()
    {
        Sent = 1_000,
        Supplied = 1_000,
        Arrived = 998,
        Late = 1,
        Never = 0,
        Corrupt = 0,
        CorruptDatagrams = 1,
        Duplicate = 0,
        Reordered = 0,
        UnmatchedReplies = 0,
        ForeignConnection = 0,
        ReceivedDatagrams = 999,
        ReceivedBytes = 199_800,
        ClientSendLoss = 0,
        SendWouldBlock = 0,
        SendFailures = 0,
        WindowOverflow = 0,
        AbandonedAtTeardown = 0,
        Window = WindowMs,
        LossRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 0.001,
        StrictLossRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 0.001,
        LateRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 0.001,
        CorruptRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 0.0,
        DuplicateRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 0.0,
        ReorderRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 0.0,
        ClientSendLossRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 0.0,
        OutOfRangeSequences = 0,
        SentOutOfRangeSequences = 0,
        AchievedRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 100.0,
    };

    /// <summary>The load the arm declared for this run.</summary>
    internal static ArmParameters ParametersOf() => new()
    {
        Seconds = 10.0,
        RatePerSecond = 200,
        PayloadBytes = 200,
        LossWindowMs = WindowMs,
    };

    private static ArmOutcome Outcome(ShapeFlags flags) => new()
    {
        Parameters = ParametersOf(),
        Metrics = Metrics(flags),
        Gates =
        {
            [ArmKeys.Common.Gates.ClientSendLoss] = 0,
            [ArmKeys.Common.Gates.WindowMs] = WindowMs,
        },
        Notes =
        {
            "every sent datagram is classified exactly once, so arrived + late + never + abandonedAtTeardown + corruptDatagrams == sent by construction.",
            "the drain runs until W after the last real send, so no datagram is called lost before its whole W has passed; a drain cut short leaves the datagrams still inside their window in abandonedAtTeardown rather than in never.",
        },
    };
}
