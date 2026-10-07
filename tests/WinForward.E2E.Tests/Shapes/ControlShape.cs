using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Tests.Shapes;

/// <summary>
/// The <c>"base"</c> kind's shape contract: one elapsed time beside the two phase objects, whose own
/// members sit one level down. Each phase is the phase arm's own typed record, so the phase's keys
/// are that kind's keys and cannot be declared a second time here.
/// </summary>
internal static class ControlShape
{
    /// <summary>How many notes the factory below publishes.</summary>
    private const int Notes = 4;

    /// <summary>The phases the control ran, which is the arity of <c>parameters/phases</c>.</summary>
    private const int Phases = 2;

    private static readonly string[] s_phases = ["latency", "loss"];

    private const string MetricsPrefix = "metrics";
    private const string ParametersPrefix = ArmKeys.Common.Record.Parameters;
    private const string LatencyPhase = $"{MetricsPrefix}/{ArmKeys.Control.Latency}";
    private const string LossPhase = $"{MetricsPrefix}/{ArmKeys.Control.Loss}";

    // The two protocol families of the latency phase, which are form-A keys: the prefix is the dotted
    // family name up to the member, the way the latency kind declares them under `metrics`.
    private const string TcpBlock = $"{LatencyPhase}/tcp.";
    private const string UdpBlock = $"{LatencyPhase}/udp.";

    private const string LatencyParameterPrefix = $"{ParametersPrefix}/{ArmKeys.Common.Parameters.Latency}";
    private const string LossParameterPrefix = $"{ParametersPrefix}/{ArmKeys.Common.Parameters.Loss}";

    /// <summary>The loss phase's nullable readings: the eight rates that are null over an empty population or duration.</summary>
    private static readonly string[] s_lossNullable =
    [
        $"{LossPhase}/{ArmKeys.Loss.LossRate}",
        $"{LossPhase}/{ArmKeys.Loss.StrictLossRate}",
        $"{LossPhase}/{ArmKeys.Loss.LateRate}",
        $"{LossPhase}/{ArmKeys.Loss.CorruptRate}",
        $"{LossPhase}/{ArmKeys.Loss.DuplicateRate}",
        $"{LossPhase}/{ArmKeys.Loss.ReorderRate}",
        $"{LossPhase}/{ArmKeys.Loss.ClientSendLossRate}",
        $"{LossPhase}/{ArmKeys.Loss.AchievedRate}",
    ];

    internal static readonly KindContract s_contract = new(
        "base",
        Outcome,
        new MetricsContract(
            Prefix: MetricsPrefix,
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
                new ArrayArity($"{ParametersPrefix}/{ArmKeys.Common.Parameters.Phases}", Phases),
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
                    PropertyCount: DeclaredKeys.PropertyCount(typeof(LossMetrics)),
                    NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(LossMetrics)),
                    OmittedWhen: ShapeFlags.None,
                    PublishesContainerKey: true,
                    Blocks: []),
            ],
            Dynamic: []),
        Parameters:
        [
            $"{ParametersPrefix}/{ArmKeys.Common.Parameters.Seconds}",
            $"{ParametersPrefix}/{ArmKeys.Common.Parameters.PhaseSeconds}",
            $"{ParametersPrefix}/{ArmKeys.Common.Parameters.Phases}",
            LatencyParameterPrefix,
            .. LatencyShape.s_parameterNames.Select(name => $"{LatencyParameterPrefix}/{name}"),
            LossParameterPrefix,
            .. LossShape.s_parameterNames.Select(name => $"{LossParameterPrefix}/{name}"),
        ]);

    /// <summary>
    /// The declared paths of this record in document order: the elapsed time, then each phase container
    /// ahead of the members the phase publishes inside it.
    /// </summary>
    private static List<string> Declared() =>
    [
        $"{MetricsPrefix}/{ArmKeys.Control.ElapsedSeconds}",
        LatencyPhase,
        .. DeclaredKeys.Under(typeof(ArmKeys.Latency), LatencyPhase),
        LossPhase,
        .. DeclaredKeys.Under(typeof(ArmKeys.Loss), LossPhase),
    ];

    /// <summary>
    /// The record a measured control run publishes: the latency record the latency kind's factory
    /// builds under the same flags, one level down, and a loss phase with its classification identity
    /// intact.
    /// </summary>
    private static ArmOutcome Outcome(ShapeFlags flags) => new()
    {
        Parameters = new ArmParameters
        {
            Seconds = 20.0,
            PhaseSeconds = 10.0,
            Phases = s_phases,
            Latency = LatencyShape.PhaseParameters(flags),
            Loss = LossShape.ParametersOf(),
        },
        Metrics = new ControlMetrics
        {
            ElapsedSeconds = 10.234,
            Latency = LatencyShape.Metrics(flags),
            Loss = LossShape.Metrics(flags),
        },
        Gates =
        {
            [ArmKeys.Common.Gates.ClientSendLoss] = 0,
            [ArmKeys.Common.Gates.WindowOverflow] = 0,
            [ArmKeys.Common.Gates.BacklogDrops] = 0,
            [ArmKeys.Common.Gates.SendFailures] = 0,
            [ArmKeys.Common.Gates.LaneShortfall] = 0,
            [ArmKeys.Common.Gates.ScheduleTruncated] = 0,
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
}
