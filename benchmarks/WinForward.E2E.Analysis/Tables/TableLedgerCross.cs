using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// §14.3 to §14.6 of "Target-ledger cross-check": where the traffic actually went, what each DNS listener
/// counted, how the target's TCP verdicts compare with the client's expectation, and the target-side
/// decode total when there is one.
/// </summary>
/// <remarks>
/// <para><b>The partition is a property of one row.</b> The same port number seen on two different rows is
/// not necessarily the same endpoint, so only the endpoints inside one row's own windows are compared, and
/// an endpoint that served both a proxied and a direct-path window is a correctness failure rather than a
/// curiosity.</para>
/// <para><b>§14.6 is a target-side total and nothing else.</b> A datagram that fails to decode carries no
/// sequence number, so it belongs to no run and no arm; no arm-level cell, rate or gate anywhere includes
/// it, and a campaign whose targets decoded every datagram prints no such table at all.</para>
/// </remarks>
internal static class TableLedgerCross
{
    private const string NoPartition = "n/a (no UDP echo window in this tree carried a source census)";

    private const string NoDns = "n/a (the ledger holds no dnsSummary records)";

    private const string NoTcp = "n/a (no tcp records in the ledger)";

    private static readonly string[] s_partitionHeaders =
    [
        "pass",
        "run",
        "proxied-path arms",
        "proxied endpoints",
        "direct-path arms",
        "direct endpoints",
        "shared endpoints",
        "check",
    ];

    private static readonly string[] s_dnsHeaders =
    [
        "pass",
        "dns port",
        "ledger UDP queries",
        "client UDP queries",
        "check",
        "ledger TCP queries",
        "client TCP queries",
        "check",
    ];

    private static readonly string[] s_verdictHeaders =
    [
        "pass",
        "target clean",
        "client expected clean",
        "target reset",
        "client expected reset",
        "target partialFin",
        "client expected partialFin",
        "target halfClose",
        "client expected halfClose",
    ];

    private static readonly string[] s_decodeHeaders =
        ["ledger", "datagrams received", "undecodable", "share of received"];

    /// <summary>§14.3: the endpoints each path actually served, one row per run that saw any.</summary>
    internal static List<string> Partition(CampaignModel campaign, LedgerViews views)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(views);

        var rows = new List<IReadOnlyList<string>>();
        foreach (var (passId, entry) in views.Passes)
        {
            var perRow = LedgerFindings.EndpointPartition(campaign, passId, entry);
            foreach (var (rowId, slot) in perRow.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (slot.Proxied.Count == 0 && slot.Direct.Count == 0)
                {
                    continue;
                }

                var shared = slot.Proxied.Intersect(slot.Direct, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                rows.Add(
                [
                    passId,
                    rowId,
                    Arms(slot.ProxiedArms),
                    slot.Proxied.Count.ToString(CultureInfo.InvariantCulture),
                    Arms(slot.DirectArms),
                    slot.Direct.Count.ToString(CultureInfo.InvariantCulture),
                    shared.Count > 0 ? string.Join(", ", shared) : "none",
                    shared.Count > 0
                        ? "CORRECTNESS FAILURE: an endpoint served both a proxied and a direct-path window"
                        : "disjoint",
                ]);
            }
        }

        return
        [
            "### 14.3 UDP source endpoints and the proxied/direct partition",
            string.Empty,
            rows.Count > 0 ? MarkdownTable.Render(s_partitionHeaders, rows) : NoPartition,
            string.Empty,
            PartitionNote,
            string.Empty,
        ];
    }

    /// <summary>§14.4: the DNS queries each port's listener counted, against the client's own.</summary>
    internal static List<string> Dns(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var rows = new List<IReadOnlyList<string>>();
        var firstPass = campaign.PassIds.Count > 0 ? campaign.PassIds[0] : string.Empty;
        var totals = LedgerFindings.DnsTotals(campaign, firstPass);
        foreach (var port in totals.Keys.Order(StringComparer.Ordinal))
        {
            var entry = totals[port];
            var clientless = entry.ClientUdp.Equals(0.0) && entry.ClientTcp.Equals(0.0);
            var band = LedgerFindings.DnsBand(entry);
            rows.Add(
            [
                "all passes",
                port,
                VerbatimNumber.Cell(entry.LedgerUdp, 0),
                VerbatimNumber.Cell(entry.ClientUdp, 0),
                DnsUdpCheck(entry, band, clientless),
                VerbatimNumber.Cell(entry.LedgerTcp, 0),
                VerbatimNumber.Cell(entry.ClientTcp, 0),
                DnsTcpCheck(entry, clientless),
            ]);
        }

        var lines = new List<string> { "### 14.4 DNS queries per port (client vs target)", string.Empty };
        if (rows.Count > 0)
        {
            lines.Add(MarkdownTable.Render(s_dnsHeaders, rows));
            lines.Add(string.Empty);
            lines.Add(DnsNote);
        }
        else
        {
            lines.Add(NoDns);
        }

        lines.Add(string.Empty);
        return lines;
    }

    /// <summary>§14.5: how the target's own verdicts compare with what the client expected.</summary>
    internal static List<string> TcpVerdicts(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var rows = new List<IReadOnlyList<string>>();
        foreach (var passId in campaign.PassIds)
        {
            var ledgerCounts = TcpVerdictsOf(campaign, passId);
            var clientCounts = ExpectedVerdicts(campaign, passId);
            rows.Add(
            [
                passId,
                VerbatimNumber.Cell(Count(ledgerCounts, "clean"), 0),
                VerbatimNumber.Cell(Count(clientCounts, "clean"), 0),
                VerbatimNumber.Cell(Count(ledgerCounts, "reset"), 0),
                VerbatimNumber.Cell(Count(clientCounts, "reset"), 0),
                VerbatimNumber.Cell(Count(ledgerCounts, "partialFin"), 0),
                VerbatimNumber.Cell(Count(clientCounts, "partialFin"), 0),
                VerbatimNumber.Cell(Count(ledgerCounts, "halfClose"), 0),
                VerbatimNumber.Cell(Count(clientCounts, "halfClose"), 0),
            ]);
        }

        var lines = new List<string> { "### 14.5 TCP verdicts: target vs the client's own expectation", string.Empty };
        if (rows.Count > 0)
        {
            lines.Add(MarkdownTable.Render(s_verdictHeaders, rows));
            lines.Add(string.Empty);
            lines.Add(TcpNote);
        }
        else
        {
            lines.Add(NoTcp);
        }

        lines.Add(string.Empty);
        return lines;
    }

    /// <summary>§14.6: the target-side decode total, printed only when there is one to disclose.</summary>
    internal static List<string> Decode(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var decode = LedgerDecodeTotals.For(campaign);
        var undecodable = DescriptiveStats.Sum([.. decode.Values.Select(entry => entry.Undecodable ?? 0.0)]);
        if (undecodable.CompareTo(0.0) <= 0)
        {
            return [];
        }

        var rows = new List<IReadOnlyList<string>>();
        var totalReceived = 0.0;
        foreach (var path in decode.Keys.Order(StringComparer.Ordinal))
        {
            var entry = decode[path];
            var received = entry.Received ?? 0.0;
            totalReceived += received;
            rows.Add(
            [
                path,
                VerbatimNumber.Cell(entry.Received, 0),
                VerbatimNumber.Cell(entry.Undecodable, 0),
                DecodeShare(entry.Undecodable, received),
            ]);
        }

        rows.Add(
        [
            "all ledgers",
            VerbatimNumber.Cell(totalReceived, 0),
            VerbatimNumber.Cell(undecodable, 0),
            totalReceived.Equals(0.0)
                ? TableLedger.NoDatagrams
                : VerbatimNumber.Cell(100.0 * undecodable / totalReceived, 4, " %"),
        ]);

        return
        [
            "### 14.6 UDP decode quality (target side, unattributable)",
            string.Empty,
            DecodeNote,
            string.Empty,
            MarkdownTable.Render(s_decodeHeaders, rows),
            string.Empty,
            DecodeTail,
            string.Empty,
        ];
    }

    /// <summary>One ledger's share of undecodable datagrams, or why it has none.</summary>
    private static string DecodeShare(double? undecodable, double received)
    {
        if (undecodable is null)
        {
            return TableLedger.NoUndecodableCounter;
        }

        return received.Equals(0.0)
            ? TableLedger.NoDatagrams
            : VerbatimNumber.Cell(100.0 * undecodable.Value / received, 4, " %");
    }

    private static Dictionary<string, int> TcpVerdictsOf(CampaignModel campaign, string passId)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var records = LedgerLoader.For(campaign, passId)
            .SelectMany(ledger => ledger.Records)
            .Where(record => string.Equals(
                JsonValue.String(record.Payload, Contracts.ArmKeys.Common.Record.Type),
                LedgerViewsBuilder.Tcp,
                StringComparison.Ordinal));
        foreach (var record in records)
        {
            var verdict = JsonText.Of(JsonValue.Member(record.Payload, Contracts.ArmKeys.Ledger.TcpRecord.Verdict));
            counts[verdict] = counts.GetValueOrDefault(verdict) + 1;
        }

        return counts;
    }

    private static Dictionary<string, double> ExpectedVerdicts(CampaignModel campaign, string passId)
    {
        var counts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in campaign.InPass(passId))
        {
            var (result, _) = ArmAccess.ArmResult(row, "REL");
            if (result is null || JsonValue.Member(result, "metrics") is not { } metrics)
            {
                continue;
            }

            if (JsonValue.Member(metrics, "expected") is not { ValueKind: JsonValueKind.Object } block)
            {
                continue;
            }

            foreach (var member in block.EnumerateObject())
            {
                counts[member.Name] = counts.GetValueOrDefault(member.Name) + (JsonValue.AsNumber(member.Value) ?? 0.0);
            }
        }

        return counts;
    }

    private static double? Count(Dictionary<string, double> counts, string verdict) =>
        counts.TryGetValue(verdict, out var value) ? value : null;

    private static double? Count(Dictionary<string, int> counts, string verdict) =>
        counts.TryGetValue(verdict, out var value) ? value : null;

    private static string Arms(HashSet<string> arms) =>
        arms.Count > 0 ? string.Join(", ", arms.Order(StringComparer.Ordinal)) : TableLedger.EndpointDash;

    /// <summary>The UDP half of §14.4's check, which a port with no client counterpart skips.</summary>
    private static string DnsUdpCheck(LedgerFindings.DnsPortTotals entry, double band, bool clientless)
    {
        if (clientless)
        {
            return "no client counterpart";
        }

        return Math.Abs(entry.LedgerUdp - entry.ClientUdp) <= band
            ? $"ok (±{VerbatimNumber.Cell(band, 0)})"
            : "MISMATCH";
    }

    /// <summary>The TCP half of §14.4's check, which a port with no client counterpart skips.</summary>
    private static string DnsTcpCheck(LedgerFindings.DnsPortTotals entry, bool clientless)
    {
        if (clientless)
        {
            return "no client counterpart";
        }

        return DescriptiveStats.WithinTolerance(
            entry.LedgerTcp,
            entry.ClientTcp,
            LedgerFindings.ConnectionTolerance,
            LedgerFindings.ConnectionSlack) == true
            ? "ok"
            : "MISMATCH";
    }

    private const string PartitionNote =
        "A datagram that arrives from an endpoint the row's configured path does not use is evidence that the "
        + "product carried traffic it was configured to pass, or passed traffic it was configured to carry. A "
        + "row with no window on one side of the partition cannot be checked this way and says so.";

    private const string DnsNote =
        "The target writes one `dnsSummary` per listener at shutdown, so it covers the ledger's whole "
        + "lifetime: for a campaign ledger that is every pass, and the client column is summed over every "
        + "pass to match, while a one-pass ledger simply covers that pass. The client column adds up every "
        + "DNS and DNSALT arm that targeted that port, dual lanes included, plus the MIX arm's DNS class, "
        + "which queries the run's own DNS port. A port the ledger reports that no client arm used says "
        + "`no client counterpart`.";

    private const string TcpNote =
        "The target's verdict and the client's own expectation are two independent readings of the same "
        + "connection; a large difference is a fidelity question for the report, not a gate.";

    private const string DecodeNote =
        "The target drops every datagram it cannot decode as a frame and counts it (`udpSummary`'s "
        + "`undecodable`, published again as `targetSummary/udp/undecodable`). That counter is the only "
        + "witness of corruption on the **request** path: a datagram the client sent but the target could not "
        + "read never comes back, so the client's own `corruptDatagrams` — a corrupted frame that *did* arrive "
        + "— cannot see it and the client can only report the result as path loss. The count is disclosed here "
        + "as a **target-side total and nothing else**: a datagram that fails to decode carries no sequence "
        + "number, so it belongs to no run and no arm, and no arm-level cell, rate or gate anywhere in this "
        + "file includes it. `lossRate`, `corruptRate` and `clientSendLoss` keep their own meanings and are "
        + "not adjusted by this number.";

    private const string DecodeTail =
        "A ledger is one target instance's lifetime, and the same file can span every pass, so its row above "
        + "is that instance's total and the `all ledgers` row sums the instances. A campaign whose targets "
        + "could not decode a single datagram prints no such table at all.";
}
