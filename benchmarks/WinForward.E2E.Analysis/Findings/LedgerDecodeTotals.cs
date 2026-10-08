using System.Text.Json;
using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Findings;

/// <summary>One target instance's UDP decode quality over its whole lifetime.</summary>
internal sealed class DecodeTotals
{
    /// <summary>The datagrams the ledger reports receiving, or null when it reports none.</summary>
    internal double? Received { get; set; }

    /// <summary>The datagrams the ledger could not decode as a frame, or null when it counts none.</summary>
    internal double? Undecodable { get; set; }
}

/// <summary>
/// The ledger's own UDP decode counters, one entry per distinct ledger file.
/// </summary>
/// <remarks>
/// <para><b>Keyed by path, not by pass.</b> An <c>undecodable</c> count is a running total for the
/// ledger's whole lifetime, and the same ledger is attached to every pass it spans, so keying by pass
/// would count one target's datagrams once per pass.</para>
/// <para><b>The largest value wins.</b> Both the per-second <c>udpSummary</c> and the closing
/// <c>targetSummary/udp</c> block carry the counters, and the later record of a running total is the
/// higher one.</para>
/// </remarks>
internal static class LedgerDecodeTotals
{
    /// <summary>The per-ledger decode totals, in first-discovery order.</summary>
    internal static Dictionary<string, DecodeTotals> For(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var totals = new Dictionary<string, DecodeTotals>(StringComparer.Ordinal);
        foreach (var passId in campaign.PassIds)
        {
            foreach (var ledger in LedgerLoader.For(campaign, passId))
            {
                if (!totals.TryGetValue(ledger.Path, out var entry))
                {
                    entry = new DecodeTotals();
                    totals[ledger.Path] = entry;
                }

                foreach (var record in ledger.Records)
                {
                    var block = CounterBlock(record.Payload);
                    if (block is not { } payload)
                    {
                        continue;
                    }

                    entry.Received = Larger(entry.Received, JsonValue.Number(payload, Contracts.ArmKeys.Ledger.UdpSummary.Received));
                    entry.Undecodable = Larger(entry.Undecodable, JsonValue.Number(payload, Contracts.ArmKeys.Ledger.UdpSummary.Undecodable));
                }
            }
        }

        return totals;
    }

    /// <summary>Where one record publishes the counters: itself, or its <c>udp</c> block.</summary>
    private static JsonElement? CounterBlock(JsonElement payload)
    {
        var type = JsonValue.String(payload, Contracts.ArmKeys.Common.Record.Type);
        if (string.Equals(type, LedgerViewsBuilder.UdpSummary, StringComparison.Ordinal))
        {
            return payload;
        }

        var udp = JsonValue.Dig(payload, "udp");
        return string.Equals(type, LedgerViewsBuilder.TargetSummary, StringComparison.Ordinal)
            && udp is { ValueKind: JsonValueKind.Object }
            ? udp
            : null;
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
