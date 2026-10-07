using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// §15, "Data availability": one line per loaded run, saying what the run actually contributed —
/// which arms are on disk, how many of them published a result, what the plan expected and did not
/// get, and what the run could not be read from.
/// </summary>
/// <remarks>
/// <para><b>One line per run, not per row.</b> The same row is measured once per pass, and the point of
/// the table is to show that the three passes of a row agree on what ran; a row whose pass2 is missing
/// an arm must be visible as such.</para>
/// <para><b>The empty cell is a dash, not a blank.</b> "declared but absent" and "present but
/// undeclared" print an em dash when the two sets agree, so a reader can tell "nothing is missing" from
/// "this column was not computed".</para>
/// </remarks>
internal static class TableAvailability
{
    private const string NoGap = "—";

    private const string NoArms = "none";

    private static readonly string[] s_headers =
    [
        "pass",
        "row",
        "arms present",
        "arms with a result",
        "declared but absent",
        "present but undeclared",
        "self samples",
        "product samples",
        "rejected samples",
        "samplerError",
        "proxy-truth.json",
        "dual lanes",
        "config files",
        "notes",
    ];

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var lines = new List<string>
        {
            "What every row actually contributed, so a reader can see exactly which cells above are thin. `arms "
            + "present` lists the arm names found; `arms with a result` counts the files that carried a `result` "
            + "record; `declared but absent` is the plan table's expectation that was not met, which is a data gap "
            + "rather than a design statement.",
            string.Empty,
        };

        var rows = new List<IReadOnlyList<string>>(campaign.Rows.Count);
        foreach (var run in campaign.Rows)
        {
            rows.Add(Row(run));
        }

        lines.Add(MarkdownTable.Render(s_headers, rows));
        lines.Add(string.Empty);
        return string.Join('\n', lines);
    }

    private static IReadOnlyList<string> Row(ClientRun run)
    {
        var declared = RowProfiles.Planned(run.RunId) ?? [];
        var present = run.Arms.SortedNames;
        var missing = declared.Where(name => !run.Arms.Contains(name)).ToList();
        var extra = present.Where(name => !declared.Contains(name, StringComparer.Ordinal)).ToList();
        var product = RunSamples.Product(run);

        return
        [
            run.PassId,
            run.RunId,
            present.Count > 0 ? string.Join(", ", present) : NoArms,
            $"{run.Arms.All.Count(arm => arm.Result is not null)}/{run.Arms.Count}",
            missing.Count > 0 ? string.Join(", ", missing) : NoGap,
            extra.Count > 0 ? string.Join(", ", extra) : NoGap,
            RunSamples.Self(run).Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            product.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            product.Count(sample => !RunSamples.IsReadable(sample)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            RunSamples.SamplerErrors(run).Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            run.ProxyTruth is not null ? "yes" : "no",
            Lanes(run),
            run.ConfigCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            run.LoadErrors.Count > 0 ? string.Join("; ", run.LoadErrors) : NoGap,
        ];
    }

    /// <summary>The lanes that ran, or why the column cannot name any.</summary>
    private static string Lanes(ClientRun run)
    {
        var lanes = new List<string>(2);
        if (run.DualProxied is not null)
        {
            lanes.Add("proxied");
        }

        if (run.DualDirect is not null)
        {
            lanes.Add("direct");
        }

        if (lanes.Count > 0)
        {
            return string.Join(", ", lanes);
        }

        return RowProfiles.Find(run.RunId) is { Dual: true } ? "declared, missing" : "n/a";
    }
}
