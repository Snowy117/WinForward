using System.Globalization;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Metrics;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Release;

/// <summary>One arm's published gate values for one pass and row, as the release matrix reads them.</summary>
/// <remarks>
/// <b>The values are the arm's own counters, kept nullable.</b> A null means the arm did not publish that
/// gate at all, which is different from a published zero: an arm kind that cannot defer work simply has
/// no <c>windowOverflow</c> counter, and that absence must not invalidate anything.
/// </remarks>
public sealed record ReleaseGateArm
{
    /// <summary>The pass the arm ran in.</summary>
    public required string Pass { get; init; }

    /// <summary>The row the arm belongs to.</summary>
    public required string RowId { get; init; }

    /// <summary>The arm name, for example <c>LAT</c>.</summary>
    public required string Arm { get; init; }

    /// <summary><c>gates.windowOverflow</c>, or null when the arm kind does not publish it.</summary>
    public double? WindowOverflow { get; init; }

    /// <summary><c>gates.clientSendLoss</c>, or null when the arm kind does not publish it.</summary>
    public double? ClientSendLoss { get; init; }

    /// <summary><c>gates.backlogDrops</c>, or null when the arm kind does not publish it.</summary>
    public double? BacklogDrops { get; init; }

    /// <summary><c>gates.scheduleTruncated</c>, or null when the arm kind does not publish it.</summary>
    public double? ScheduleTruncated { get; init; }

    /// <summary><c>gates.laneShortfall</c>, or null when the arm kind does not publish it.</summary>
    public double? LaneShortfall { get; init; }

    /// <summary>Whether the arm's UDP accounting identity does not hold.</summary>
    public bool UdpIdentityViolated { get; init; }

    /// <summary>Which identity terms disagreed, when <see cref="UdpIdentityViolated"/> is true.</summary>
    public string? UdpIdentityDetail { get; init; }

    /// <summary>Whether the arm's DNS partition identity does not hold.</summary>
    public bool DnsPartitionViolated { get; init; }

    /// <summary>Which DNS partition terms disagreed, when <see cref="DnsPartitionViolated"/> is true.</summary>
    public string? DnsPartitionDetail { get; init; }
}

/// <summary>One run's sampler failures for one pass and row.</summary>
/// <param name="Pass">The pass the run belongs to.</param>
/// <param name="RowId">The row the run belongs to.</param>
/// <param name="SamplerErrors">How many <c>samplerError</c> records the run published.</param>
/// <param name="RejectedSamples">How many sample records could not be read and were rejected.</param>
public sealed record ReleaseGateSampling(string Pass, string RowId, int SamplerErrors, int RejectedSamples);

/// <summary>One campaign-level gate result that qualifies the release without invalidating a cell.</summary>
/// <param name="Kind">The rule, for example <c>base-floor</c> or <c>control-drift</c>.</param>
/// <param name="Scope">What the caveat is about: a pass, or the campaign.</param>
/// <param name="Detail">The caveat in the report's words, with the numbers it is about.</param>
public sealed record ReleaseGateCaveat(string Kind, string Scope, string Detail);

/// <summary>One metric cell a gate invalidated: the pass, the arm and the rule that made it unmeasurable.</summary>
/// <param name="Metric">The metric identifier, for example <c>loss.lossRate</c>.</param>
/// <param name="RowId">The row whose cell was invalidated.</param>
/// <param name="Pass">The pass whose cell was invalidated.</param>
/// <param name="Arm">The arm whose gate failed.</param>
/// <param name="Gate">The gate name, for example <c>clientSendLoss</c>.</param>
/// <param name="Reason">The gate's value and why it makes the cell unmeasurable.</param>
public sealed record ReleaseGateInvalidation(
    string Metric,
    string RowId,
    string Pass,
    string Arm,
    string Gate,
    string Reason);

/// <summary>The gate facts one release run reads from its campaign, before any cell is mapped.</summary>
/// <remarks>
/// <b>This record is the matrix's input, not its result.</b> It carries no decision: every fact is a
/// counter the harness published, a per-record identity result or a campaign-level check, and the matrix
/// alone decides which metric cell each fact invalidates.
/// </remarks>
public sealed record ReleaseGateEvidence
{
    /// <summary>Every arm's gate values, one entry per measured arm window.</summary>
    public required IReadOnlyList<ReleaseGateArm> Arms { get; init; }

    /// <summary>The per-run sampler failures; a clean run is omitted.</summary>
    public IReadOnlyList<ReleaseGateSampling> Sampling { get; init; } = [];

    /// <summary>Whether any run's foreign-connection counter is above zero.</summary>
    public bool ForeignConnection { get; init; }

    /// <summary>What the foreign-connection finding says, when there is one.</summary>
    public string? ForeignConnectionDetail { get; init; }

    /// <summary>Whether any dual phase reported a direct-path leak.</summary>
    public bool DirectLeak { get; init; }

    /// <summary>What the direct-leak finding says, when there is one.</summary>
    public string? DirectLeakDetail { get; init; }

    /// <summary>Whether the two control blocks differ beyond their declared threshold.</summary>
    public bool ControlDrift { get; init; }

    /// <summary>What the control drift says, when it is above its threshold.</summary>
    public string? ControlDriftDetail { get; init; }

    /// <summary>Whether a pass's control block lost traffic, which is a path-quality caveat.</summary>
    public bool BaseFloorFailed { get; init; }

    /// <summary>Which pass and control block lost traffic, when one did.</summary>
    public string? BaseFloorDetail { get; init; }
}

/// <summary>The release gate matrix's result: the invalidated cells and the campaign-level caveats.</summary>
/// <remarks>
/// <b>The result is a lookup, not a list to be re-derived.</b> The comparison asks it whether one
/// <c>(metric, row, pass)</c> cell survived, and the writer prints the same entries; keeping one
/// construction avoids a verdict whose family size disagrees with the table's invalidations.
/// </remarks>
public sealed class ReleaseGateMatrixResult
{
    /// <summary>The result of an evidence set with no gate failure.</summary>
    internal static ReleaseGateMatrixResult Empty { get; } = new() { Invalidations = [], Caveats = [] };

    private Dictionary<string, Dictionary<string, List<ReleaseGateInvalidation>>>? _byMetric;

    /// <summary>Every invalidated cell, in the report's order.</summary>
    public required IReadOnlyList<ReleaseGateInvalidation> Invalidations { get; init; }

    /// <summary>Every campaign-level caveat the matrix produced.</summary>
    public required IReadOnlyList<ReleaseGateCaveat> Caveats { get; init; }

    /// <summary>Every invalidation for one metric and row, in pass order.</summary>
    /// <param name="metric">The metric identifier.</param>
    /// <param name="rowId">The row identifier.</param>
    /// <returns>The invalidations, or an empty list when the cell survived.</returns>
    internal IReadOnlyList<ReleaseGateInvalidation> For(string metric, string rowId)
    {
        ArgumentException.ThrowIfNullOrEmpty(metric);
        ArgumentException.ThrowIfNullOrEmpty(rowId);

        var index = _byMetric ??= BuildIndex();
        return index.TryGetValue(metric, out var rows) && rows.TryGetValue(rowId, out var entries)
            ? entries
            : [];
    }

    /// <summary>Whether one metric cell was invalidated by any gate.</summary>
    /// <param name="metric">The metric identifier.</param>
    /// <param name="rowId">The row identifier.</param>
    /// <param name="pass">The pass identifier.</param>
    /// <returns>True when the cell is unmeasurable.</returns>
    public bool IsInvalidated(string metric, string rowId, string pass)
    {
        ArgumentException.ThrowIfNullOrEmpty(pass);

        return For(metric, rowId).Any(entry => string.Equals(entry.Pass, pass, StringComparison.Ordinal));
    }

    private Dictionary<string, Dictionary<string, List<ReleaseGateInvalidation>>> BuildIndex()
    {
        var index = new Dictionary<string, Dictionary<string, List<ReleaseGateInvalidation>>>(StringComparer.Ordinal);
        foreach (var entry in Invalidations)
        {
            if (!index.TryGetValue(entry.Metric, out var rows))
            {
                rows = new Dictionary<string, List<ReleaseGateInvalidation>>(StringComparer.Ordinal);
                index[entry.Metric] = rows;
            }

            if (!rows.TryGetValue(entry.RowId, out var entries))
            {
                entries = [];
                rows[entry.RowId] = entries;
            }

            entries.Add(entry);
        }

        return index;
    }
}

/// <summary>
/// Maps one campaign's per-arm gate values onto the release metric cells they make unmeasurable.
/// </summary>
/// <remarks>
/// <para><b>A failed gate is a cell-level statement, not a campaign-level one.</b> The matrix reads each
/// arm's own counters and stops exactly the cells that counter disqualifies; it does not stop the row,
/// the pass or the metric. The campaign-level checks — control drift, the BASE path-loss floor, a
/// foreign connection and a direct leak — become caveats or findings and never invalidate every row,
/// which is why a dirty pass's control block qualifies the campaign instead of erasing its product cells.</para>
/// <para><b>The legacy tables are untouched.</b> <c>TableLatency</c> keeps its own window-overflow cell
/// and <c>GateValidity</c> keeps its own pass-level verdict; this matrix is the release path's separate
/// reading and no legacy writer consults it.</para>
/// <para><b>The scope each gate stops:</b></para>
/// <list type="bullet">
/// <item><description><c>windowOverflow</c> and <c>backlogDrops</c> stop the arm's latency cells.</description></item>
/// <item><description><c>clientSendLoss</c> stops the arm's latency and loss cells; REL and PERSIST publish neither, so their <c>clientSendLoss</c> stops no release cell.</description></item>
/// <item><description>a violated UDP accounting identity stops the arm's loss cells and the row's throughput cell; a violated DNS partition identity stops the DNS arm's latency and loss cells.</description></item>
/// <item><description><c>scheduleTruncated</c> stops the arm's latency cells and the row's throughput cell.</description></item>
/// <item><description><c>laneShortfall</c> stops every metric cell the arm serves.</description></item>
/// <item><description>a sampler failure or a rejected sample stops the row's CPU and memory cells, the only metrics read from the 1 Hz trace.</description></item>
/// </list>
/// </remarks>
public static class ReleaseGateMatrix
{
    /// <summary>The gate names, in the order the report prints them.</summary>
    private static readonly IReadOnlyList<string> s_gateOrder =
        ["windowOverflow", "clientSendLoss", "udp-identity", "dns-partition", "backlogDrops", "scheduleTruncated", "laneShortfall", "samplerError"];

    private const string LatencyFamily = "latency";

    private const string LossFamily = "udp-loss";

    private const string CpuFamily = "cpu";

    private const string MemoryFamily = "memory";

    /// <summary>Builds the invalidation list and the campaign-level caveats from one evidence set.</summary>
    /// <param name="evidence">The gate facts the campaign published.</param>
    /// <returns>Every invalidated cell and every caveat, in deterministic order.</returns>
    public static ReleaseGateMatrixResult Evaluate(ReleaseGateEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var invalidations = new List<ReleaseGateInvalidation>();
        var seen = new HashSet<(string Metric, string Row, string Pass, string Gate)>();
        foreach (var arm in evidence.Arms)
        {
            ApplyArm(arm, invalidations, seen);
        }

        foreach (var sampling in evidence.Sampling)
        {
            if (sampling.SamplerErrors > 0 || sampling.RejectedSamples > 0)
            {
                ApplySampling(sampling, invalidations, seen);
            }
        }

        Sort(invalidations);
        return new ReleaseGateMatrixResult
        {
            Invalidations = invalidations,
            Caveats = BuildCaveats(evidence),
        };
    }

    /// <summary>Whether one gate stops this metric for the arm that published it.</summary>
    /// <param name="gate">The gate name.</param>
    /// <param name="spec">The metric's catalogue declaration.</param>
    /// <param name="arm">The arm whose gate failed.</param>
    /// <returns>True when the cell is unmeasurable.</returns>
    private static bool Affects(string gate, MetricSpec spec, string arm)
    {
        var sameArm = string.Equals(spec.Arm, arm, StringComparison.Ordinal);
        var latency = string.Equals(spec.Family, LatencyFamily, StringComparison.Ordinal);
        var loss = string.Equals(spec.Family, LossFamily, StringComparison.Ordinal);
        var sampling = string.Equals(spec.Family, CpuFamily, StringComparison.Ordinal)
            || string.Equals(spec.Family, MemoryFamily, StringComparison.Ordinal);
        var throughput = string.Equals(spec.Unit, "Mbps", StringComparison.Ordinal);
        var dnsArm = string.Equals(spec.UdpPath, "dns", StringComparison.Ordinal);

        return gate switch
        {
            "windowOverflow" => sameArm && latency,
            "clientSendLoss" => sameArm && (latency || loss),
            "udp-identity" => (sameArm && loss) || throughput,
            "dns-partition" => sameArm && dnsArm && (latency || loss),
            "backlogDrops" => sameArm && latency,
            "scheduleTruncated" => (sameArm && latency) || throughput,
            "laneShortfall" => sameArm,
            "samplerError" => sampling,
            _ => false,
        };
    }

    private static void ApplyArm(
        ReleaseGateArm arm,
        List<ReleaseGateInvalidation> invalidations,
        HashSet<(string Metric, string Row, string Pass, string Gate)> seen)
    {
        foreach (var spec in MetricCatalogue.Specs)
        {
            if (arm.WindowOverflow is > 0.0 && Affects("windowOverflow", spec, arm.Arm))
            {
                Add(
                    invalidations, seen, spec, arm,
                    "windowOverflow",
                    $"{arm.Arm} gates.windowOverflow={Cell(arm.WindowOverflow)} > 0: requests past the in-flight ceiling were measured through the deferred queue, so the latency histogram is not a direct round-trip distribution");
            }

            if (arm.ClientSendLoss is > 0.0 && Affects("clientSendLoss", spec, arm.Arm))
            {
                Add(
                    invalidations, seen, spec, arm,
                    "clientSendLoss",
                    $"{arm.Arm} gates.clientSendLoss={Cell(arm.ClientSendLoss)} > 0: the client did not send part of the offered work, so the latency and loss cells are not over the offered population");
            }

            if (arm.UdpIdentityViolated && Affects("udp-identity", spec, arm.Arm))
            {
                Add(
                    invalidations, seen, spec, arm,
                    "udp-identity",
                    $"{arm.Arm} UDP accounting identity does not hold: {arm.UdpIdentityDetail ?? "the sent datagram count differs from its classification"}");
            }

            if (arm.DnsPartitionViolated && Affects("dns-partition", spec, arm.Arm))
            {
                Add(
                    invalidations, seen, spec, arm,
                    "dns-partition",
                    $"{arm.Arm} DNS partition identity does not hold: {arm.DnsPartitionDetail ?? "the answered and unanswered query partitions do not sum to the sent queries"}");
            }

            if (arm.BacklogDrops is > 0.0 && Affects("backlogDrops", spec, arm.Arm))
            {
                Add(
                    invalidations, seen, spec, arm,
                    "backlogDrops",
                    $"{arm.Arm} gates.backlogDrops={Cell(arm.BacklogDrops)} > 0: the bounded deferred queue discarded requests, so the latency tail is censored");
            }

            if (arm.ScheduleTruncated is > 0.0 && Affects("scheduleTruncated", spec, arm.Arm))
            {
                Add(
                    invalidations, seen, spec, arm,
                    "scheduleTruncated",
                    $"{arm.Arm} gates.scheduleTruncated={Cell(arm.ScheduleTruncated)} > 0: part of the offered schedule was never offered, so the cell measures a smaller schedule than its plan");
            }

            if (arm.LaneShortfall is > 0.0 && Affects("laneShortfall", spec, arm.Arm))
            {
                Add(
                    invalidations, seen, spec, arm,
                    "laneShortfall",
                    $"{arm.Arm} gates.laneShortfall={Cell(arm.LaneShortfall)} > 0: a planned lane supplied no traffic, so the arm measured less than its plan");
            }
        }
    }

    private static void ApplySampling(
        ReleaseGateSampling sampling,
        List<ReleaseGateInvalidation> invalidations,
        HashSet<(string Metric, string Row, string Pass, string Gate)> seen)
    {
        foreach (var spec in MetricCatalogue.Specs)
        {
            if (!Affects("samplerError", spec, string.Empty))
            {
                continue;
            }

            var key = (spec.Key, sampling.RowId, sampling.Pass, "samplerError");
            if (seen.Add(key))
            {
                invalidations.Add(new ReleaseGateInvalidation(
                    spec.Key,
                    sampling.RowId,
                    sampling.Pass,
                    string.Empty,
                    "samplerError",
                    $"{sampling.SamplerErrors.ToString(CultureInfo.InvariantCulture)} samplerError record(s) and {sampling.RejectedSamples.ToString(CultureInfo.InvariantCulture)} unreadable sample(s) in this pass: the 1 Hz CPU and memory trace has a sampling gap, so it cannot supply this cell"));
            }
        }
    }

    private static void Add(
        List<ReleaseGateInvalidation> invalidations,
        HashSet<(string Metric, string Row, string Pass, string Gate)> seen,
        MetricSpec spec,
        ReleaseGateArm arm,
        string gate,
        string reason)
    {
        if (seen.Add((spec.Key, arm.RowId, arm.Pass, gate)))
        {
            invalidations.Add(new ReleaseGateInvalidation(spec.Key, arm.RowId, arm.Pass, arm.Arm, gate, reason));
        }
    }

    private static List<ReleaseGateCaveat> BuildCaveats(ReleaseGateEvidence evidence)
    {
        var caveats = new List<ReleaseGateCaveat>();
        if (evidence.ControlDrift)
        {
            caveats.Add(new ReleaseGateCaveat(
                "control-drift",
                "campaign",
                evidence.ControlDriftDetail ?? "the two control blocks differ beyond their declared threshold; the control comparison is the caveat and no product cell is invalidated"));
        }

        if (evidence.BaseFloorFailed)
        {
            caveats.Add(new ReleaseGateCaveat(
                "base-floor",
                "campaign",
                evidence.BaseFloorDetail ?? "a control block lost traffic; this is a path-quality caveat for the campaign, not a reason to invalidate the product cells"));
        }

        if (evidence.ForeignConnection)
        {
            caveats.Add(new ReleaseGateCaveat(
                "foreign-connection",
                "campaign",
                evidence.ForeignConnectionDetail ?? "the product delivered datagrams belonging to one flow into another"));
        }

        if (evidence.DirectLeak)
        {
            caveats.Add(new ReleaseGateCaveat(
                "direct-leak",
                "campaign",
                evidence.DirectLeakDetail ?? "the product intercepted traffic its configuration sends direct"));
        }

        return caveats;
    }

    private static void Sort(List<ReleaseGateInvalidation> invalidations)
    {
        var metricOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < MetricCatalogue.Specs.Count; index++)
        {
            metricOrder[MetricCatalogue.Specs[index].Key] = index;
        }

        var gateOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < s_gateOrder.Count; index++)
        {
            gateOrder[s_gateOrder[index]] = index;
        }

        invalidations.Sort((left, right) =>
        {
            var order = NaturalKey.Compare(left.Pass, right.Pass);
            order = order != 0 ? order : string.CompareOrdinal(left.RowId, right.RowId);
            order = order != 0 ? order : metricOrder[left.Metric].CompareTo(metricOrder[right.Metric]);
            order = order != 0 ? order : gateOrder[left.Gate].CompareTo(gateOrder[right.Gate]);
            return order != 0 ? order : string.CompareOrdinal(left.Arm, right.Arm);
        });
    }

    private static string Cell(double? value) => value is { } number ? VerbatimNumber.Cell(number, 0) : "n/a";
}
