using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Tests;

/// <summary>The switches a kind's shape factory publishes under.</summary>
[Flags]
internal enum ShapeFlags
{
    /// <summary>Every reading is known, so nothing publishes as null.</summary>
    None = 0,

    /// <summary>Readings with no measurement behind them publish as JSON null, not as a missing key.</summary>
    UnknownReadings = 1,
}

/// <summary>An array the kind publishes, and how many elements the measured record carries.</summary>
internal sealed record ArrayArity(string Path, int Length);

/// <summary>
/// One metrics value object together with the <c>ArmKeys</c> subtree that declares its members, the
/// nullable readings that publish as null, the keys the record may omit, and its arrays.
/// </summary>
/// <param name="Prefix">The record member the keys live under, e.g. <c>metrics</c>.</param>
/// <param name="PropertyCount">Public properties of the record, which must equal <paramref name="Declared"/>'s size.</param>
/// <param name="NullablePropertyCount">Nullable properties of the record, which must equal <paramref name="Nullable"/>'s size.</param>
/// <param name="Declared">The paths the key subtree declares, in declaration order.</param>
/// <param name="Nullable">Declared paths whose unknown value is written as JSON null.</param>
/// <param name="Conditional">Declared paths the record may omit when the arm publishes no such quantity.</param>
/// <param name="Arrays">Declared array paths and the measured arity of each.</param>
internal sealed record MetricsContract(
    string Prefix,
    int PropertyCount,
    int NullablePropertyCount,
    IReadOnlyList<string> Declared,
    IReadOnlyList<string> Nullable,
    IReadOnlyList<string> Conditional,
    IReadOnlyList<ArrayArity> Arrays);

/// <summary>
/// One migrated kind: the explicit factory that builds the outcome the arm would publish, and the
/// keys that outcome is allowed to contain.
/// </summary>
/// <remarks>
/// A required property added to a metrics record breaks the factory at compile time, and the key and
/// property counts are asserted to match, so neither half of the pair can grow alone.
/// </remarks>
internal sealed record KindContract(
    string Kind,
    Func<ShapeFlags, ArmOutcome> Outcome,
    MetricsContract Metrics);

/// <summary>
/// The migrated kinds, one entry each. A kind is registered here when its arm publishes a typed
/// metrics record; the record-level parts every kind shares live in <see cref="RecordContract"/>.
/// </summary>
internal static class ContractRegistry
{
    internal static readonly KindContract[] s_all =
    [
        new(
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
                Arrays: [new ArrayArity(ArmKeys.Common.Record.Notes, 1)])),
        new(
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
                Arrays: [new ArrayArity(ArmKeys.Common.Record.Notes, 4)])),
    ];
}

/// <summary>The parts of a result record every kind shares, declared once in <c>ArmKeys.Common</c>.</summary>
internal static class RecordContract
{
    internal static List<string> Skeleton => DeclaredKeys.Under(typeof(ArmKeys.Common.Record), string.Empty);

    internal static List<string> Gates => DeclaredKeys.Under(typeof(ArmKeys.Common.Gates), ArmKeys.Common.Record.Gates);

    internal static List<string> Parameters => DeclaredKeys.Under(typeof(ArmKeys.Common.Parameters), ArmKeys.Common.Record.Parameters);

    /// <summary>
    /// The declared paths of the histogram named <paramref name="name"/>: the histogram itself, and
    /// the eight leaves every histogram carries. The leaves are one nested class rather than four
    /// copies, so the path of a leaf is this composition and not the class name of
    /// <c>ArmKeys.Common.LatencyRecord.Histogram</c>.
    /// </summary>
    private static List<string> Histogram(string name)
    {
        var histogram = $"{ArmKeys.Common.Record.Latency}/{name}";
        return [histogram, .. DeclaredKeys.Under(typeof(ArmKeys.Common.LatencyRecord.Histogram), histogram)];
    }

    /// <summary>The declared paths of the histograms that hold at least one sample.</summary>
    internal static List<string> Histograms(params string[] names) => [.. names.SelectMany(Histogram)];
}
