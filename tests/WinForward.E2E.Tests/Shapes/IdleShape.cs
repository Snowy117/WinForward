using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Tests.Shapes;

/// <summary>The <c>idle</c> kind's shape contract: one reading, and the record's notes array.</summary>
internal static class IdleShape
{
    /// <summary>How many notes the factory below publishes.</summary>
    private const int Notes = 1;

    internal static readonly KindContract s_contract = new(
        "idle",
        static _ => new ArmOutcome
        {
            Parameters =
            {
                [ArmKeys.Common.Parameters.Seconds] = 5.0,
                [ArmKeys.Common.Parameters.Traffic] = "none",
            },
            Metrics = new IdleMetrics { ElapsedSeconds = 5.001 },
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = 0L,
                [ArmKeys.Common.Gates.WindowMs] = 0L,
            },
            Notes = { "no traffic is generated; only the 1 Hz resource samples attached to this arm carry information." },
        },
        new MetricsContract(
            Prefix: "metrics",
            PropertyCount: DeclaredKeys.PropertyCount(typeof(IdleMetrics)),
            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(IdleMetrics)),
            Declared: DeclaredKeys.Under(typeof(ArmKeys.Idle), "metrics"),
            Nullable: [],
            Conditional: [],
            Arrays: [new ArrayArity(ArmKeys.Common.Record.Notes, Notes)],
            Blocks: [],
            Dynamic: []),
        Parameters: []);
}
