using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Findings;

/// <summary>
/// The DNS port totals the ledger's <c>dnsSummary</c> records and the client's own arms are read into:
/// one entry per port, with both sides' query counts and the summary granularity that bounds them.
/// </summary>
internal static partial class LedgerFindings
{
    internal sealed class DnsPortTotals
    {
        /// <summary>UDP queries the ledger's summaries report.</summary>
        internal double LedgerUdp { get; set; }

        /// <summary>TCP queries the ledger's summaries report.</summary>
        internal double LedgerTcp { get; set; }

        /// <summary>Datagrams the client says it sent to this port.</summary>
        internal double ClientUdp { get; set; }

        /// <summary>TCP queries the client says it sent to this port.</summary>
        internal double ClientTcp { get; set; }

        /// <summary>How long the arms that used this port ran.</summary>
        internal double DurationSeconds { get; set; }

        /// <summary>How many shutdown summaries the numbers come from.</summary>
        internal int Summaries { get; set; }
    }

    /// <summary>One row's endpoints, split by the path the arm that saw them was configured to take.</summary>

    private static List<ClientRun> EveryRun(CampaignModel campaign)
    {
        var runs = new List<ClientRun>();
        foreach (var pass in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(pass))
            {
                runs.Add(row);
                runs.AddRange(new[] { row.DualProxied, row.DualDirect }.Where(lane => lane is not null)!);
            }
        }

        return runs;
    }

    /// <summary>One DNS port's ledger totals and the client totals they are read against.</summary>
    private static Dictionary<string, DnsPortTotals> DnsTotals(CampaignModel campaign, string passId)
    {
        var totals = new Dictionary<string, DnsPortTotals>(StringComparer.Ordinal);
        var ledgers = LedgerLoader.For(campaign, passId);
        if (ledgers.Count == 0)
        {
            return totals;
        }

        var summaries = ledgers
            .SelectMany(ledger => ledger.Records)
            .Select(record => record.Payload)
            .Where(payload => string.Equals(
                JsonValue.String(payload, "type"),
                LedgerViewsBuilder.DnsSummary,
                StringComparison.Ordinal));
        foreach (var payload in summaries)
        {
            var entry = Slot(totals, JsonText.Of(JsonValue.Member(payload, Contracts.ArmKeys.Ledger.DnsSummary.Port)));
            entry.LedgerUdp += JsonValue.Number(payload, Contracts.ArmKeys.Ledger.DnsSummary.UdpQueries) ?? 0.0;
            entry.LedgerTcp += JsonValue.Number(payload, Contracts.ArmKeys.Ledger.DnsSummary.TcpQueries) ?? 0.0;
            entry.Summaries++;
        }

        foreach (var run in EveryRun(campaign))
        {
            AddDnsArmTotals(totals, run);
            AddMixTotals(totals, run);
        }

        return totals;
    }

    /// <summary>The DNS and DNSALT arms' own client counts for one port.</summary>
    private static void AddDnsArmTotals(Dictionary<string, DnsPortTotals> totals, ClientRun run)
    {
        foreach (var armName in new[] { "DNS", "DNSALT" })
        {
            var (result, _) = ArmAccess.ArmResult(run, armName);
            var port = result is null ? null : JsonValue.Dig(result, "parameters/dnsPort");
            if (result is null || port is null)
            {
                continue;
            }

            var entry = Slot(totals, JsonText.Of(port));
            entry.ClientUdp += JsonValue.AsNumber(JsonValue.Dig(result, "metrics/udp.sent")) ?? 0.0;
            entry.ClientTcp += JsonValue.AsNumber(JsonValue.Dig(result, "metrics/tcpSent")) ?? 0.0;
            entry.DurationSeconds += RunClocks.ArmWindowSeconds(run, armName) ?? 0.0;
        }
    }

    /// <summary>The MIX arm's DNS queries, which go to the run's own target port.</summary>
    private static void AddMixTotals(Dictionary<string, DnsPortTotals> totals, ClientRun run)
    {
        var (mix, _) = ArmAccess.ArmResult(run, "MIX");
        var runDnsPort = mix is null ? null : JsonValue.Dig(run.Document, "target/dnsPort");
        var mixDns = mix is null ? null : JsonValue.AsNumber(JsonValue.Dig(mix, "metrics/classes/dns/sent"));
        if (runDnsPort is null || mixDns is null)
        {
            return;
        }

        var entry = Slot(totals, JsonText.Of(runDnsPort));
        entry.ClientUdp += mixDns.Value;
        entry.DurationSeconds = Math.Max(entry.DurationSeconds, RunClocks.ArmWindowSeconds(run, "MIX") ?? 0.0);
    }

    private static DnsPortTotals Slot(Dictionary<string, DnsPortTotals> totals, string port)
    {
        if (!totals.TryGetValue(port, out var entry))
        {
            entry = new DnsPortTotals();
            totals[port] = entry;
        }

        return entry;
    }
}
