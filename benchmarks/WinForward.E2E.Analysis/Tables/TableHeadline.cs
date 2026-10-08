using System.Globalization;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Metrics;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// §4, "Headline matrix": every metric the analysis publishes, one column each, one row per measured
/// program, plus the pass counts behind each cell.
/// </summary>
/// <remarks>
/// <para><b>This is the only section that prints all twenty-one metrics.</b> Each column is rendered
/// from its own declaration — the label, the unit and the rounding travel with the metric — so the
/// matrix and <c>verdict.json</c> cannot disagree about what a metric is or how it is scaled.</para>
/// <para><b>An empty cell is a statement, not a gap.</b> A rate the harness wrote as <see langword="null"/> had a
/// zero denominator: nothing was sent, so there is no rate to print and <c>0</c> would be a different
/// claim. <c>not carried (UDP bypassed)</c> is the same kind of statement about a product that cannot
/// carry UDP at all, and <c>n/a (reason)</c> about a row whose plan never ran the arm.</para>
/// <para><b>The pass-count table is the matrix's own footing.</b> A cell is a median over passes, so a
/// column built from one pass is visibly weaker than one built from three; the second table prints the
/// count behind every cell rather than leaving the reader to infer it.</para>
/// </remarks>
internal static class TableHeadline
{
    private const string CountHeading = "### Pass counts behind each headline cell";

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    /// <param name="campaign">The loaded campaign every metric is read from.</param>
    /// <returns>The body's text.</returns>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var cells = new Dictionary<string, MetricCell[]>(StringComparer.Ordinal);
        foreach (var rowId in campaign.RowIds)
        {
            var row = new MetricCell[MetricCatalogue.Specs.Count];
            for (var index = 0; index < MetricCatalogue.Specs.Count; index++)
            {
                row[index] = MetricCatalogue.PerPassValues(campaign, MetricCatalogue.Specs[index], rowId);
            }

            cells[rowId] = row;
        }

        var lines = new List<string>
        {
            Caption(campaign.WarmupSeconds),
            string.Empty,
            MarkdownTable.Render(Headers(), Matrix(campaign, cells)),
            string.Empty,
            CountHeading,
            string.Empty,
            MarkdownTable.Render(CountHeaders(), Counts(campaign, cells)),
            string.Empty,
        };

        return string.Join('\n', lines);
    }

    /// <summary>What the matrix prints and how each column is defined, in one paragraph.</summary>
    private static string Caption(double warmupSeconds)
    {
        var warmup = VerbatimNumber.Fixed(warmupSeconds, 1);
        return "Rows are measured programs. Every cell is `median [p25–p75] across passes (n=K)`. `REL unexpectedEofRate` is "
               + "`metrics.unexpectedEof / metrics.connectAttempts` and `REL fidelityRate` is `metrics.fidelityMismatch / "
               + "metrics.connectAttempts` (both over connect attempts, never over completed connections). Rates are "
               + "percentages; `proxy CPU` is percent of one vCPU over the loaded arms (IDLE excluded), measured as user-mode "
               + "process CPU only — section 6 states the scope in full — and `steady-state private bytes` is the per-pass p50 "
               + $"of the product's samples after the first {warmup} s of each arm. `DNS(53)` columns are the port-53 arm, whose "
               + "UDP path differs per row (section 9): use `DNSALT` for a cross-product comparison. A cell reading "
               + $"`{RowProfiles.NotCarriedCell}` is traffic the product does not carry, and an empty cell is a rate whose denominator was zero "
               + "— not a zero.";
    }

    /// <summary>The matrix's header: one column per metric, labelled with its unit.</summary>
    private static List<string> Headers() =>
        ["row", .. MetricCatalogue.Specs.Select(spec => $"{spec.Label} ({spec.Unit})")];

    /// <summary>The pass-count table's header: one column per metric key, then the coverage column.</summary>
    private static List<string> CountHeaders() =>
        ["row", .. MetricCatalogue.Specs.Select(spec => spec.Key), "coverage"];

    /// <summary>One line per row: the row's id and one rendered cell per metric.</summary>
    private static List<IReadOnlyList<string>> Matrix(
        CampaignModel campaign,
        Dictionary<string, MetricCell[]> cells)
    {
        var rows = new List<IReadOnlyList<string>>(campaign.RowIds.Count);
        foreach (var rowId in campaign.RowIds)
        {
            var row = new List<string>(MetricCatalogue.Specs.Count + 1) { rowId };
            for (var index = 0; index < MetricCatalogue.Specs.Count; index++)
            {
                row.Add(MetricCatalogue.CellText(cells[rowId][index], MetricCatalogue.Specs[index]));
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>One line per row: how many passes stand behind each cell, and how many columns have two.</summary>
    private static List<IReadOnlyList<string>> Counts(
        CampaignModel campaign,
        Dictionary<string, MetricCell[]> cells)
    {
        var rows = new List<IReadOnlyList<string>>(campaign.RowIds.Count);
        foreach (var rowId in campaign.RowIds)
        {
            var row = new List<string>(MetricCatalogue.Specs.Count + 2) { rowId };
            var covered = 0;
            foreach (var cell in cells[rowId])
            {
                var passes = cell.Values.Count;
                row.Add(passes.ToString(CultureInfo.InvariantCulture));
                if (passes >= 2)
                {
                    covered++;
                }
            }

            row.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{covered} of {MetricCatalogue.Specs.Count} metrics have >= 2 passes"));
            rows.Add(row);
        }

        return rows;
    }
}
