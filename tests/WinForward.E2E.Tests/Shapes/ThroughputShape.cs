using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Tests.Shapes;

/// <summary>The <c>throughput</c> kind's shape contract: one flat metrics object and four notes.</summary>
internal static class ThroughputShape
{
    /// <summary>How many notes the factory below publishes.</summary>
    private const int Notes = 4;

    internal static readonly KindContract s_contract = new(
        "throughput",
        static flags => new ArmOutcome
        {
            Parameters =
            {
                [ArmKeys.Common.Parameters.Seconds] = 8.0,
                [ArmKeys.Common.Parameters.Streams] = 2,
                [ArmKeys.Common.Parameters.TargetBytesPerSecond] = 20_000_000L,
                [ArmKeys.Common.Parameters.FramePayloadBytes] = 32_768,
            },
            Metrics = new ThroughputMetrics
            {
                Bytes = 159_965_600,
                BytesSent = 159_998_400,
                Frames = 4_877,
                FramesSent = 4_878,
                FramesEchoed = 4_877,
                StreamConnects = 2,
                ConnectFailures = 0,
                SendFailures = 0,
                BudgetBytes = 160_000_000,
                BudgetReached = true,
                BudgetRemainingBytes = 1_600,
                ElapsedSeconds = 8.0014,
                GoodputBps = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 19_992_324.386,
                GoodputMbps = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 159.9386,
                PerStreamMinBytes = 79_966_400,
                PerStreamMaxBytes = 79_999_200,
                Corrupt = 0,
                ProtocolErrors = 0,
                TargetBytesPerSecond = 20_000_000,
            },
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = 0L,
                [ArmKeys.Common.Gates.WindowMs] = 0L,
            },
            Notes =
            {
                "bytes counts echoed frame bytes that the client read back; the echo path is the measured transfer, so ingress and egress are both exercised.",
                "the aggregate send rate is paced to targetBytesPerSecond so per-stream counters stay comparable across proxifiers.",
                "streamConnects, connectFailures and sendFailures are the stream counters behind bytesSent and bytes: a run where no stream connected publishes framesSent = 0 with connectFailures = streams, so an arm that transferred nothing says which end failed instead of reading as an arm that offered nothing.",
                "budgetReached is true only when a stream was refused a frame because fewer than framePayloadBytes remained of budgetBytes, i.e. the run ended because the budget was spent rather than because seconds elapsed; a frame is never part-budgeted, so bytesSent stays below budgetBytes and budgetRemainingBytes is that unused remainder.",
            },
        },
        new MetricsContract(
            Prefix: "metrics",
            PropertyCount: DeclaredKeys.PropertyCount(typeof(ThroughputMetrics)),
            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(ThroughputMetrics)),
            Declared: DeclaredKeys.Under(typeof(ArmKeys.Throughput), "metrics"),
            Nullable:
            [
                $"metrics/{ArmKeys.Throughput.GoodputBps}",
                $"metrics/{ArmKeys.Throughput.GoodputMbps}",
            ],
            Conditional: [],
            Arrays: [new ArrayArity(ArmKeys.Common.Record.Notes, Notes)],
            Blocks: [],
            Dynamic: []),
        Parameters: []);
}
