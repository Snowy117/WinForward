using System.Text.Json;
using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Findings;

/// <summary>One target instance's frame-truncation counters over its whole lifetime.</summary>
internal sealed class TruncationTotals
{
    /// <summary>Frames the peer's close cut off mid-frame on the TCP echo listener, or null when uncounted.</summary>
    internal double? TcpFrames { get; set; }

    /// <summary>
    /// Each DNS listener's own length-prefix short reads, keyed by the port that listener published: two
    /// listeners keep two counts, so the port is what tells them apart.
    /// </summary>
    internal SortedDictionary<string, double> DnsFrames { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether this target cut anything off at all, which is what §14.7 discloses.</summary>
    internal bool Any =>
        TcpFrames is > 0.0 || DnsFrames.Values.Any(frames => frames > 0.0);
}

/// <summary>
/// The ledger's own truncation counters, one entry per distinct ledger file: the TCP echo listener's
/// frames cut off by the peer's close and each DNS listener's length-prefix short reads.
/// </summary>
/// <remarks>
/// <para><b>Two mechanisms share one leaf name.</b> <c>truncatedFrames</c> sits at the root of both
/// <c>tcpSummary</c> and <c>dnsSummary</c>, and each family's value is read only against its own
/// mechanism (D19.3 C); they are kept in two separate slots here for exactly that reason.</para>
/// <para><b>Keyed by path, not by pass.</b> The counter is a running total for the ledger's whole
/// lifetime and the same ledger is attached to every pass it spans, so keying by pass would count one
/// target's frames once per pass. The largest value seen wins, which is the later record of a running
/// total — the same rule <see cref="LedgerDecodeTotals"/> reads <c>undecodable</c> with.</para>
/// <para><b>Both levels are read and neither is trusted alone.</b> A <c>tcpSummary</c> or
/// <c>dnsSummary</c> record carries the counter at its own root and the closing <c>targetSummary</c>
/// repeats it under <c>tcp</c>, <c>dns</c> and <c>dnsAlt</c>; the maximum over all of them is the
/// target's own total.</para>
/// </remarks>
internal static class LedgerTruncationTotals
{
    /// <summary>The per-ledger truncation totals, in first-discovery order.</summary>
    internal static Dictionary<string, TruncationTotals> For(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var totals = new Dictionary<string, TruncationTotals>(StringComparer.Ordinal);
        foreach (var passId in campaign.PassIds)
        {
            foreach (var ledger in LedgerLoader.For(campaign, passId))
            {
                if (!totals.TryGetValue(ledger.Path, out var entry))
                {
                    entry = new TruncationTotals();
                    totals[ledger.Path] = entry;
                }

                foreach (var record in ledger.Records)
                {
                    Stamp(entry, record.Payload);
                }
            }
        }

        return totals;
    }

    /// <summary>Moves one record's counters into its ledger's totals.</summary>
    private static void Stamp(TruncationTotals entry, JsonElement payload)
    {
        var type = JsonValue.String(payload, Contracts.ArmKeys.Common.Record.Type);
        if (string.Equals(type, LedgerViewsBuilder.TcpSummary, StringComparison.Ordinal))
        {
            entry.TcpFrames = Larger(
                entry.TcpFrames,
                JsonValue.Number(payload, Contracts.ArmKeys.Ledger.TcpSummary.TruncatedFrames));
            return;
        }

        if (string.Equals(type, LedgerViewsBuilder.DnsSummary, StringComparison.Ordinal))
        {
            DnsListener(entry, payload);
            return;
        }

        if (!string.Equals(type, LedgerViewsBuilder.TargetSummary, StringComparison.Ordinal))
        {
            return;
        }

        if (JsonValue.Dig(payload, Contracts.ArmKeys.Ledger.TargetSummary.Tcp) is { ValueKind: JsonValueKind.Object } tcp)
        {
            entry.TcpFrames = Larger(
                entry.TcpFrames,
                JsonValue.Number(tcp, Contracts.ArmKeys.Ledger.TargetSummary.TcpTotals.TruncatedFrames));
        }

        foreach (var listener in new[]
                 {
                     Contracts.ArmKeys.Ledger.TargetSummary.Dns,
                     Contracts.ArmKeys.Ledger.TargetSummary.DnsAlt,
                 })
        {
            if (JsonValue.Dig(payload, listener) is { ValueKind: JsonValueKind.Object } block)
            {
                DnsListener(entry, block);
            }
        }
    }

    /// <summary>One DNS listener's block: its port, and the short reads it counted.</summary>
    private static void DnsListener(TruncationTotals entry, JsonElement block)
    {
        if (JsonValue.Number(block, Contracts.ArmKeys.Ledger.DnsSummary.TruncatedFrames) is not { } frames)
        {
            return;
        }

        var port = JsonText.Of(JsonValue.Member(block, Contracts.ArmKeys.Ledger.DnsSummary.Port));
        entry.DnsFrames[port] = entry.DnsFrames.TryGetValue(port, out var current)
            ? Math.Max(current, frames)
            : frames;
    }

    private static double? Larger(double? current, double? value)
    {
        if (value is null)
        {
            return current;
        }

        return current is null ? value : Math.Max(current.Value, value.Value);
    }
}
