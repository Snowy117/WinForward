using WinForward.E2E.Analysis.Metrics;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>One column of the UDP accuracy table: the field, and what bounds its rule of three.</summary>
/// <param name="Name">The column's name, which is also the field's name.</param>
/// <param name="Field">The path leaf read inside the arm's own block.</param>
/// <param name="Rate">Whether the value is a rate to be printed as a percentage.</param>
/// <param name="Denominator">Which counter bounds an all-zero cell, or null when none does.</param>
internal sealed record UdpField(string Name, string Field, bool Rate, string? Denominator);

/// <summary>
/// The UDP accuracy detail: the two arms that carry the full UDP classification, every datagram
/// outcome the harness counts, and the denominators behind the rule-of-three bounds.
/// </summary>
/// <remarks>
/// <para><b>A row that does not carry UDP prints the marker, never a number.</b> Reporting its
/// datagrams as a measured result is the single worst error this analysis could make, so the marker
/// replaces every cell of the row and the row is excluded from every UDP comparison.</para>
/// <para><b>An all-zero cell is bounded, not asserted.</b> Zero observed events bound the true rate
/// near <c>3/n</c> for that row's denominator, so the cell prints the bound rather than a zero that
/// would read as proof. A cell whose median is zero but which has a non-zero pass keeps the ordinary
/// rendering, because the spread is the finding.</para>
/// <para><b>An empty cell is a null rate.</b> The harness writes <see langword="null"/> when the denominator was
/// zero, which is not the same statement as a measured zero, and the two are different cell
/// categories.</para>
/// </remarks>
internal static class TableUdp
{
    private const string Caption =
        "Arms that carry the full UDP classification: `LOSS` (whole arm) and `MIX` (`metrics.classes.udp`). "
        + "Every cell is `median [p25–p75] across passes (n=K)`. Rates are percentages of the arm's `sent`. "
        + "**A row that does not carry UDP prints `" + RowProfiles.NotCarriedCell + "` in every UDP cell and is "
        + "excluded from every UDP comparison** — reporting its datagrams as a measured result would be the "
        + "single worst error this analysis could make. **Rule of three:** a cell is printed as `< 3/n` when "
        + "*every* pass reports exactly zero, with n that row's denominator — zero observed events bound the "
        + "true rate near 3/n, they do not prove it is zero. A cell whose median is zero but which has a "
        + "non-zero pass keeps its ordinary `0 [0–x]` rendering so the spread stays visible. An empty cell is a "
        + "`null` rate: the harness wrote `null` because nothing was sent, which is not a zero rate.";

    private const string FallbackNote =
        "`MIX` has no `supplied` counter in its UDP class, so `clientSendLoss` for that arm falls back to the "
        + "`sent` denominator for its rule-of-three bound.";

    /// <summary>The two arms the table covers: the whole LOSS arm and the MIX arm's UDP class.</summary>
    private static readonly (string Arm, string Prefix)[] s_arms = [("LOSS", "metrics"), ("MIX", "metrics/classes/udp")];

    /// <summary>The classifier's fields, in the order the harness counts them.</summary>
    private static readonly UdpField[] s_fields =
    [
        new("sent", "sent", Rate: false, Denominator: null),
        new("arrived", "arrived", Rate: false, Denominator: "sent"),
        new("late", "late", Rate: false, Denominator: "sent"),
        new("never", "never", Rate: false, Denominator: "sent"),
        new("corruptDatagrams", "corruptDatagrams", Rate: false, Denominator: "sent"),
        new("corrupt", "corrupt", Rate: false, Denominator: "sent"),
        new("duplicate", "duplicate", Rate: false, Denominator: "sent"),
        new("reordered", "reordered", Rate: false, Denominator: "sent"),
        new("foreignConnection", "foreignConnection", Rate: false, Denominator: "sent"),
        new("abandonedAtTeardown", "abandonedAtTeardown", Rate: false, Denominator: "sent"),
        new("lossRate", "lossRate", Rate: true, Denominator: "sent"),
        new("strictLossRate", "strictLossRate", Rate: true, Denominator: "sent"),
        new("corruptRate", "corruptRate", Rate: true, Denominator: "sent"),
        new("reorderRate", "reorderRate", Rate: true, Denominator: "sent"),
        new("clientSendLoss", "clientSendLoss", Rate: false, Denominator: "supplied"),
    ];

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    /// <param name="campaign">The loaded campaign every counter is read from.</param>
    /// <returns>The body's text.</returns>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var lines = new List<string>
        {
            Caption,
            string.Empty,
        };

        var headers = new List<string> { "row", "arm", "passes", "UDP carriage" };
        headers.AddRange(s_fields.Select(field => field.Name));
        headers.Add("window ms");
        headers.Add("UDP/53 carriage");

        var rows = new List<IReadOnlyList<string>>();
        foreach (var rowId in campaign.RowIds)
        {
            foreach (var (armName, prefix) in s_arms)
            {
                rows.Add(Row(campaign, rowId, armName, prefix));
            }
        }

        lines.Add(MarkdownTable.Render(headers, rows));
        lines.Add(string.Empty);
        lines.Add("### UDP denominators behind the rule-of-three bounds");
        lines.Add(string.Empty);
        lines.Add(MarkdownTable.Render(
            ["row", "LOSS sent", "LOSS supplied", "MIX udp sent", "MIX udp supplied"],
            [.. campaign.RowIds.Select(rowId => Denominators(campaign, rowId))]));
        lines.Add(string.Empty);
        lines.Add(FallbackNote);
        lines.Add(string.Empty);
        return string.Join('\n', lines);
    }

    /// <summary>One (row, arm) pair's line.</summary>
    private static List<string> Row(CampaignModel campaign, string rowId, string armName, string prefix)
    {
        var profile = RowProfiles.Find(rowId);
        var carriage = profile is null ? "n/a" : RowProfiles.UdpLabel(profile.Udp);
        if (!campaign.RunsOf(rowId).Any(run => run.Arms.Contains(armName)))
        {
            var (_, reason) = MetricStatus.Resolve(campaign, armName, udpPath: null, dns53: false, rowId);
            var why = reason ?? $"no {armName} arm";
            return [rowId, armName, "0", carriage, .. MarkdownTable.Repeated(s_fields.Length + 2, $"n/a ({why})")];
        }

        if (profile is not null && string.Equals(profile.Udp, RowProfiles.UdpNotCarried, StringComparison.Ordinal))
        {
            return [rowId, armName, "n/a", RowProfiles.NotCarriedCell, .. MarkdownTable.Repeated(s_fields.Length + 2, RowProfiles.NotCarriedCell)];
        }

        var cells = new Dictionary<string, MetricCell>(StringComparer.Ordinal);
        var passes = 0;
        foreach (var field in s_fields)
        {
            var cell = new MetricCell();
            foreach (var passId in campaign.PassIds)
            {
                var run = campaign.InPass(passId, rowId);
                if (run is null)
                {
                    continue;
                }

                var (value, why) = ArmAccess.Number(run, armName, $"{prefix}/{field.Field}");
                if (value is null)
                {
                    cell.Reasons[passId] = why ?? "unavailable";
                }
                else
                {
                    cell.Values[passId] = value.Value;
                }
            }

            cells[field.Name] = cell;
            passes = Math.Max(passes, cell.Values.Count);
        }

        var denominators = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var name in new[] { "sent", "supplied" })
        {
            denominators[name] = DescriptiveStats.Median(Collected(campaign, rowId, armName, $"{prefix}/{name}"));
        }

        var rendered = new List<string> { rowId, armName, passes.ToString(System.Globalization.CultureInfo.InvariantCulture), carriage };
        foreach (var field in s_fields)
        {
            rendered.Add(Cell(cells[field.Name], field, armName, Bound(denominators[field.Denominator ?? "sent"])));
        }

        var windows = Collected(campaign, rowId, armName, prefix + "/window");
        rendered.Add(windows.Count > 0 ? DescriptiveStats.FmtStat(windows, digits: 1) : "n/a (no window metric)");
        rendered.Add(profile is null ? "n/a" : RowProfiles.Udp53Label(profile.Udp53));
        return rendered;
    }

    /// <summary>One classifier cell, with the rule-of-three bound its denominator gives it.</summary>
    private static string Cell(MetricCell cell, UdpField field, string armName, int? boundN)
    {
        if (cell.Values.Count == 0 && cell.NullPasses == 0)
        {
            return string.Equals(armName, "MIX", StringComparison.Ordinal)
                ? $"n/a (MIX does not publish {field.Name})"
                : $"n/a (no {field.Name} metric)";
        }

        var missing = cell.Reasons.Count - cell.NullPasses;
        if (field.Rate)
        {
            return cell.Values.Count == 0
                ? string.Empty
                : DescriptiveStats.FmtStat(
                    [.. cell.SortedValues().Select(value => value * 100.0)],
                    digits: 4,
                    unit: " %",
                    zeroBoundN: boundN,
                    boundScale: 100.0,
                    nullPasses: cell.NullPasses,
                    missingPasses: missing);
        }

        return DescriptiveStats.FmtStat(
            cell.SortedValues(),
            digits: 0,
            unit: string.Empty,
            zeroBoundN: string.Equals(field.Name, "sent", StringComparison.Ordinal) ? null : boundN,
            boundScale: null,
            nullPasses: cell.NullPasses,
            missingPasses: missing);
    }

    /// <summary>The rule-of-three denominator as the reference rounds it, or null when there is none.</summary>
    private static int? Bound(double? denominator) =>
        denominator is { } value ? (int)Math.Round(value, MidpointRounding.ToEven) : null;

    /// <summary>One arm's per-pass readings of one path, in pass order and with the passes that gave nothing.</summary>
    private static List<double> Collected(CampaignModel campaign, string rowId, string armName, string path)
    {
        var values = new List<double>();
        foreach (var passId in campaign.PassIds)
        {
            var run = campaign.InPass(passId, rowId);
            if (run is null)
            {
                continue;
            }

            var (value, _) = ArmAccess.Number(run, armName, path);
            if (value is not null)
            {
                values.Add(value.Value);
            }
        }

        return values;
    }

    /// <summary>The four denominators the rule-of-three bounds are quoted against, per row.</summary>
    private static List<string> Denominators(CampaignModel campaign, string rowId)
    {
        var cells = new List<string> { rowId };
        foreach (var (armName, prefix) in s_arms)
        {
            foreach (var field in new[] { "sent", "supplied" })
            {
                var collected = Collected(campaign, rowId, armName, $"{prefix}/{field}");
                cells.Add(collected.Count > 0 ? DescriptiveStats.FmtStat(collected, digits: 0) : "n/a");
            }
        }

        return cells;
    }
}
