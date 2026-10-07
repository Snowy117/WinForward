using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Tests.Shapes;

/// <summary>
/// The <c>persistent</c> kind's shape contract: one flat metrics object whose reconnect, idle-window
/// and error counters all sit beside each other, and the three readings an empty population or
/// duration leaves unknown.
/// </summary>
internal static class PersistentShape
{
    /// <summary>How many notes the factory below publishes.</summary>
    private const int Notes = 2;

    private const string MetricsPrefix = "metrics";
    private const string Parameters = ArmKeys.Common.Record.Parameters;

    /// <summary>The parameter members the arm publishes, which is what the contract declares.</summary>
    private static readonly string[] s_parameterNames =
    [
        ArmKeys.Common.Parameters.Seconds,
        ArmKeys.Common.Parameters.IntervalMs,
        ArmKeys.Common.Parameters.IdleSeconds,
        ArmKeys.Common.Parameters.PayloadBytes,
        ArmKeys.Common.Parameters.ExpectedBytes,
        ArmKeys.Common.Parameters.ResponseTimeoutMs,
    ];

    private static readonly string[] s_nullable =
    [
        $"{MetricsPrefix}/{ArmKeys.Persistent.MeanConnectMs}",
        $"{MetricsPrefix}/{ArmKeys.Persistent.ResponseRate}",
        $"{MetricsPrefix}/{ArmKeys.Persistent.AchievedRate}",
    ];

    internal static readonly KindContract s_contract = new(
        "persistent",
        Outcome,
        new MetricsContract(
            Prefix: MetricsPrefix,
            PropertyCount: DeclaredKeys.PropertyCount(typeof(PersistentMetrics)),
            NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(PersistentMetrics)),
            Declared: DeclaredKeys.Under(typeof(ArmKeys.Persistent), MetricsPrefix),
            Nullable: s_nullable,
            Conditional: [],
            Arrays: [new ArrayArity(ArmKeys.Common.Record.Notes, Notes)],
            Blocks: [],
            Dynamic: []),
        Parameters: [.. s_parameterNames.Select(name => $"{Parameters}/{name}")]);

    /// <summary>
    /// The record a measured run publishes: the connection survived its idle window, every request it
    /// offered was answered, and the error counters are zero.
    /// </summary>
    private static ArmOutcome Outcome(ShapeFlags flags) => new()
    {
        Parameters = new ArmParameters
        {
            Seconds = 30.0,
            IntervalMs = 1_000,
            IdleSeconds = 20,
            PayloadBytes = 120,
            ExpectedBytes = 4_096,
            ResponseTimeoutMs = 2_000,
        },
        Metrics = Metrics(flags),
        Gates =
        {
            [ArmKeys.Common.Gates.ClientSendLoss] = 0,
            [ArmKeys.Common.Gates.WindowMs] = 0,
        },
        Notes =
        {
            "requests counts paced exchanges attempted and responses counts the echoes that completed them; the difference is explained by connectFailures, sendFailures, timeouts, remoteClosed and protocolErrors.",
            "survivedIdle is the connection live when the idle window opened completing the first request after it; it is false when that request had to reconnect, when it failed, and when no idle window fitted inside the arm.",
        },
    };

    private static PersistentMetrics Metrics(ShapeFlags flags)
    {
        var unknown = flags.HasFlag(ShapeFlags.UnknownReadings);
        return new PersistentMetrics
        {
            Requests = 30,
            Responses = 30,
            Reconnects = 0,
            SurvivedIdle = true,
            IdleSecondsScheduled = 20.0,
            IdleSecondsObserved = 20.001,
            SendWouldBlock = 0,
            SendFailures = 0,
            Timeouts = 0,
            RemoteClosed = 0,
            ProtocolErrors = 0,
            Corrupt = 0,
            UnmatchedReplies = 0,
            ConnectAttempts = 1,
            ConnectFailures = 0,
            MeanConnectMs = unknown ? null : 0.281,
            ResponseRate = unknown ? null : 1.0,
            AchievedRate = unknown ? null : 1.0,
        };
    }
}
