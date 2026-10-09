using WinForward.E2E.Analysis.Checks;
using WinForward.E2E.Analysis.Metrics;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// §5, "Latency detail": the harness's own histograms, one sub-table per latency class, one row per
/// (row, arm) pair the campaign actually ran.
/// </summary>
/// <remarks>
/// <para><b>The percentiles are read, never recomputed.</b> Each cell is one histogram statistic per
/// pass, aggregated across passes; the <c>count</c> column is the median histogram count, so a reader
/// can see how many observations back each percentile.</para>
/// <para><b>A row that cannot carry the number says so in the number's own shape.</b> A class the arm
/// does not measure prints <c>n/a (arm has no class histogram)</c>; a row that does not carry UDP
/// prints <c>not carried (UDP bypassed)</c> in place of every UDP-derived cell, because a datagram
/// count for such a row would be a measurement of nothing.</para>
/// <para><b>An eleven-cell row is the reference's, not a defect to repair.</b> The two shapes that
/// carry no numbers at all — a design absence and a histogram nobody published — hand the renderer one
/// id, one arm, one explanation and eight <c>n/a</c> cells against a ten-column header. The renderer
/// joins what it is given, so the golden carries those lines as they are and the port reproduces them
/// rather than padding or refusing them (<c>python-oracle-changes.md</c> §5.1).</para>
/// </remarks>
internal static class TableLatency
{
    private const string Caption =
        "Percentiles are read straight from the harness's histograms (`minUs`, `meanUs`, `p50Us`, `p90Us`, "
        + "`p99Us`, `p999Us`, `maxUs`) — never recomputed. Each cell is `median [p25–p75] across passes (n=K)`; "
        + "the `count` column is the median histogram count, so it also shows how many observations back each "
        + "percentile. A row that did not run an arm prints `not measured in this row`, and a UDP-derived cell "
        + "for a row that does not carry UDP prints `" + RowProfiles.NotCarriedCell + "`.";

    private const string WindowOverflowCell = "n/a (windowOverflow > 0)";

    private const string NoData = "n/a (no data)";

    /// <summary>The latency classes, in the order their sub-tables are written.</summary>
    private static readonly string[] s_classes = ["tcp-connect", "tcp-rtt", "udp-rtt", "dns-rtt"];

    private static readonly string[] s_headers = ["row", "arm", "count", .. MetricCatalogue.Percentiles];

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    /// <param name="campaign">The loaded campaign every histogram is read from.</param>
    /// <returns>The body's text.</returns>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var lines = new List<string>
        {
            Caption,
            string.Empty,
        };

        foreach (var latencyClass in s_classes)
        {
            lines.Add($"### `{latencyClass}`");
            lines.Add(string.Empty);
            var rows = new List<IReadOnlyList<string>>();
            foreach (var rowId in campaign.RowIds)
            {
                foreach (var armName in ArmRecords.LoadOrder)
                {
                    if (Row(campaign, latencyClass, rowId, armName) is { } row)
                    {
                        rows.Add(row);
                    }
                }
            }

            lines.Add(rows.Count > 0 ? MarkdownTable.Render(s_headers, rows) : NoData);
            lines.Add(string.Empty);
        }

        return string.Join('\n', lines);
    }

    /// <summary>One (row, arm) pair's line, or null when the row's plan never ran the arm.</summary>
    private static List<string>? Row(CampaignModel campaign, string latencyClass, string rowId, string armName)
    {
        if (!campaign.RunsOf(rowId).Any(run => run.Arms.Contains(armName)))
        {
            return null;
        }

        var udpPath = latencyClass is "udp-rtt" or "dns-rtt" ? "udp" : null;
        var (status, reason) = MetricStatus.Resolve(campaign, armName, udpPath, dns53: false, rowId);
        if (status is MetricStatus.NotInPlan or MetricStatus.DeclaredAbsent)
        {
            return [rowId, armName, $"n/a ({reason})", .. MarkdownTable.Repeated(MetricCatalogue.Percentiles.Count + 1, "n/a")];
        }

        if (string.Equals(status, MetricStatus.NotCarried, StringComparison.Ordinal))
        {
            return [rowId, armName, .. MarkdownTable.Repeated(MetricCatalogue.Percentiles.Count + 2, RowProfiles.NotCarriedCell)];
        }

        var (cells, reasons) = Histograms(campaign, latencyClass, rowId, armName);
        if (cells["count"].Values.Count == 0
            && !MetricCatalogue.Percentiles.Any(stat => cells[stat].Values.Count > 0))
        {
            var why = reasons.Count > 0 ? reasons[0] : "no histogram";
            return [rowId, armName, $"n/a ({why})", .. MarkdownTable.Repeated(MetricCatalogue.Percentiles.Count + 1, "n/a")];
        }

        if (WindowOverflow.Reached(campaign, rowId, armName))
        {
            return [rowId, armName, .. MarkdownTable.Repeated(MetricCatalogue.Percentiles.Count + 2, WindowOverflowCell)];
        }

        var rendered = new List<string>(s_headers.Length)
        {
            rowId,
            armName,
            cells["count"].Values.Count > 0
                ? DescriptiveStats.FmtStat(cells["count"].SortedValues(), digits: 0)
                : "n/a",
        };
        foreach (var stat in MetricCatalogue.Percentiles)
        {
            var values = cells[stat].SortedValues();
            rendered.Add(values.Count > 0 ? DescriptiveStats.FmtStat(values, digits: 1, unit: " us") : "n/a");
        }

        return rendered;
    }

    /// <summary>
    /// One class's histograms for one (row, arm) pair: the <c>count</c> column and the seven
    /// percentiles, each carrying the passes that published a value, plus every reason a pass gave for
    /// not publishing one.
    /// </summary>
    private static (Dictionary<string, MetricCell> Cells, List<string> Reasons) Histograms(
        CampaignModel campaign,
        string latencyClass,
        string rowId,
        string armName)
    {
        var cells = new Dictionary<string, MetricCell>(StringComparer.Ordinal);
        var reasons = new List<string>();
        foreach (var stat in Statistics())
        {
            var cell = new MetricCell();
            foreach (var passId in campaign.PassIds)
            {
                var run = campaign.InPass(passId, rowId);
                if (run is null)
                {
                    continue;
                }

                var (value, why) = ArmAccess.Latency(run, armName, latencyClass, stat);
                if (value is null)
                {
                    reasons.Add(why ?? "unavailable");
                }
                else
                {
                    cell.Values[passId] = value.Value;
                }
            }

            cells[stat] = cell;
        }

        return (cells, reasons);
    }

    /// <summary>The <c>count</c> column and then the seven percentiles, which is the read order.</summary>
    private static IEnumerable<string> Statistics()
    {
        yield return "count";
        foreach (var stat in MetricCatalogue.Percentiles)
        {
            yield return stat;
        }
    }
}
