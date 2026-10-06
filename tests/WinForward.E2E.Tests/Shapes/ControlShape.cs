using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Tests.Shapes;

/// <summary>
/// The <c>"base"</c> kind's shape contract: one elapsed time beside the two phase objects, whose own
/// members sit one level down. The latency phase is the latency record, so its two protocol blocks are
/// omitted whole in the flag state that does not run them; the loss phase is unconditional.
/// </summary>
internal static class ControlShape
{
    /// <summary>How many notes the factory below publishes.</summary>
    private const int Notes = 4;

    /// <summary>The phases the control ran, which is the arity of <c>parameters/phases</c>.</summary>
    private const int Phases = 2;

    private static readonly string[] s_phases = ["latency", "loss"];

    private const string Metrics = "metrics";
    private const string LatencyPhase = $"{Metrics}/{ArmKeys.Control.Latency}";
    private const string LossPhase = $"{Metrics}/{ArmKeys.Control.Loss}";

    // The two protocol families of the latency phase, which are form-A keys: the prefix is the dotted
    // family name up to the member, the way the latency kind declares them under `metrics`.
    private const string TcpBlock = $"{LatencyPhase}/tcp.";
    private const string UdpBlock = $"{LatencyPhase}/udp.";

    private const string LatencyParameterPrefix = $"parameters/{ArmKeys.Common.Parameters.Latency}";
    private const string LossParameterPrefix = $"parameters/{ArmKeys.Common.Parameters.Loss}";

    /// <summary>The loss phase's nullable readings: the eight rates that are null over an empty population or duration.</summary>
    private static readonly string[] s_lossNullable =
    [
        $"{LossPhase}/{ArmKeys.Control.LossPhase.LossRate}",
        $"{LossPhase}/{ArmKeys.Control.LossPhase.StrictLossRate}",
        $"{LossPhase}/{ArmKeys.Control.LossPhase.LateRate}",
        $"{LossPhase}/{ArmKeys.Control.LossPhase.CorruptRate}",
        $"{LossPhase}/{ArmKeys.Control.LossPhase.DuplicateRate}",
        $"{LossPhase}/{ArmKeys.Control.LossPhase.ReorderRate}",
        $"{LossPhase}/{ArmKeys.Control.LossPhase.ClientSendLossRate}",
        $"{LossPhase}/{ArmKeys.Control.LossPhase.AchievedRate}",
    ];

    internal static readonly KindContract s_contract = new(
        "base",
        Outcome,
        new MetricsContract(
            Prefix: Metrics,
            PropertyCount: DeclaredKeys.PropertyCount(typeof(ControlMetrics)),
            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(ControlMetrics)),
            Declared: Declared(),

            // Both of the phase's protocol families are conditional, so every latency key is omitted
            // in one of the two flag states.
            Nullable:
            [
                $"{LatencyPhase}/{ArmKeys.Latency.TcpAchievedRate}",
                $"{LatencyPhase}/{ArmKeys.Latency.TcpMeanConnectMs}",
                $"{LatencyPhase}/{ArmKeys.Latency.UdpAchievedRate}",
                $"{LatencyPhase}/{ArmKeys.Latency.UdpLossRate}",
                .. s_lossNullable,
            ],
            Conditional: DeclaredKeys.Under(typeof(ArmKeys.Latency), LatencyPhase),
            Arrays:
            [
                new ArrayArity($"{LatencyPhase}/{ArmKeys.Latency.TcpLaneSupplied}", LatencyShape.Lanes),
                new ArrayArity($"{LatencyPhase}/{ArmKeys.Latency.TcpLaneSentOk}", LatencyShape.Lanes),
                new ArrayArity($"parameters/{ArmKeys.Common.Parameters.Phases}", Phases),
                new ArrayArity(ArmKeys.Common.Record.Notes, Notes),
            ],
            Blocks:
            [
                new MetricsBlock(
                    Prefix: $"{LatencyPhase}/",
                    PropertyCount: DeclaredKeys.PropertyCount(typeof(LatencyMetrics)),
                    NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(LatencyMetrics)),
                    OmittedWhen: ShapeFlags.None,
                    PublishesContainerKey: true,
                    Blocks:
                    [
                        new MetricsBlock(
                            Prefix: TcpBlock,
                            PropertyCount: DeclaredKeys.PropertyCount(typeof(LatencyTcpMetrics)),
                            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(LatencyTcpMetrics)),
                            OmittedWhen: ShapeFlags.UdpOnly,
                            PublishesContainerKey: false,
                            Blocks: []),
                        new MetricsBlock(
                            Prefix: UdpBlock,
                            PropertyCount: DeclaredKeys.PropertyCount(typeof(LatencyUdpMetrics)),
                            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(LatencyUdpMetrics)),
                            OmittedWhen: ShapeFlags.TcpOnly,
                            PublishesContainerKey: false,
                            Blocks: []),
                    ]),
                new MetricsBlock(
                    Prefix: $"{LossPhase}/",

                    // The loss phase still publishes through the migration adapter, so there is no record
                    // whose properties could be counted: the declared key set is the count until B2c types
                    // the arm, and this becomes a reflection read of the loss record.
                    PropertyCount: DeclaredKeys.Under(typeof(ArmKeys.Control.LossPhase), LossPhase).Count,
                    NullablePropertyCount: s_lossNullable.Length,
                    OmittedWhen: ShapeFlags.None,
                    PublishesContainerKey: true,
                    Blocks: []),
            ],
            Dynamic: []),
        Parameters:
        [
            .. DeclaredKeys.Under(typeof(ArmKeys.Common.Parameters), LatencyParameterPrefix),
            .. DeclaredKeys.Under(typeof(ArmKeys.Common.Parameters), LossParameterPrefix),
        ]);

    /// <summary>
    /// The declared paths of this record in document order: the elapsed time, then each phase container
    /// ahead of the members the phase publishes inside it.
    /// </summary>
    private static List<string> Declared() =>
    [
        $"{Metrics}/{ArmKeys.Control.ElapsedSeconds}",
        LatencyPhase,
        .. DeclaredKeys.Under(typeof(ArmKeys.Latency), LatencyPhase),
        LossPhase,
        .. DeclaredKeys.Under(typeof(ArmKeys.Control.LossPhase), LossPhase),
    ];

    /// <summary>
    /// The record a measured control run publishes: the latency record the latency kind's factory
    /// builds under the same flags, one level down, and a loss phase with its classification identity
    /// intact.
    /// </summary>
    private static ArmOutcome Outcome(ShapeFlags flags)
    {
        var outcome = new ArmOutcome
        {
            Metrics = new ControlMetrics
            {
                ElapsedSeconds = 10.234,
                Latency = LatencyShape.Metrics(flags),
                Loss = LossPhaseMetrics(flags),
            },
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = 0L,
                [ArmKeys.Common.Gates.WindowOverflow] = 0L,
                [ArmKeys.Common.Gates.BacklogDrops] = 0L,
                [ArmKeys.Common.Gates.SendFailures] = 0L,
                [ArmKeys.Common.Gates.LaneShortfall] = 0L,
                [ArmKeys.Common.Gates.ScheduleTruncated] = 0L,
                [ArmKeys.Common.Gates.InFlightCeilingMs] = 3_169.415,
                [ArmKeys.Common.Gates.WindowMs] = 200.0,
            },
            Notes =
            {
                "base runs the latency arm and then the loss arm back to back inside one record, with no proxifier loaded: it is the harness floor.",
                "base is two 10 s phases, so parameters.seconds is their sum and parameters.latency/parameters.loss carry the effective per-phase parameters; metrics.elapsedSeconds is the measured wall time.",
                "the latency phase runs the plan's protocol and the loss phase always runs udp, so a BASE entry that wants a udp latency floor has to declare protocol.",
                "a rate whose denominator is zero is written as null rather than 0: nothing was sent, so there is no rate to report.",
            },
        };
        outcome.Parameters.Add(ArmKeys.Common.Parameters.Seconds, 20.0);
        outcome.Parameters.Add(ArmKeys.Common.Parameters.PhaseSeconds, 10.0);
        outcome.Parameters.Add(ArmKeys.Common.Parameters.Phases, s_phases);
        outcome.Parameters.Add(ArmKeys.Common.Parameters.Latency, LatencyShape.PhaseParameters(flags));
        outcome.Parameters.Add(ArmKeys.Common.Parameters.Loss, LossParameters());
        return outcome;
    }

    /// <summary>
    /// The loss phase as the arm publishes it today: one name/value object, written through the same
    /// migration adapter the arm uses. Its eight rates are the readings an empty population or duration
    /// leaves unknown.
    /// </summary>
    private static DictionaryMetrics LossPhaseMetrics(ShapeFlags flags)
    {
        var known = !flags.HasFlag(ShapeFlags.UnknownReadings);
        return new DictionaryMetrics
        {
            [ArmKeys.Control.LossPhase.Sent] = 1_000L,
            [ArmKeys.Control.LossPhase.Supplied] = 1_000L,
            [ArmKeys.Control.LossPhase.Arrived] = 998L,
            [ArmKeys.Control.LossPhase.Late] = 1L,
            [ArmKeys.Control.LossPhase.Never] = 0L,
            [ArmKeys.Control.LossPhase.Corrupt] = 0L,
            [ArmKeys.Control.LossPhase.CorruptDatagrams] = 1L,
            [ArmKeys.Control.LossPhase.Duplicate] = 0L,
            [ArmKeys.Control.LossPhase.Reordered] = 0L,
            [ArmKeys.Control.LossPhase.UnmatchedReplies] = 0L,
            [ArmKeys.Control.LossPhase.ForeignConnection] = 0L,
            [ArmKeys.Control.LossPhase.ReceivedDatagrams] = 999L,
            [ArmKeys.Control.LossPhase.ReceivedBytes] = 199_800L,
            [ArmKeys.Control.LossPhase.ClientSendLoss] = 0L,
            [ArmKeys.Control.LossPhase.SendWouldBlock] = 0L,
            [ArmKeys.Control.LossPhase.SendFailures] = 0L,
            [ArmKeys.Control.LossPhase.WindowOverflow] = 0L,
            [ArmKeys.Control.LossPhase.AbandonedAtTeardown] = 0L,
            [ArmKeys.Control.LossPhase.Window] = 200.0,
            [ArmKeys.Control.LossPhase.LossRate] = known ? 0.001 : null,
            [ArmKeys.Control.LossPhase.StrictLossRate] = known ? 0.001 : null,
            [ArmKeys.Control.LossPhase.LateRate] = known ? 0.001 : null,
            [ArmKeys.Control.LossPhase.CorruptRate] = known ? 0.0 : null,
            [ArmKeys.Control.LossPhase.DuplicateRate] = known ? 0.0 : null,
            [ArmKeys.Control.LossPhase.ReorderRate] = known ? 0.0 : null,
            [ArmKeys.Control.LossPhase.ClientSendLossRate] = known ? 0.0 : null,
            [ArmKeys.Control.LossPhase.OutOfRangeSequences] = 0L,
            [ArmKeys.Control.LossPhase.AchievedRate] = known ? 100.0 : null,
        };
    }

    /// <summary>The loss phase's effective parameters, as the phase arm publishes them.</summary>
    private static Dictionary<string, object?> LossParameters() => new(StringComparer.Ordinal)
    {
        [ArmKeys.Common.Parameters.Seconds] = 10.0,
        [ArmKeys.Common.Parameters.RatePerSecond] = 500,
        [ArmKeys.Common.Parameters.PayloadBytes] = 200,
        [ArmKeys.Common.Parameters.LossWindowMs] = 200,
    };
}
