using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// The target-ledger cross-check: the frames the targets had cut off in half, one table per
/// mechanism and one row per target and pass.
/// </summary>
/// <remarks>
/// <para><b>Two mechanisms share one leaf name.</b> The TCP echo listener counts a connection the
/// peer's close cut off in the middle of a frame; a DNS listener counts a message its own two-byte
/// length prefix read short. They are rendered apart rather than added together, and each
/// listener's DNS count is listed under its own port.</para>
/// <para><b>Per target and per pass, never per arm.</b> A truncated frame carries no sequence number,
/// so it belongs to no run and no arm: the counters are the target's own running totals, printed as
/// the same target-and-pass row its ledger is listed with elsewhere, and no cell, rate or gate in
/// this section includes them. A tree in which no target truncated anything prints no subsection at
/// all, which is the same condition one mechanism further out.</para>
/// </remarks>
internal static class TableLedgerTruncated
{
    private static readonly string[] s_truncatedTcpHeaders =
        ["pass", "ledger", "truncated frames"];

    private static readonly string[] s_truncatedDnsHeaders =
        ["pass", "ledger", "dns port", "truncated frames"];

    /// <summary>The subsection's own lines, or nothing when no target truncated a frame.</summary>
    internal static List<string> Truncated(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var totals = LedgerTruncationTotals.For(campaign);
        var rows = PassRows(campaign, totals);
        var tcp = rows.Where(row => row.Tcp > 0.0).ToList();
        var dns = rows.Where(row => row.Listeners.Count > 0).ToList();
        if (tcp.Count == 0 && dns.Count == 0)
        {
            return [];
        }

        var lines = new List<string>
        {
            "### 14.7 Truncated frames (target side, unattributable)",
            string.Empty,
            TruncationNote,
            string.Empty,
        };

        if (tcp.Count > 0)
        {
            lines.Add(MarkdownTable.Render(
                s_truncatedTcpHeaders,
                [.. tcp.Select(row => (IReadOnlyList<string>)
                    [row.Pass, row.Ledger, VerbatimNumber.Cell(row.Tcp, 0)])]));
            lines.Add(string.Empty);
            lines.Add(TcpTruncationNote);
            lines.Add(string.Empty);
        }

        if (dns.Count > 0)
        {
            lines.Add(MarkdownTable.Render(
                s_truncatedDnsHeaders,
                [.. dns.SelectMany(row => row.Listeners
                    .Select(listener => (IReadOnlyList<string>)
                        [row.Pass, row.Ledger, listener.Key, VerbatimNumber.Cell(listener.Value, 0)]))]));
            lines.Add(string.Empty);
            lines.Add(DnsTruncationNote);
            lines.Add(string.Empty);
        }

        return lines;
    }

    /// <summary>One ledger's totals as the passes it is attached to see it.</summary>
    private static List<TruncationRow> PassRows(
        CampaignModel campaign,
        Dictionary<string, TruncationTotals> totals)
    {
        var rows = new List<TruncationRow>();
        foreach (var passId in campaign.PassIds)
        {
            var paths = LedgerLoader.For(campaign, passId)
                .Select(ledger => ledger.Path)
                .Where(path => totals.TryGetValue(path, out var entry) && entry.Any);
            rows.AddRange(paths.Select(path => new TruncationRow(
                passId,
                path,
                totals[path].TcpFrames ?? 0.0,
                [.. totals[path].DnsFrames.Where(listener => listener.Value > 0.0)])));
        }

        return rows;
    }

    /// <summary>One (pass, ledger) row of the truncated-frames table: the target's TCP total, and its per-listener DNS totals.</summary>
    private sealed record TruncationRow(
        string Pass,
        string Ledger,
        double Tcp,
        IReadOnlyList<KeyValuePair<string, double>> Listeners);

    private const string TruncationNote =
        "A frame the peer's close cut in half, and a DNS message a length prefix read short, are the same "
        + "kind of disclosure as a datagram that failed to decode: the count is the **target's own**, it "
        + "carries no sequence number and therefore no run and no arm, and it is published here as a "
        + "target-and-pass total. The counter is a running total for the ledger's whole lifetime, so a "
        + "ledger that spans several passes repeats its number under each of them rather than dividing it. "
        + "`truncatedFrames` reaches the ledger at two levels — the `tcpSummary` and `dnsSummary` records "
        + "carry it at their own root and the closing `targetSummary` repeats it under `tcp`, `dns` and "
        + "`dnsAlt` — so the two tables are also a cross-check of that repetition: a target that cut nothing "
        + "off is not listed, and a tree in which no target did prints no subsection here at all.";

    private const string TcpTruncationNote =
        "**TCP — a frame cut off by the peer's close.** The echo listener's frame reader ends on a partial "
        + "frame when the peer closes its send side mid-frame, and counts the connection in "
        + "`tcpSummary/truncatedFrames`. The cut-off connection keeps the target's existing `protocolError` "
        + "verdict: the trailer it never received is not a verdict of its own, so the verdict distribution "
        + "already carries it and no arm is named by the count.";

    private const string DnsTruncationNote =
        "**DNS — a length-prefix short read.** A DNS listener reads a two-byte length prefix and then that "
        + "many bytes, so either read ending short is this listener's own `dnsSummary/truncatedFrames`. "
        + "It is a different mechanism from the frame reader's, which is why it is listed per "
        + "listener with its port: two listeners keep two counts, and neither is the echo listener's.";
}
