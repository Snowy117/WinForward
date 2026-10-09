using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Metrics;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// "DNS comparability detail": the port-53 arm beside the arm on a port no product special-cases,
/// with the carriage each row measures and how much of the arm rode it.
/// </summary>
/// <remarks>
/// <para><b>Only the alt-port arm is comparable across products.</b> The port-53 UDP path differs per
/// row — relayed through the proxy on some, forwarded verbatim to a local DNS target on others, and
/// passed through unredirected by a hardcoded port-53 rule — so a port-53 number measures that row's
/// own wiring rather than the product's DNS handling.</para>
/// <para><b>The share is what tells a reader how much of the arm was on that path.</b> The TCP part of
/// the arm is proxied on every row, so the UDP share is the part whose carriage is in question.</para>
/// <para><b>A row that does not carry UDP has no comparable DNS number at all.</b> Its rate and latency
/// cells print the not-carried marker rather than a number that would look like a result.</para>
/// </remarks>
internal static class TableDns
{
    private const string Caption =
        "`DNS` is the port-53 arm and `DNSALT` targets a port no product special-cases. **Only `DNSALT` is "
        + "comparable across products**: the port-53 UDP path differs per row — relayed through the proxy on "
        + "some, forwarded verbatim to the target by a local DNS target on others, and passed through "
        + "unredirected by ProxiFyre's hardcoded port-53 rule — so a port-53 number is a measurement of that "
        + "row's own wiring, not of the product's DNS handling. The `UDP/53 carriage` column states which of "
        + "those each row is, and the `DNS udp share` column shows how much of the arm rode that path (the TCP "
        + "part of the arm is proxied on every row). A row that does not carry UDP has no comparable DNS "
        + "number at all.";

    private const string RateUnit = "pp";

    private const int RateDigits = 4;

    private static readonly string[] s_headers =
    [
        "row",
        "DNS arm port",
        "DNSALT arm port",
        "UDP/53 carriage",
        "DNS udp share",
        "DNS answerRate",
        "DNS dns-rtt p50",
        "DNSALT udp share",
        "DNSALT answerRate",
        "DNSALT dns-rtt p50",
        "comparability",
    ];

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    /// <param name="campaign">The loaded campaign every arm is read from.</param>
    /// <returns>The body's text.</returns>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        return string.Join(
            '\n',
            Caption,
            string.Empty,
            MarkdownTable.Render(s_headers, [.. campaign.RowIds.Select(rowId => Row(campaign, rowId))]),
            string.Empty);
    }

    /// <summary>One row's line: both arms' ports, shares, rates and latencies, and why they compare.</summary>
    private static IReadOnlyList<string> Row(CampaignModel campaign, string rowId)
    {
        var profile = RowProfiles.Find(rowId);
        var (dnsStatus, dnsReason) = MetricStatus.Resolve(campaign, "DNS", udpPath: "dns", dns53: true, rowId);
        var (altStatus, altReason) = MetricStatus.Resolve(campaign, "DNSALT", udpPath: "dns", dns53: false, rowId);
        if (string.Equals(dnsStatus, MetricStatus.NotInPlan, StringComparison.Ordinal)
            && string.Equals(altStatus, MetricStatus.NotInPlan, StringComparison.Ordinal))
        {
            return [rowId, .. MarkdownTable.Repeated(9, "n/a"), dnsReason!];
        }

        var notCarried = profile is not null && string.Equals(profile.Udp, RowProfiles.UdpNotCarried, StringComparison.Ordinal);
        var dnsRateCell = notCarried
            ? RowProfiles.NotCarriedCell
            : RateCell(campaign, rowId, "DNS");
        var altRateCell = notCarried
            ? RowProfiles.NotCarriedCell
            : RateCell(campaign, rowId, "DNSALT");
        var dnsRtt = notCarried ? RowProfiles.NotCarriedCell : RttCell(campaign, rowId, "DNS");
        var altRtt = notCarried ? RowProfiles.NotCarriedCell : RttCell(campaign, rowId, "DNSALT");
        if (string.Equals(dnsStatus, MetricStatus.NotInPlan, StringComparison.Ordinal))
        {
            dnsRateCell = $"n/a ({dnsReason})";
            dnsRtt = dnsRateCell;
        }

        if (string.Equals(altStatus, MetricStatus.NotInPlan, StringComparison.Ordinal))
        {
            altRateCell = $"n/a ({altReason})";
            altRtt = altRateCell;
        }

        var share = Share(campaign, rowId, "DNS");
        var altShare = Share(campaign, rowId, "DNSALT");
        return
        [
            rowId,
            VerbatimNumber.Cell(DescriptiveStats.Median(Ports(campaign, rowId, "DNS")), 0),
            VerbatimNumber.Cell(DescriptiveStats.Median(Ports(campaign, rowId, "DNSALT")), 0),
            profile is null ? "n/a" : RowProfiles.Udp53Label(profile.Udp53),
            share is null ? "n/a" : VerbatimNumber.Cell(share * 100.0, 1, " %"),
            dnsRateCell,
            dnsRtt,
            altShare is null ? "n/a" : VerbatimNumber.Cell(altShare * 100.0, 1, " %"),
            altRateCell,
            altRtt,
            Comparability(profile, dnsStatus, altStatus, notCarried),
        ];
    }

    /// <summary>The sentence that says what the row's port-53 arm can be compared against.</summary>
    private static string Comparability(RowProfile? profile, string dnsStatus, string altStatus, bool notCarried)
    {
        if (string.Equals(dnsStatus, MetricStatus.DnsCarriage, StringComparison.Ordinal))
        {
            return $"port-53 arm is not cross-product comparable ({RowProfiles.Udp53Label(profile!.Udp53)}); use DNSALT";
        }

        if (string.Equals(altStatus, MetricStatus.NotInPlan, StringComparison.Ordinal))
        {
            return "DNSALT not measured in this row";
        }

        return notCarried ? "excluded from DNS-latency comparisons: UDP bypassed" : "comparable on DNSALT";
    }

    /// <summary>One arm's answer rate in percentage points, as a cell.</summary>
    private static string RateCell(CampaignModel campaign, string rowId, string armName)
    {
        var cell = new MetricCell();
        foreach (var passId in campaign.PassIds)
        {
            var run = campaign.InPass(passId, rowId);
            if (run is null)
            {
                continue;
            }

            var (value, why) = ArmAccess.Number(run, armName, "metrics/answerRate");
            if (value is null)
            {
                cell.Reasons[passId] = why ?? "unavailable";
            }
            else
            {
                cell.Values[passId] = value.Value * 100.0;
            }
        }

        return MetricCatalogue.CellText(cell, RateDigits, RateUnit);
    }

    /// <summary>One arm's DNS round-trip p50 across the passes.</summary>
    private static string RttCell(CampaignModel campaign, string rowId, string armName)
    {
        var cell = new MetricCell();
        foreach (var passId in campaign.PassIds)
        {
            var run = campaign.InPass(passId, rowId);
            if (run is null)
            {
                continue;
            }

            var (value, why) = ArmAccess.Latency(run, armName, "dns-rtt", "p50Us");
            if (value is null)
            {
                cell.Reasons[passId] = why ?? "unavailable";
            }
            else
            {
                cell.Values[passId] = value.Value;
            }
        }

        return cell.Values.Count == 0
            ? $"n/a ({cell.ReasonSummary()})"
            : DescriptiveStats.FmtStat(cell.SortedValues(), digits: 1, unit: " us", nullPasses: cell.NullPasses);
    }

    /// <summary>The share of one DNS arm that rode the UDP path, median across the passes that had one.</summary>
    private static double? Share(CampaignModel campaign, string rowId, string armName)
    {
        var shares = new List<double>();
        foreach (var passId in campaign.PassIds)
        {
            var run = campaign.InPass(passId, rowId);
            if (run is null)
            {
                continue;
            }

            var (udp, _) = ArmAccess.Number(run, armName, "metrics/udp.sent");
            var (tcp, _) = ArmAccess.Number(run, armName, "metrics/tcpSent");
            if (udp is null || tcp is null || udp.Value + tcp.Value <= 0)
            {
                continue;
            }

            shares.Add(udp.Value / (udp.Value + tcp.Value));
        }

        return DescriptiveStats.Median(shares);
    }

    /// <summary>One arm's declared port, over the passes that published it.</summary>
    private static List<double> Ports(CampaignModel campaign, string rowId, string armName)
    {
        var ports = new List<double>();
        foreach (var run in campaign.RunsOf(rowId))
        {
            var (value, _) = ArmAccess.Text(run, armName, "parameters/dnsPort");
            if (JsonValue.AsNumber(value) is { } port)
            {
                ports.Add(port);
            }
        }

        return ports;
    }
}
