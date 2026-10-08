using System.Globalization;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Findings;

/// <summary>
/// What the target ledger adds to the analysis: the findings that only a second, independent count can
/// produce — a connection or datagram count the client and the target disagree about, a window whose
/// records cannot be attributed, an endpoint seen on both paths of one row.
/// </summary>
/// <remarks>
/// <para><b>The ledger is the check, not the source.</b> Every count the client published stays the
/// reported number; the ledger is read against it, and a disagreement is disclosed with both sides.</para>
/// <para><b>The endpoint partition is a property of one row.</b> The same port number seen on two different
/// rows is not necessarily the same endpoint, so only the endpoints inside one row's own windows are
/// compared.</para>
/// </remarks>
internal static partial class LedgerFindings
{
    internal const double ConnectionTolerance = 0.01;

    internal const double ConnectionSlack = 2.0;

    private const double DatagramTolerance = 0.01;

    private const double DatagramSlack = 5.0;

    private const double SummaryIntervalSeconds = 1.0;

    internal static List<Finding> Collect(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var views = LedgerViewsBuilder.For(campaign);
        if (!views.Available)
        {
            return
            [
                new Finding(
                    Severity.MeasurementCaveat,
                    "no-target-ledger",
                    "campaign",
                    "no target-ledger.jsonl (or ledger.jsonl) was found beside the pass directories, in the raw "
                    + "directory or in its parent, so the independent second opinion the campaign is designed around "
                    + "is unavailable"),
            ];
        }

        var outFindings = new List<Finding>();
        var lastPass = string.Empty;
        foreach (var (passId, entry) in views.Passes)
        {
            lastPass = passId;
            if (entry.Records == 0)
            {
                outFindings.Add(new Finding(Severity.MeasurementCaveat, "empty-target-ledger", passId, "the ledger holds no records"));
                continue;
            }

            if (entry.BadLines > 0)
            {
                outFindings.Add(new Finding(
                    Severity.HarnessError,
                    "ledger-bad-lines",
                    passId,
                    string.Create(CultureInfo.InvariantCulture, $"{entry.BadLines} ledger line(s) are not JSON")));
            }

            outFindings.AddRange(ArmFindings(passId, entry));
            outFindings.AddRange(OverlapFindings(campaign, passId, entry));
        }

        // The reference's dns-totals block reads the pass variable its own loop above left behind, so it
        // reads the records of the last pass that has a ledger; the client totals span every pass.
        outFindings.AddRange(DnsFindings(campaign, lastPass));
        return outFindings;
    }
    private static List<Finding> ArmFindings(string passId, LedgerPassView entry)
    {
        var outFindings = new List<Finding>();
        foreach (var arm in entry.PerArm)
        {
            var scope = $"{passId}/{arm.Row} {arm.Arm}";
            if (arm.ClientConnections is > 0
                && DescriptiveStats.WithinTolerance(arm.TcpConnections, arm.ClientConnections, ConnectionTolerance, ConnectionSlack) is false)
            {
                outFindings.Add(new Finding(
                    Severity.MeasurementCaveat,
                    "ledger-connection-mismatch",
                    scope,
                    $"the ledger saw {arm.TcpConnections.ToString(CultureInfo.InvariantCulture)} connection(s) in the arm's window against the client's own "
                    + $"{VerbatimNumber.Cell(arm.ClientConnections, 1)} ({arm.ClientConnectionsSource})"));
            }

            if (arm.ClientDatagrams is > 0)
            {
                var band = DatagramBand(arm.ClientDatagrams.Value, arm.DurationSeconds);
                if (Math.Abs(arm.UdpDatagrams - arm.ClientDatagrams.Value) > band)
                {
                    outFindings.Add(new Finding(
                        Severity.MeasurementCaveat,
                        "ledger-datagram-mismatch",
                        scope,
                        $"the ledger counted {arm.UdpDatagrams.ToString(CultureInfo.InvariantCulture)} datagram(s) from its source census against the client's own "
                        + $"{VerbatimNumber.Cell(arm.ClientDatagrams, 1)} ({arm.ClientDatagramsSource}), outside the "
                        + $"±{VerbatimNumber.Cell(band, 1)} band the one-second summary granularity allows"));
                }
            }

            if (arm.Unattributable)
            {
                outFindings.Add(new Finding(
                    Severity.MeasurementCaveat,
                    "ledger-window-ambiguous",
                    scope,
                    "this window overlaps " + string.Join(", ", arm.OverlappingRuns)
                    + " and the ledger carries no per-run label, so its records cannot be attributed to this run "
                    + "rather than to the overlapping one; the counts below are the whole overlapping window"));
            }

            if (arm.UdpOverflow > 0)
            {
                outFindings.Add(new Finding(
                    Severity.MeasurementCaveat,
                    "ledger-source-overflow",
                    scope,
                    $"udpSummary.sourceOverflow={arm.UdpOverflow.ToString(CultureInfo.InvariantCulture)}: the endpoint census table was full, so the endpoint "
                    + "list for this window is incomplete"));
            }
        }

        return outFindings;
    }
    private static List<Finding> OverlapFindings(CampaignModel campaign, string passId, LedgerPassView entry)
    {
        var outFindings = new List<Finding>();
        foreach (var (rowId, slot) in EndpointPartition(campaign, passId, entry))
        {
            var overlap = slot.Proxied.Intersect(slot.Direct, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (overlap.Count == 0)
            {
                continue;
            }

            outFindings.Add(new Finding(
                Severity.CorrectnessFailure,
                "ledger-endpoint-overlap",
                $"{passId}/{rowId}",
                "datagram(s) arrived from endpoint(s) used on both a proxied and a direct-path window of this "
                + $"run: {string.Join(", ", overlap)}; a direct-path arm arriving from a proxied endpoint (or the reverse) "
                + "means the product did not route that traffic where it was configured to"));
        }

        return outFindings;
    }
    private static List<Finding> DnsFindings(CampaignModel campaign, string totalsPass)
    {
        var outFindings = new List<Finding>();
        var firstPass = campaign.PassIds.Count > 0 ? campaign.PassIds[0] : string.Empty;
        foreach (var (port, totals) in DnsTotals(campaign, firstPass))
        {
            if (totals.ClientUdp.Equals(0.0) && totals.ClientTcp.Equals(0.0))
            {
                continue;
            }

            var band = DnsBand(totals);
            if (Math.Abs(totals.LedgerUdp - totals.ClientUdp) > band)
            {
                outFindings.Add(new Finding(
                    Severity.MeasurementCaveat,
                    "ledger-dns-udp-mismatch",
                    $"campaign dns port {port}",
                    $"{totals.Summaries.ToString(CultureInfo.InvariantCulture)} dnsSummary record(s) report {VerbatimNumber.Cell(totals.LedgerUdp, 0)} UDP "
                    + $"quer{(totals.LedgerUdp is 1.0 ? "y" : "ies")} against the client's own {VerbatimNumber.Cell(totals.ClientUdp, 0)}, "
                    + $"outside the ±{VerbatimNumber.Cell(band, 0)} band the {totals.Summaries.ToString(CultureInfo.InvariantCulture)} shutdown "
                    + $"summar{(totals.Summaries == 1 ? "y" : "ies")} allow"));
            }

            if (DescriptiveStats.WithinTolerance(totals.LedgerTcp, totals.ClientTcp, ConnectionTolerance, ConnectionSlack) is false)
            {
                outFindings.Add(new Finding(
                    Severity.MeasurementCaveat,
                    "ledger-dns-tcp-mismatch",
                    $"campaign dns port {port}",
                    $"{totals.Summaries.ToString(CultureInfo.InvariantCulture)} dnsSummary record(s) report {VerbatimNumber.Cell(totals.LedgerTcp, 0)} TCP "
                    + $"quer{(totals.LedgerTcp is 1.0 ? "y" : "ies")} against the client's own {VerbatimNumber.Cell(totals.ClientTcp, 0)}"));
            }

            outFindings.AddRange(WriteErrorFindings(campaign, totalsPass));
        }

        return outFindings;
    }
    private static List<Finding> WriteErrorFindings(CampaignModel campaign, string passId)
    {
        var outFindings = new List<Finding>();
        var summaries = LedgerLoader.For(campaign, passId)
            .SelectMany(ledger => ledger.Records)
            .Where(record => string.Equals(
                JsonValue.String(record.Payload, Contracts.ArmKeys.Common.Record.Type),
                LedgerViewsBuilder.TargetSummary,
                StringComparison.Ordinal));
        foreach (var record in summaries)
        {
            var errors = JsonValue.Number(record.Payload, Contracts.ArmKeys.Ledger.TargetSummary.LedgerWriteErrors) ?? 0.0;
            if (!errors.Equals(0.0))
            {
                outFindings.Add(new Finding(
                    Severity.HarnessError,
                    "ledger-write-errors",
                    passId,
                    $"targetSummary.ledgerWriteErrors={VerbatimNumber.Cell(errors, 0)}: the ledger itself lost records"));
            }
        }

        return outFindings;
    }

    /// <summary>How a pass's ledger records are attributed to runs, in the reference's words.</summary>
    internal static string AttributionOf(CampaignModel campaign, string passId)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        return LedgerViewsBuilder.For(campaign).Attribution.GetValueOrDefault(passId, "n/a");
    }

    /// <summary>The widest defensible agreement band for a datagram count.</summary>
    internal static double DatagramBand(double clientDatagrams, double durationSeconds)
    {
        var band = Math.Max(DatagramSlack, DatagramTolerance * Math.Abs(clientDatagrams));
        if (clientDatagrams != 0.0 && durationSeconds != 0.0)
        {
            band = Math.Max(band, clientDatagrams / durationSeconds * SummaryIntervalSeconds);
        }

        return band;
    }

    /// <summary>The agreement band DNS query totals are judged by: one summary interval of the client's own rate, or 1 %.</summary>
    internal static double DnsBand(DnsPortTotals totals)
    {
        ArgumentNullException.ThrowIfNull(totals);

        var band = Math.Max(DatagramSlack, DatagramTolerance * Math.Abs(totals.ClientUdp));
        if (totals.ClientUdp != 0.0 && totals.DurationSeconds != 0.0)
        {
            var rate = totals.ClientUdp / totals.DurationSeconds;
            band = Math.Max(band, rate * SummaryIntervalSeconds * Math.Max(1, totals.Summaries));
        }

        return band;
    }

}
