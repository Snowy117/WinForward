using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// §1, "What each row is and what it does with UDP": the campaign's own design table, one row per
/// measured row, saying what the row runs and where its UDP traffic goes.
/// </summary>
/// <remarks>
/// <para><b>This table is the single place the campaign's design is recorded.</b> Every
/// <c>not measured in this row</c>, <c>not carried (UDP bypassed)</c> and comparability rule elsewhere in
/// the document is derived from it, so a row's label can never drift from its treatment.</para>
/// <para><b>A row the design table does not know is still printed.</b> Its cells say so and name the arms
/// that were actually found, which is what keeps a tree with an extra row from producing a document that
/// silently drops it.</para>
/// </remarks>
internal static class TableRowProfiles
{
    private const string UnknownRow = "n/a (row id not in ROW_PROFILES)";

    private static readonly string[] s_headers =
    [
        "row",
        "product",
        "configuration",
        "plan",
        "arms the plan runs",
        "dual phase",
        "what it does with UDP/53",
        "what it does with general UDP",
    ];

    /// <summary>The prose §1 opens with, which says what the table is generated from.</summary>
    private const string GeneratedFrom =
        "Generated from `ROW_PROFILES` in the analysis source, which is the single place the campaign's "
        + "design is recorded: the plan each row runs, whether it has a dual phase, what it does with "
        + "destination-port-53 UDP and what it does with general UDP. Every `not measured in this row`, "
        + "`not carried (UDP bypassed)` and comparability rule elsewhere in this file is derived from this "
        + "table, so a row's label can never drift from its treatment.";

    private const string PartialRows =
        "The three WinForward rows are a deliberate design: `wf-aot-nativeudp` differs from `wf-aot-opt` "
        + "only in the UDP carriage and `wf-aot-dnsrelay` only in the DNS local target, so a difference "
        + "between `wf-aot-opt` and either is attributable to that one feature. Because a partial row never "
        + "ran the arms the other rows ran, every comparison involving it is reported as `not measured in "
        + "this row` and is excluded from the Holm family rather than treated as a missing value or a zero.";

    private const string DnsAltNote =
        "`DNSALT` targets a port no product special-cases, so it is the arm whose results are comparable "
        + "across products; the port-53 `DNS` arm is not, because its UDP path differs per row (third column "
        + "from the right above). `proxifier` cannot proxy UDP at all, so every UDP cell for that row reads "
        + $"`{RowProfiles.NotCarriedCell}` and the row is excluded from the UDP-accuracy and DNS-latency "
        + "comparisons; its TCP results are unaffected.";

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var lines = new List<string> { GeneratedFrom, string.Empty };
        var extraNotes = new List<string>();
        var rows = new List<IReadOnlyList<string>>(campaign.RowIds.Count);
        foreach (var rowId in campaign.RowIds)
        {
            rows.Add(Row(campaign, rowId, extraNotes));
        }

        lines.Add(MarkdownTable.Render(s_headers, [.. rows.Select(row => row.Take(s_headers.Length).ToList())]));
        lines.Add(string.Empty);
        if (extraNotes.Count > 0)
        {
            lines.Add("Plan/observation mismatches: " + string.Join("; ", extraNotes) + ".");
            lines.Add(string.Empty);
        }

        lines.Add(PartialRows);
        lines.Add(string.Empty);
        lines.Add(DnsAltNote);
        lines.Add(string.Empty);
        return string.Join('\n', lines);
    }

    /// <summary>One row of the table, collecting what the design did not declare when it finds any.</summary>
    private static IReadOnlyList<string> Row(CampaignModel campaign, string rowId, List<string> extraNotes)
    {
        var observed = campaign.RunsOf(rowId).SelectMany(run => run.Arms.SortedNames)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var profile = RowProfiles.Find(rowId);
        if (profile is null)
        {
            return
            [
                rowId,
                UnknownRow,
                "n/a (no declared configuration)",
                "n/a (unknown plan)",
                observed.Count > 0 ? string.Join(", ", observed) : "none",
                "n/a",
                "n/a (unknown; the analysis falls back to the arms actually present)",
                "n/a (unknown; the analysis falls back to the arms actually present)",
            ];
        }

        var declared = RowProfiles.PlanArms[profile.Plan];
        var extra = observed.Where(name => !declared.Contains(name, StringComparer.Ordinal)).ToList();
        if (extra.Count > 0)
        {
            extraNotes.Add($"undeclared arms present: {string.Join(", ", extra)}");
        }

        return
        [
            rowId,
            profile.Product,
            profile.Config,
            profile.Plan,
            string.Join(", ", declared),
            profile.Dual ? "yes" : "no",
            RowProfiles.Udp53Label(profile.Udp53),
            RowProfiles.UdpLabel(profile.Udp)
                + (string.Equals(rowId, "proxifyre", StringComparison.Ordinal) ? " — except destination port 53" : string.Empty),
        ];
    }
}
