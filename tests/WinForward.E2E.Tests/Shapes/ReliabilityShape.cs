using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Tests.Shapes;

/// <summary>
/// The <c>reliability</c> kind's shape contract: two outcome distributions, the arm-wide counters,
/// and the joint mode x observed breakdown whose members are the modes the schedule ran.
/// </summary>
/// <remarks>
/// The factory models the run whose plan schedules all four modes of the arm's default mix, so
/// <c>byMode</c> is declared with those four blocks: the member set of a real run follows its own
/// plan, while every block's schema is the same one this contract pins. A test asserts the declared
/// mode names still agree with <see cref="TcpCommand.Name(TcpMode)"/>.
/// </remarks>
internal static class ReliabilityShape
{
    /// <summary>How many notes the factory below publishes.</summary>
    private const int Notes = 2;

    private const string MetricsPrefix = "metrics";
    private const string Parameters = ArmKeys.Common.Record.Parameters;

    /// <summary>The mode mix the factory's plan declares, which the record republishes after its own defaults.</summary>
    private const string ModeMixText = "clean=25,resetAfterN=25,partialFin=25,halfClose=25";

    /// <summary>The modes the factory's schedule ran, in schedule order, and their contract names.</summary>
    internal static readonly (TcpMode Mode, string Name)[] s_modes =
    [
        (TcpMode.Clean, ArmKeys.Reliability.ModeNames.Clean),
        (TcpMode.ResetAfterN, ArmKeys.Reliability.ModeNames.ResetAfterN),
        (TcpMode.PartialFin, ArmKeys.Reliability.ModeNames.PartialFin),
        (TcpMode.HalfClose, ArmKeys.Reliability.ModeNames.HalfClose),
    ];

    private static readonly string[] s_nullable =
    [
        $"{MetricsPrefix}/{ArmKeys.Reliability.FidelityRate}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.MeanConnectMs}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.MeanTransferMs}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.AchievedRate}",
        .. s_modes.SelectMany(mode => new[]
        {
            $"{ModePrefix(mode.Name)}/{ArmKeys.Reliability.Mode.MinEchoedBytes}",
            $"{ModePrefix(mode.Name)}/{ArmKeys.Reliability.Mode.MaxEchoedBytes}",
            $"{ModePrefix(mode.Name)}/{ArmKeys.Reliability.Mode.MinTrailerBytes}",
            $"{ModePrefix(mode.Name)}/{ArmKeys.Reliability.Mode.MaxTrailerBytes}",
        }),
    ];

    /// <summary>The parameter members the arm publishes, which is what the contract declares.</summary>
    private static readonly string[] s_parameterNames =
    [
        ArmKeys.Common.Parameters.Seconds,
        ArmKeys.Common.Parameters.ConnectionsPerSecond,
        ArmKeys.Common.Parameters.ExpectedBytes,
        ArmKeys.Common.Parameters.ModeMix,
        ArmKeys.Common.Parameters.FramePayloadBytes,
    ];

    internal static readonly KindContract s_contract = new(
        "reliability",
        Outcome,
        new MetricsContract(
            Prefix: MetricsPrefix,
            PropertyCount: DeclaredKeys.PropertyCount(typeof(ReliabilityMetrics)),
            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(ReliabilityMetrics)),
            Declared: Declared(),
            Nullable: s_nullable,
            Conditional: [],
            Arrays: [new ArrayArity(ArmKeys.Common.Record.Notes, Notes)],
            Blocks:
            [
                new MetricsBlock(
                    Prefix: $"{MetricsPrefix}/{ArmKeys.Reliability.Outcomes}/",
                    PropertyCount: DeclaredKeys.PropertyCount(typeof(ReliabilityOutcomes)),
                    NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(ReliabilityOutcomes)),
                    OmittedWhen: ShapeFlags.None,
                    PublishesContainerKey: true,
                    Blocks: []),
                new MetricsBlock(
                    Prefix: $"{MetricsPrefix}/{ArmKeys.Reliability.Expected}/",
                    PropertyCount: DeclaredKeys.PropertyCount(typeof(ReliabilityOutcomes)),
                    NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(ReliabilityOutcomes)),
                    OmittedWhen: ShapeFlags.None,
                    PublishesContainerKey: true,
                    Blocks: []),

                // The holder of the mode blocks is the byMode object itself, whose members are the
                // schedule's: it declares one per mode the factory's plan scheduled.
                new MetricsBlock(
                    Prefix: $"{MetricsPrefix}/{ArmKeys.Reliability.ByMode}/",
                    PropertyCount: s_modes.Length,
                    NullablePropertyCount: 0,
                    OmittedWhen: ShapeFlags.None,
                    PublishesContainerKey: true,
                    Blocks: [.. s_modes.Select(ModeBlock)]),
            ],
            Dynamic: []),
        Parameters: [.. s_parameterNames.Select(name => $"{Parameters}/{name}")]);

    /// <summary>
    /// The declared paths of this record in document order: each distribution's container ahead of
    /// its members, and the mode blocks in schedule order.
    /// </summary>
    private static List<string> Declared() =>
    [
        $"{MetricsPrefix}/{ArmKeys.Reliability.ConnectAttempts}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.ScheduledAttempts}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.Outcomes}",
        .. DeclaredKeys.Under(typeof(ArmKeys.Reliability.OutcomeNames), $"{MetricsPrefix}/{ArmKeys.Reliability.Outcomes}"),
        $"{MetricsPrefix}/{ArmKeys.Reliability.Expected}",
        .. DeclaredKeys.Under(typeof(ArmKeys.Reliability.OutcomeNames), $"{MetricsPrefix}/{ArmKeys.Reliability.Expected}"),
        $"{MetricsPrefix}/{ArmKeys.Reliability.UnexpectedEof}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.ExpectedEarlyEof}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.Truncated}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.FidelityMismatch}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.FidelityRate}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.ConnectFail}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.ExpectedBytes}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.ModeSchedule}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.EchoedBytes}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.TrailerBytes}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.ByMode}",
        .. s_modes.SelectMany(mode => ModeMembers(mode.Name)),
        $"{MetricsPrefix}/{ArmKeys.Reliability.AttemptRecords}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.AttemptRecordsOmitted}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.MeanConnectMs}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.MeanTransferMs}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.AchievedRate}",
        $"{MetricsPrefix}/{ArmKeys.Reliability.EffectiveModeMix}",
    ];

    /// <summary>The declared paths of one mode block, its container path first.</summary>
    private static List<string> ModeMembers(string mode) =>
    [
        ModePrefix(mode),
        $"{ModePrefix(mode)}/{ArmKeys.Reliability.Mode.Attempts}",
        $"{ModePrefix(mode)}/{ArmKeys.Reliability.Mode.Observed}",
        .. DeclaredKeys.Under(typeof(ArmKeys.Reliability.OutcomeNames), $"{ModePrefix(mode)}/{ArmKeys.Reliability.Mode.Observed}"),
        $"{ModePrefix(mode)}/{ArmKeys.Reliability.Mode.Truncated}",
        $"{ModePrefix(mode)}/{ArmKeys.Reliability.Mode.EchoedBytes}",
        $"{ModePrefix(mode)}/{ArmKeys.Reliability.Mode.TrailerBytes}",
        $"{ModePrefix(mode)}/{ArmKeys.Reliability.Mode.MinEchoedBytes}",
        $"{ModePrefix(mode)}/{ArmKeys.Reliability.Mode.MaxEchoedBytes}",
        $"{ModePrefix(mode)}/{ArmKeys.Reliability.Mode.MinTrailerBytes}",
        $"{ModePrefix(mode)}/{ArmKeys.Reliability.Mode.MaxTrailerBytes}",
    ];

    private static MetricsBlock ModeBlock((TcpMode Mode, string Name) mode) => new(
        Prefix: $"{ModePrefix(mode.Name)}/",
        PropertyCount: DeclaredKeys.PropertyCount(typeof(ReliabilityModeMetrics)),
        NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(ReliabilityModeMetrics)),
        OmittedWhen: ShapeFlags.None,
        PublishesContainerKey: true,
        Blocks:
        [
            new MetricsBlock(
                Prefix: $"{ModePrefix(mode.Name)}/{ArmKeys.Reliability.Mode.Observed}/",
                PropertyCount: DeclaredKeys.PropertyCount(typeof(ReliabilityOutcomes)),
                NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(ReliabilityOutcomes)),
                OmittedWhen: ShapeFlags.None,
                PublishesContainerKey: true,
                Blocks: []),
        ]);

    private static string ModePrefix(string mode) => $"{MetricsPrefix}/{ArmKeys.Reliability.ByMode}/{mode}";

    /// <summary>
    /// The record the factory's measured run publishes: every mode produced attempts, and the two
    /// distributions are the marginals of the mode blocks beneath them.
    /// </summary>
    private static ArmOutcome Outcome(ShapeFlags flags) => new()
    {
        Parameters = new ArmParameters
        {
            Seconds = 10.0,
            ConnectionsPerSecond = 10,
            ExpectedBytes = 8_192,
            ModeMix = ModeMixText,
            FramePayloadBytes = 1_024,
        },
        Metrics = Metrics(flags),
        Gates =
        {
            [ArmKeys.Common.Gates.ClientSendLoss] = 0,
            [ArmKeys.Common.Gates.WindowMs] = 0,
        },
        Notes =
        {
            "outcome values are what the client observed; 'expected' is what the requested mode calls for, and fidelityMismatch counts any divergence plus truncated echoes.",
            "byMode is the joint mode x observed distribution: outcomes gives the marginals, and only byMode says which mode produced them.",
        },
    };

    /// <summary>
    /// The record a measured run publishes. The arm-wide counters are the sums of the four mode
    /// blocks, so the fixture reads as one run rather than as numbers that only have to differ.
    /// </summary>
    private static ReliabilityMetrics Metrics(ShapeFlags flags)
    {
        var unknown = flags.HasFlag(ShapeFlags.UnknownReadings);
        return new ReliabilityMetrics
        {
            ConnectAttempts = 100,
            ScheduledAttempts = 100,
            Outcomes = new ReliabilityOutcomes
            {
                Clean = 72,
                Reset = 25,
                UnexpectedEof = 2,
                Timeout = 0,
                ConnectFail = 0,
                HalfCloseViolation = 1,
                OtherError = 0,
            },
            Expected = new ReliabilityOutcomes
            {
                Clean = 74,
                Reset = 25,
                UnexpectedEof = 1,
                Timeout = 0,
                ConnectFail = 0,
                HalfCloseViolation = 0,
                OtherError = 0,
            },
            UnexpectedEof = 1,
            ExpectedEarlyEof = 1,
            Truncated = 2,
            FidelityMismatch = 1,
            FidelityRate = unknown ? null : 0.01,
            ConnectFail = 0,
            ExpectedBytes = 8_192,
            ModeSchedule = string.Join(',', s_modes.Select(static mode => mode.Name)),
            EchoedBytes = 812_646,
            TrailerBytes = 100,
            ByMode = s_modes.ToDictionary(
                static mode => mode.Name,
                mode => Mode(mode.Mode, unknown),
                StringComparer.Ordinal),
            AttemptRecords = 12,
            AttemptRecordsOmitted = 0,
            MeanConnectMs = unknown ? null : 0.281,
            MeanTransferMs = unknown ? null : 0.402,
            AchievedRate = unknown ? null : 10.0,
            EffectiveModeMix = ModeMixText,
        };
    }

    /// <summary>One mode's block: its tally, its observed distribution and its byte extremes.</summary>
    private static ReliabilityModeMetrics Mode(TcpMode mode, bool unknown)
    {
        const int attempts = 25;
        return new ReliabilityModeMetrics
        {
            Attempts = attempts,
            Observed = new ReliabilityOutcomes
            {
                Clean = mode is TcpMode.Clean ? 25 : 0,
                Reset = mode is TcpMode.ResetAfterN ? 25 : 0,
                UnexpectedEof = mode is TcpMode.PartialFin ? 1 : 0,
                Timeout = 0,
                ConnectFail = 0,
                HalfCloseViolation = mode is TcpMode.HalfClose ? 1 : 0,
                OtherError = mode is TcpMode.HalfClose ? 24 : 0,
            },
            Truncated = mode is TcpMode.PartialFin ? 2 : 0,
            EchoedBytes = attempts * 8_192L,
            TrailerBytes = attempts,
            MinEchoedBytes = unknown ? null : 8_192,
            MaxEchoedBytes = unknown ? null : 8_192,
            MinTrailerBytes = unknown ? null : 1,
            MaxTrailerBytes = unknown ? null : 1,
        };
    }
}
