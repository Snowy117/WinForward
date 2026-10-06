using System.Globalization;
using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Tests;

/// <summary>The switches a kind's shape factory publishes under.</summary>
[Flags]
internal enum ShapeFlags
{
    /// <summary>Every reading is known and every block the arm can publish is published.</summary>
    None = 0,

    /// <summary>Readings with no measurement behind them publish as JSON null, not as a missing key.</summary>
    UnknownReadings = 1,

    /// <summary>The arm ran only the tcp half, so it publishes no <c>udp.*</c> key at all.</summary>
    TcpOnly = 2,

    /// <summary>The arm ran only the udp half, so it publishes no <c>tcp.*</c> key at all.</summary>
    UdpOnly = 4,

    /// <summary>The run observed no value for its data-driven containers: they are still written, empty.</summary>
    EmptyContainers = 8,
}

/// <summary>An array the kind publishes, and how many elements the measured record carries.</summary>
internal sealed record ArrayArity(string Path, int Length);

/// <summary>
/// One block of a metrics record: the keys it owns are the declared paths under <paramref name="Prefix"/>,
/// and its own record declares one property per key.
/// </summary>
/// <param name="Prefix">The declared path prefix of the block's keys, e.g. <c>metrics/tcp.</c>.</param>
/// <param name="PropertyCount">Public properties of the block's record, which must equal its key count.</param>
/// <param name="NullablePropertyCount">Nullable properties of the block's record, which must equal its registered null cases.</param>
/// <param name="OmittedWhen">
/// The flag state whose arm did not run this block, so the factory publishes none of its keys; <see cref="ShapeFlags.None"/>
/// when the arm always publishes it.
/// </param>
internal sealed record MetricsBlock(string Prefix, int PropertyCount, int NullablePropertyCount, ShapeFlags OmittedWhen);

/// <summary>
/// A declared object whose member names come from the run rather than from a constant.
/// </summary>
/// <param name="Path">The declared container path, e.g. <c>metrics/rcodes</c>.</param>
/// <param name="IsMemberName">Whether a member name published under that container is one the contract allows.</param>
/// <remarks>
/// The container's own path is declared by <c>ArmKeys</c>, so a container the writer forgets is still a
/// missing declared key; its members are checked by shape, because a name that is data cannot be
/// declared as a constant and would otherwise read as "written but not declared" on every run.
/// </remarks>
internal sealed record DynamicMembers(string Path, Func<string, bool> IsMemberName)
{
    /// <summary>True when <paramref name="path"/> is one member of this container.</summary>
    internal bool Covers(string path) => path.StartsWith($"{Path}/", StringComparison.Ordinal);

    /// <summary>The member name <paramref name="path"/> publishes under this container.</summary>
    internal string MemberOf(string path) => path[(Path.Length + 1)..];
}

/// <summary>
/// One metrics value object together with the <c>ArmKeys</c> subtree that declares its members, the
/// nullable readings that publish as null, the keys the record may omit, and its arrays.
/// </summary>
/// <param name="Prefix">The record member the keys live under, e.g. <c>metrics</c>.</param>
/// <param name="PropertyCount">
/// Public properties of the record, one of which holds each registered block (a block holder publishes the
/// block's keys rather than one key of its own), so <c>PropertyCount - Blocks.Count + Σ block.PropertyCount</c>
/// must equal <paramref name="Declared"/>'s size.
/// </param>
/// <param name="NullablePropertyCount">Nullable properties of the record, which must equal <paramref name="Nullable"/>'s size once the blocks' are added.</param>
/// <param name="Declared">The paths the key subtree declares, in declaration order.</param>
/// <param name="Nullable">Declared paths whose unknown value is written as JSON null.</param>
/// <param name="Conditional">Declared paths the record may omit when the arm publishes no such quantity.</param>
/// <param name="Arrays">Declared array paths and the measured arity of each.</param>
/// <param name="Blocks">The typed blocks the record holds, each with its own key prefix.</param>
/// <param name="Dynamic">The declared containers whose member names are data.</param>
internal sealed record MetricsContract(
    string Prefix,
    int PropertyCount,
    int NullablePropertyCount,
    IReadOnlyList<string> Declared,
    IReadOnlyList<string> Nullable,
    IReadOnlyList<string> Conditional,
    IReadOnlyList<ArrayArity> Arrays,
    IReadOnlyList<MetricsBlock> Blocks,
    IReadOnlyList<DynamicMembers> Dynamic);

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
    /// <summary>The tcp lanes the latency factory's record carries; the per-lane arrays have this arity.</summary>
    private const int LatencyLanes = 2;

    private const int LatencyNotes = 3;

    private const int DnsNotes = 2;

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
                Arrays: [new ArrayArity(ArmKeys.Common.Record.Notes, 1)],
                Blocks: [],
                Dynamic: [])),
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
                Arrays: [new ArrayArity(ArmKeys.Common.Record.Notes, 4)],
                Blocks: [],
                Dynamic: [])),
        new(
            "latency",
            static flags => new ArmOutcome
            {
                Parameters =
                {
                    [ArmKeys.Common.Parameters.Seconds] = 8.0,
                    [ArmKeys.Common.Parameters.RatePerSecond] = 20,
                    [ArmKeys.Common.Parameters.PayloadBytes] = 120,
                    [ArmKeys.Common.Parameters.Protocol] = ProtocolOf(flags),
                    [ArmKeys.Common.Parameters.Lanes] = LatencyLanes,
                    [ArmKeys.Common.Parameters.InFlightWindow] = 4096,
                },
                Metrics = new LatencyMetrics
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
                },
                Gates =
                {
                    [ArmKeys.Common.Gates.ClientSendLoss] = 0L,
                    [ArmKeys.Common.Gates.WindowOverflow] = 0L,
                    [ArmKeys.Common.Gates.BacklogDrops] = 0L,
                    [ArmKeys.Common.Gates.SendFailures] = 0L,
                    [ArmKeys.Common.Gates.LanesPlanned] = LatencyLanes,
                    [ArmKeys.Common.Gates.LanesStarted] = LatencyLanes,
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
            },
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
                    new ArrayArity($"metrics/{ArmKeys.Latency.TcpLaneSupplied}", LatencyLanes),
                    new ArrayArity($"metrics/{ArmKeys.Latency.TcpLaneSentOk}", LatencyLanes),
                    new ArrayArity(ArmKeys.Common.Record.Notes, LatencyNotes),
                ],
                Blocks:
                [
                    new MetricsBlock(
                        Prefix: "metrics/tcp.",
                        PropertyCount: DeclaredKeys.PropertyCount(typeof(LatencyTcpMetrics)),
                        NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(LatencyTcpMetrics)),
                        OmittedWhen: ShapeFlags.UdpOnly),
                    new MetricsBlock(
                        Prefix: "metrics/udp.",
                        PropertyCount: DeclaredKeys.PropertyCount(typeof(LatencyUdpMetrics)),
                        NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(LatencyUdpMetrics)),
                        OmittedWhen: ShapeFlags.TcpOnly),
                ],
                Dynamic: [])),
        new(
            "dns",
            static flags => new ArmOutcome
            {
                Parameters =
                {
                    [ArmKeys.Common.Parameters.Seconds] = 8.0,
                    [ArmKeys.Common.Parameters.RatePerSecond] = 50,
                    [ArmKeys.Common.Parameters.TcpPercent] = 20,
                    [ArmKeys.Common.Parameters.CnameEvery] = 0,
                    [ArmKeys.Common.Parameters.DnsPort] = 5301,
                    [ArmKeys.Common.Parameters.DrainWindowMs] = 1000,
                },
                Metrics = new DnsMetrics
                {
                    Sent = 400,
                    UdpSent = 320,
                    TcpSent = 80,
                    Unsent = 0,
                    UdpUnsent = 0,
                    TcpUnsent = 0,
                    Offered = 400,
                    Answered = 399,
                    Servfail = 0,
                    Timeout = 1,
                    Other = 0,
                    Unanswered = 1,
                    SocketErrors = 0,
                    EmptyAnswers = 0,
                    Malformed = 0,
                    Unmatched = 0,
                    AnswerRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 0.9975,
                    AchievedRate = flags.HasFlag(ShapeFlags.UnknownReadings) ? null : 49.875,
                    Rcodes = Counts(flags, [("0", 399)]),
                    QueryTypes = Counts(flags, [("A", 216), ("AAAA", 96), ("HTTPS", 80), ("TXT", 8)]),
                },
                Gates =
                {
                    [ArmKeys.Common.Gates.ClientSendLoss] = 0L,
                    [ArmKeys.Common.Gates.WindowMs] = 0L,
                },
                Notes =
                {
                    "every query written to the socket is terminal in exactly one of answered (rcode 0), servfail (rcode != 0), timeout (still unmatched when the drain window closed) or other (tcp only: a response consumed a queued query but carried a different transaction id).",
                    "queryTypes counts the queries actually written to the socket, per DNS type, so the mix is measured from the query the arm built rather than declared.",
                },
            },
            new MetricsContract(
                Prefix: "metrics",
                PropertyCount: DeclaredKeys.PropertyCount(typeof(DnsMetrics)),
                NullablePropertyCount: DeclaredKeys.NullablePropertyCount(typeof(DnsMetrics)),
                Declared: DeclaredKeys.Under(typeof(ArmKeys.Dns), "metrics"),
                Nullable:
                [
                    $"metrics/{ArmKeys.Dns.AnswerRate}",
                    $"metrics/{ArmKeys.Dns.AchievedRate}",
                ],
                Conditional: [],
                Arrays: [new ArrayArity(ArmKeys.Common.Record.Notes, DnsNotes)],
                Blocks: [],
                Dynamic:
                [
                    new DynamicMembers($"metrics/{ArmKeys.Dns.Rcodes}", IsRcodeName),
                    new DynamicMembers($"metrics/{ArmKeys.Dns.QueryTypes}", IsQueryTypeName),
                ])),
    ];

    /// <summary>The protocol a latency arm ran under the factory's flags.</summary>
    private static string ProtocolOf(ShapeFlags flags) => flags.HasFlag(ShapeFlags.UdpOnly)
        ? "udp"
        : flags.HasFlag(ShapeFlags.TcpOnly)
            ? "tcp"
            : "tcp+udp";

    /// <summary>
    /// The counts of a data-driven container, in the order the run observed them: an empty container is
    /// written empty rather than left out.
    /// </summary>
    private static Dictionary<string, long> Counts(ShapeFlags flags, (string Name, long Count)[] counts) =>
        flags.HasFlag(ShapeFlags.EmptyContainers)
            ? new Dictionary<string, long>(StringComparer.Ordinal)
            : counts.ToDictionary(static count => count.Name, static count => count.Count, StringComparer.Ordinal);

    /// <summary>An rcode member name: the decimal value of a 4-bit rcode.</summary>
    private static bool IsRcodeName(string member) =>
        int.TryParse(member, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is >= 0 and < 16;

    /// <summary>A query-type member name: one of the five the arm names, or <c>typeN</c> for any other type.</summary>
    private static bool IsQueryTypeName(string member) =>
        member is "A" or "AAAA" or "CNAME" or "HTTPS" or "TXT"
        || (member.StartsWith("type", StringComparison.Ordinal)
            && ushort.TryParse(member.AsSpan("type".Length), NumberStyles.None, CultureInfo.InvariantCulture, out _));
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
