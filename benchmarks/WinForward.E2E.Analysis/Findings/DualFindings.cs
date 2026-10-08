using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Findings;

/// <summary>One (pass, row) dual phase: both lanes' path-quality readings and the leak the truth file reports.</summary>
/// <param name="Pass">The pass the phase ran in.</param>
/// <param name="Row">The row that owns the phase.</param>
/// <param name="Leak">The direct lane's intercepted connection count, or null when it cannot be read.</param>
/// <param name="ShapeNotes">How the two lanes' declared workload shapes differ.</param>
/// <param name="ProxiedLatencyP50">The proxied lane's LAT tcp-rtt p50.</param>
/// <param name="DirectLatencyP50">The direct lane's LAT tcp-rtt p50.</param>
/// <param name="ProxiedLatencyP99">The proxied lane's LAT tcp-rtt p99.</param>
/// <param name="DirectLatencyP99">The direct lane's LAT tcp-rtt p99.</param>
/// <param name="ProxiedLossRate">The proxied lane's LOSS loss rate, in percentage points.</param>
/// <param name="DirectLossRate">The direct lane's LOSS loss rate, in percentage points.</param>
internal sealed record DualRecord(
    string Pass,
    string Row,
    double? Leak,
    IReadOnlyList<string> ShapeNotes,
    double? ProxiedLatencyP50,
    double? DirectLatencyP50,
    double? ProxiedLatencyP99,
    double? DirectLatencyP99,
    double? ProxiedLossRate,
    double? DirectLossRate);

/// <summary>One row's dual phase across passes, summarised for the interference findings.</summary>
/// <param name="Passes">How many passes ran the phase.</param>
/// <param name="Leak">The largest leak any pass reported.</param>
/// <param name="DirectLatencyP50">The direct lane's per-pass LAT tcp-rtt p50 values.</param>
/// <param name="ProxiedLatencyP50">The proxied lane's per-pass LAT tcp-rtt p50 values.</param>
/// <param name="DirectLoss">The direct lane's per-pass LOSS loss rates.</param>
/// <param name="ProxiedLoss">The proxied lane's per-pass LOSS loss rates.</param>
/// <param name="ShapeNotes">Every shape difference any pass reported.</param>
internal sealed record DualRowSummary(
    int Passes,
    double? Leak,
    IReadOnlyList<double> DirectLatencyP50,
    IReadOnlyList<double> ProxiedLatencyP50,
    IReadOnlyList<double> DirectLoss,
    IReadOnlyList<double> ProxiedLoss,
    IReadOnlyList<string> ShapeNotes);

/// <summary>
/// The dual phase, read as findings: the direct lane is a property of the path, so a row whose direct
/// lane is worse than the campaign's best direct lane is showing that the product interfered with traffic
/// it was configured to leave alone.
/// </summary>
/// <remarks>
/// <para><b>Two claims, one per path-quality metric.</b> Latency is compared as a ratio against the best
/// direct lane and loss as a difference in percentage points, each against the campaign's own
/// pre-declared threshold.</para>
/// <para><b>A shape difference is a caveat, not a failure.</b> When the two lanes do not declare the same
/// workload the comparison itself is in question, which is disclosed rather than silently dropped.</para>
/// </remarks>
internal static class DualFindings
{
    /// <summary>The parameters the two lanes must agree on for their readings to be comparable.</summary>
    private static readonly string[] s_shapeParameters =
    [
        "seconds",
        "ratePerSecond",
        "payloadBytes",
        "protocol",
        "lanes",
        "inFlightWindow",
        "lossWindowMs",
        "desktops",
        "connectionsPerSecond",
        "streams",
        "targetBytesPerSecond",
        "intervalMs",
        "idleSeconds",
    ];

    /// <summary>Every dual phase the campaign ran, pass by pass.</summary>
    internal static List<DualRecord> Records(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var records = new List<DualRecord>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                if (!CampaignQueries.IsDualRow(row.RunId))
                {
                    continue;
                }

                records.Add(new DualRecord(
                    passId,
                    row.RunId,
                    JsonValue.Number(row.DualTruth, "directLeak"),
                    ShapeNotes(row.DualProxied, row.DualDirect),
                    Latency(row.DualProxied),
                    Latency(row.DualDirect),
                    Latency(row.DualProxied, "p99Us"),
                    Latency(row.DualDirect, "p99Us"),
                    LossRate(row.DualProxied),
                    LossRate(row.DualDirect)));
            }
        }

        return records;
    }

    /// <summary>Every dual finding: path interference, a shape mismatch and an unreadable leak.</summary>
    internal static List<Finding> Collect(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var summary = Summarise(Records(campaign));
        return [.. Interference(summary), .. Caveats(summary)];
    }

    private static List<Finding> Interference(Dictionary<string, DualRowSummary> summary)
    {
        var outFindings = new List<Finding>();
        var directLatency = new Dictionary<string, double>(StringComparer.Ordinal);
        var directLoss = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (row, entry) in summary)
        {
            if (entry.DirectLatencyP50.Count > 0)
            {
                directLatency[row] = DescriptiveStats.Median(entry.DirectLatencyP50)!.Value;
            }

            if (entry.DirectLoss.Count > 0)
            {
                directLoss[row] = DescriptiveStats.Median(entry.DirectLoss)!.Value;
            }
        }

        if (directLatency.Count > 0)
        {
            var bestRow = directLatency.MinBy(entry => entry.Value).Key;
            var best = directLatency[bestRow];
            foreach (var row in directLatency.Keys.Order(StringComparer.Ordinal))
            {
                var value = directLatency[row];
                if (best > 0.0 && (value / best) - 1.0 > Thresholds.Latency)
                {
                    outFindings.Add(new Finding(
                        Severity.PathInterference,
                        "direct-lane-latency",
                        row,
                        $"the direct lane's LAT tcp-rtt p50 is {VerbatimNumber.Cell(value, 1)} us against {VerbatimNumber.Cell(best, 1)} us on {bestRow} ({VerbatimNumber.Fixed(100.0 * ((value / best) - 1.0), 1)} % worse): the direct lane is a property of the path, so the product is interfering with traffic it was configured to leave alone"));
                }
            }
        }

        if (directLoss.Count > 0)
        {
            var bestRow = directLoss.MinBy(entry => entry.Value).Key;
            var best = directLoss[bestRow];
            foreach (var row in directLoss.Keys.Order(StringComparer.Ordinal))
            {
                var value = directLoss[row];
                if (value - best > Thresholds.UdpLoss)
                {
                    outFindings.Add(new Finding(
                        Severity.PathInterference,
                        "direct-lane-loss",
                        row,
                        $"the direct lane's LOSS lossRate is {VerbatimNumber.Fixed(value, 4)} pp against {VerbatimNumber.Fixed(best, 4)} pp on {bestRow}: the direct lane's loss is the path's, so a product whose direct lane loses more is interfering"));
                }
            }
        }

        return outFindings;
    }

    private static List<Finding> Caveats(Dictionary<string, DualRowSummary> summary)
    {
        var outFindings = new List<Finding>();
        foreach (var row in summary.Keys.Order(StringComparer.Ordinal))
        {
            var entry = summary[row];
            if (entry.ShapeNotes.Count > 0)
            {
                outFindings.Add(new Finding(
                    Severity.MeasurementCaveat,
                    "dual-shape-mismatch",
                    row,
                    "the two dual lanes do not run the same declared workload shape: "
                    + string.Join("; ", entry.ShapeNotes.Order(StringComparer.Ordinal))));
            }

            if (entry.Leak is null)
            {
                outFindings.Add(new Finding(
                    Severity.MeasurementCaveat,
                    "direct-leak-unreadable",
                    row,
                    "no numeric directLeak is available for this row's dual phase"));
            }
        }

        return outFindings;
    }

    /// <summary>One row's dual phase across passes: the largest leak, both lanes' readings, and the shape notes.</summary>
    internal static Dictionary<string, DualRowSummary> Summarise(List<DualRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var summary = new Dictionary<string, DualRowSummary>(StringComparer.Ordinal);
        var shapeNotes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var directLatency = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var proxiedLatency = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var directLoss = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var proxiedLoss = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var leaks = new Dictionary<string, double>(StringComparer.Ordinal);
        var passes = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var record in records)
        {
            if (directLatency.TryAdd(record.Row, []))
            {
                order.Add(record.Row);
                shapeNotes[record.Row] = new HashSet<string>(StringComparer.Ordinal);
                proxiedLatency[record.Row] = [];
                directLoss[record.Row] = [];
                proxiedLoss[record.Row] = [];
            }

            passes[record.Row] = passes.GetValueOrDefault(record.Row) + 1;
            if (record.Leak is not null)
            {
                leaks[record.Row] = Math.Max(leaks.GetValueOrDefault(record.Row), record.Leak.Value);
            }

            Add(directLatency, record.Row, record.DirectLatencyP50);
            Add(proxiedLatency, record.Row, record.ProxiedLatencyP50);
            Add(directLoss, record.Row, record.DirectLossRate);
            Add(proxiedLoss, record.Row, record.ProxiedLossRate);
            foreach (var note in record.ShapeNotes)
            {
                shapeNotes[record.Row].Add(note);
            }
        }

        foreach (var row in order)
        {
            summary[row] = new DualRowSummary(
                passes[row],
                leaks.TryGetValue(row, out var leak) ? leak : null,
                directLatency[row],
                proxiedLatency[row],
                directLoss[row],
                proxiedLoss[row],
                [.. shapeNotes[row]]);
        }

        return summary;
    }

    private static void Add(Dictionary<string, List<double>> values, string row, double? value)
    {
        if (value is not null)
        {
            values[row].Add(value.Value);
        }
    }

    private static double? Latency(ClientRun? lane, string stat = "p50Us") =>
        lane is null ? null : ArmAccess.Latency(lane, "LAT", "tcp-rtt", stat).Value;

    private static double? LossRate(ClientRun? lane)
    {
        if (lane is null)
        {
            return null;
        }

        var value = ArmAccess.Number(lane, "LOSS", "metrics/lossRate").Value;
        return value * 100.0;
    }

    private static List<string> ShapeNotes(ClientRun? proxied, ClientRun? direct)
    {
        if (proxied is null || direct is null)
        {
            return ["a lane run directory is missing"];
        }

        var notes = new List<string>();
        foreach (var armName in proxied.Arms.SortedNames.Intersect(direct.Arms.SortedNames, StringComparer.Ordinal))
        {
            var (left, _) = ArmAccess.ArmResult(proxied, armName);
            var (right, _) = ArmAccess.ArmResult(direct, armName);
            if (left is null || right is null)
            {
                continue;
            }

            foreach (var key in s_shapeParameters)
            {
                var a = JsonValue.Dig(left, $"parameters/{key}");
                var b = JsonValue.Dig(right, $"parameters/{key}");
                if (!JsonText.Same(a, b))
                {
                    notes.Add($"{armName}.{key} differs ({JsonText.Of(a)} vs {JsonText.Of(b)})");
                }
            }
        }

        foreach (var name in proxied.Arms.SortedNames.Where(name => !direct.Arms.Contains(name)))
        {
            notes.Add($"arms only in the proxied lane: {name}");
        }

        foreach (var name in direct.Arms.SortedNames.Where(name => !proxied.Arms.Contains(name)))
        {
            notes.Add($"arms only in the direct lane: {name}");
        }

        return notes;
    }
}
