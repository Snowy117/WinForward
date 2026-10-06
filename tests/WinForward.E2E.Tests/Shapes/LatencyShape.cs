using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Tests.Shapes;

/// <summary>
/// The <c>latency</c> kind's shape contract: two protocol blocks whose members are dotted into the
/// <c>metrics</c> object, so each block is omitted whole in the flag state that does not run it.
/// </summary>
internal static class LatencyShape
{
    /// <summary>The tcp lanes the factory's record carries; the per-lane arrays have this arity.</summary>
    internal const int Lanes = 2;

    /// <summary>How many notes the factory below publishes.</summary>
    private const int Notes = 3;

    internal static readonly KindContract s_contract = new(
        "latency",
        Outcome,
        new MetricsContract(
            Prefix: "metrics",
            PropertyCount: DeclaredKeys.PropertyCount(typeof(LatencyMetrics)),
            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(LatencyMetrics)),
            Declared: DeclaredKeys.Under(typeof(ArmKeys.Latency), "metrics"),

            // Every key of this kind belongs to one of the two protocol blocks, so every one of
            // them is omitted -- whole block, never one key -- when the arm ran the other protocol.
            Conditional: DeclaredKeys.Under(typeof(ArmKeys.Latency), "metrics"),
            Nullable:
            [
                $"metrics/{ArmKeys.Latency.TcpAchievedRate}",
                $"metrics/{ArmKeys.Latency.TcpMeanConnectMs}",
                $"metrics/{ArmKeys.Latency.UdpAchievedRate}",
                $"metrics/{ArmKeys.Latency.UdpLossRate}",
            ],
            Arrays:
            [
                new ArrayArity($"metrics/{ArmKeys.Latency.TcpLaneSupplied}", Lanes),
                new ArrayArity($"metrics/{ArmKeys.Latency.TcpLaneSentOk}", Lanes),
                new ArrayArity(ArmKeys.Common.Record.Notes, Notes),
            ],
            Blocks:
            [
                new MetricsBlock(
                    Prefix: "metrics/tcp.",
                    PropertyCount: DeclaredKeys.PropertyCount(typeof(LatencyTcpMetrics)),
                    NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(LatencyTcpMetrics)),
                    OmittedWhen: ShapeFlags.UdpOnly,
                    PublishesContainerKey: false,
                    Blocks: []),
                new MetricsBlock(
                    Prefix: "metrics/udp.",
                    PropertyCount: DeclaredKeys.PropertyCount(typeof(LatencyUdpMetrics)),
                    NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(LatencyUdpMetrics)),
                    OmittedWhen: ShapeFlags.TcpOnly,
                    PublishesContainerKey: false,
                    Blocks: []),
            ],
            Dynamic: []),
        Parameters: []);

    /// <summary>
    /// The record the latency arm publishes under <paramref name="flags"/>: also the control's latency
    /// phase, one level down, because both run the same arm.
    /// </summary>
    internal static LatencyMetrics Metrics(ShapeFlags flags) => new()
    {
        Tcp = flags.HasFlag(ShapeFlags.UdpOnly) ? null : new LatencyTcpMetrics
        {
            LaneStarted = 1,
            LaneSupplied = [161, 160],
            LaneSentOk = [161, 160],
            Supplied = 321,
            Sent = 321,
            SendWouldBlock = 0,
            WindowOverflow = 0,
            BacklogDrops = 0,
            SendFailures = 0,
            AbandonedAtTeardown = 0,
            ClientSendLoss = 0,
            Received = 321,
            OutstandingAtTeardown = 0,
            Corrupt = 0,
            ProtocolErrors = 0,
            RemoteClosed = 0,
            UnmatchedReplies = 0,
            WindowCeilingMs = 203_821.735,
            ScheduleTruncated = 0,
            AchievedRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 40.191,
            ConnectAttempts = 8,
            ConnectFailures = 0,
            MeanConnectMs = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 0.281,
        },
        Udp = flags.HasFlag(ShapeFlags.TcpOnly) ? null : new LatencyUdpMetrics
        {
            LaneStarted = 1,
            Supplied = 160,
            Sent = 160,
            SendWouldBlock = 0,
            WindowOverflow = 0,
            BacklogDrops = 0,
            SendFailures = 0,
            AbandonedAtTeardown = 0,
            ClientSendLoss = 0,
            Received = 160,
            Corrupt = 0,
            ProtocolErrors = 0,
            UnmatchedReplies = 0,
            ForeignConnection = 0,
            OutstandingAtTeardown = 0,
            WindowCeilingMs = 101_910.868,
            ScheduleTruncated = 0,
            LossRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 0.0,
            AchievedRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 20.095,
        },
    };

    /// <summary>
    /// The load the arm declares for this run, which is also what the control's
    /// <c>parameters/latency</c> object carries for its phase.
    /// </summary>
    internal static Dictionary<string, object?> PhaseParameters(ShapeFlags flags) => new(StringComparer.Ordinal)
    {
        [ArmKeys.Common.Parameters.Seconds] = 8.0,
        [ArmKeys.Common.Parameters.RatePerSecond] = 20,
        [ArmKeys.Common.Parameters.PayloadBytes] = 120,
        [ArmKeys.Common.Parameters.Protocol] = ShapeData.ProtocolOf(flags),
        [ArmKeys.Common.Parameters.Lanes] = Lanes,
        [ArmKeys.Common.Parameters.InFlightWindow] = 4096,
    };

    private static ArmOutcome Outcome(ShapeFlags flags)
    {
        var outcome = new ArmOutcome
        {
            Metrics = Metrics(flags),
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = 0L,
                [ArmKeys.Common.Gates.WindowOverflow] = 0L,
                [ArmKeys.Common.Gates.BacklogDrops] = 0L,
                [ArmKeys.Common.Gates.SendFailures] = 0L,
                [ArmKeys.Common.Gates.LanesPlanned] = Lanes,
                [ArmKeys.Common.Gates.LanesStarted] = Lanes,
                [ArmKeys.Common.Gates.LaneShortfall] = 0L,
                [ArmKeys.Common.Gates.ScheduleTruncated] = 0L,
                [ArmKeys.Common.Gates.InFlightCeilingMs] = 203_821.735,
                [ArmKeys.Common.Gates.WindowMs] = 0L,
            },
            Notes =
            {
                "latency is measured from each request's intended send instant, never from the actual send instant.",
                "a request offered at a full in-flight window is deferred, not dropped: it goes out as soon as a reply frees a slot, still stamped with its original intended instant, so a stalled product is published as an inflated sample instead of a missing one.",
                "tcp and udp counters are summed only after every lane has joined, so sent cannot exceed supplied however many lanes a plan asks for.",
            },
        };
        foreach (var parameter in PhaseParameters(flags))
        {
            outcome.Parameters.Add(parameter.Key, parameter.Value);
        }

        return outcome;
    }
}
