using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// The dual-phase detail: per row, the two lanes' LAT and LOSS readings, how far the direct lane sits
/// from the campaign's best direct lane, and the one-sentence verdict on whether the product interfered.
/// </summary>
/// <remarks>
/// <para><b>The direct lane is a property of the path.</b> It leaves the product alone, so its latency and
/// loss are what the path itself does; a row whose direct lane is worse than another row's is showing
/// interference rather than a slow product, and a non-zero <c>directLeak</c> is a correctness failure
/// reported in the same words the summary section uses.</para>
/// <para><b>A row that never ran the phase says why.</b> A plan without a dual phase and a plan whose
/// phase left no directory are two different absences and the reason column spells each one out.</para>
/// </remarks>
internal static class TableDual
{
    private const string NoPhase = "n/a (no dual phase: the row's plan is ";

    private const string NoDirectory = "n/a (the plan declares a dual phase but no dual/ directory was found)";

    private const string Match = "declared parameters match";

    private const string WithinBand = "no leak observed; direct lane within the pre-declared band of the best row";

    private static readonly string[] s_headers =
    [
        "row",
        "passes",
        "directLeak",
        "proxied LAT tcp-rtt p50",
        "direct LAT tcp-rtt p50",
        "direct / best - 1",
        "proxied LOSS lossRate",
        "direct LOSS lossRate",
        "direct - best (pp)",
        "shape",
        "verdict",
    ];

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    /// <param name="campaign">The loaded campaign every lane is read from.</param>
    /// <returns>The body's text.</returns>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var summary = DualFindings.Summarise(DualFindings.Records(campaign));
        var directLatency = Medians(summary, entry => entry.DirectLatencyP50);
        var directLoss = Medians(summary, entry => entry.DirectLoss);
        var bestLatency = Best(directLatency);
        var bestLoss = Best(directLoss);
        var rows = new List<IReadOnlyList<string>>(campaign.RowIds.Count);
        foreach (var rowId in campaign.RowIds)
        {
            rows.Add(Line(rowId, summary, directLatency, directLoss, bestLatency, bestLoss));
        }

        var lines = new List<string>
        {
            "Each full-plan row that supports it runs a second phase in `dual/`: a `proxied` lane and a `direct` "
            + "lane execute the same workload shape against two targets, with only the path differing. The direct "
            + "lane's latency and loss are a property of the *path*, not of the product, so a row whose direct lane "
            + "is worse than another's is showing interference; `directLeak > 0` is a correctness failure and is "
            + "reported in section 0.1. Cells are `median [p25–p75] across passes (n=K)`; a row that did not run the "
            + "phase prints `not measured in this row`.",
            string.Empty,
            MarkdownTable.Render(s_headers, rows),
            string.Empty,
        };

        return string.Join('\n', lines);
    }

    /// <summary>One row's line, or the reason its plan never ran the phase.</summary>
    private static List<string> Line(
        string rowId,
        Dictionary<string, DualRowSummary> summary,
        Dictionary<string, double> directLatency,
        Dictionary<string, double> directLoss,
        double? bestLatency,
        double? bestLoss)
    {
        var profile = RowProfiles.Find(rowId);
        if (profile is null || !profile.Dual)
        {
            var plan = profile is null ? "unknown" : profile.Plan;
            return [rowId, "0", NoPhase + plan + ")", .. Unavailable()];
        }

        if (!summary.TryGetValue(rowId, out var entry))
        {
            return [rowId, "0", NoDirectory, .. Unavailable()];
        }

        double? latency = directLatency.TryGetValue(rowId, out var latencyValue) ? latencyValue : null;
        double? loss = directLoss.TryGetValue(rowId, out var lossValue) ? lossValue : null;
        double? excess = latency is not null && bestLatency is not null && !bestLatency.Value.Equals(0.0)
            ? (latency.Value / bestLatency.Value) - 1.0
            : null;
        double? excessLoss = loss is not null && bestLoss is not null ? loss.Value - bestLoss.Value : null;
        return
        [
            rowId,
            entry.Passes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            VerbatimNumber.Cell(entry.Leak, 0),
            Stat(entry.ProxiedLatencyP50, digits: 1, unit: " us"),
            Stat(entry.DirectLatencyP50, digits: 1, unit: " us"),
            excess is { } excessValue ? VerbatimNumber.Cell(excessValue * 100.0, 2, " %") : "n/a",
            Stat(entry.ProxiedLoss, digits: 4, unit: " pp"),
            Stat(entry.DirectLoss, digits: 4, unit: " pp"),
            VerbatimNumber.Cell(excessLoss, 4),
            entry.ShapeNotes.Count == 0 ? Match : string.Join("; ", entry.ShapeNotes.Order(StringComparer.Ordinal)),
            Verdict(entry, excess, excessLoss),
        ];
    }

    /// <summary>One lane's per-pass values as the reference renders a cell, or <c>n/a</c> when there are none.</summary>
    private static string Stat(IReadOnlyList<double> values, int digits, string unit) =>
        values.Count > 0 ? DescriptiveStats.FmtStat(values, digits, unit) : "n/a";

    /// <summary>The one sentence the row's direct lane gets.</summary>
    private static string Verdict(DualRowSummary entry, double? excess, double? excessLoss)
    {
        if (entry.Leak is { } leak && leak.CompareTo(0.0) != 0)
        {
            return $"CORRECTNESS FAILURE: directLeak={VerbatimNumber.Cell(leak, 0)}";
        }

        if (excess > Thresholds.Latency)
        {
            return "PATH INTERFERENCE: the direct lane is "
                + $"{VerbatimNumber.Fixed(100.0 * excess.Value, 1)} % slower than the best direct lane";
        }

        return excessLoss > Thresholds.UdpLoss
            ? "PATH INTERFERENCE: the direct lane loses "
                + $"{VerbatimNumber.Fixed(excessLoss.Value, 4)} pp more than the best direct lane"
            : WithinBand;
    }

    private static string[] Unavailable() =>
        [.. Enumerable.Repeat("n/a", s_headers.Length - 3)];

    private static Dictionary<string, double> Medians(
        Dictionary<string, DualRowSummary> summary,
        Func<DualRowSummary, IReadOnlyList<double>> values)
    {
        var medians = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (row, entry) in summary)
        {
            var readings = values(entry);
            if (readings.Count > 0)
            {
                medians[row] = DescriptiveStats.Median(readings)!.Value;
            }
        }

        return medians;
    }

    private static double? Best(Dictionary<string, double> medians) =>
        medians.Count == 0 ? null : medians.Values.Min();
}
