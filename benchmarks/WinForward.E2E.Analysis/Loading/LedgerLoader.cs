using System.Runtime.CompilerServices;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Loading;

/// <summary>
/// Reads the target ledgers a pass's rows' traffic went to, once per campaign.
/// </summary>
/// <remarks>
/// <para><b>The ledger is the campaign's independent second opinion.</b> It is written by the target, not
/// by the client, so the analysis reads it to check the client's own counts against something that did
/// not produce them.</para>
/// <para><b>A line that is not JSON is counted, not thrown on.</b> The ledger is written while the
/// machine is under load, so a truncated last line is a fact about the run and is reported in §2 and in
/// the findings.</para>
/// </remarks>
internal static class LedgerLoader
{
    private static readonly ConditionalWeakTable<CampaignModel, Dictionary<string, IReadOnlyList<LedgerData>>> s_cache = [];

    /// <summary>One pass's ledgers, in discovery order; an empty list when the pass has none.</summary>
    internal static IReadOnlyList<LedgerData> For(CampaignModel campaign, string passId)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(passId);

        var cache = s_cache.GetValue(campaign, _ => new Dictionary<string, IReadOnlyList<LedgerData>>(StringComparer.Ordinal));
        if (cache.TryGetValue(passId, out var loaded))
        {
            return loaded;
        }

        var paths = campaign.LedgerPaths.TryGetValue(passId, out var found) ? found : [];
        loaded = [.. paths.Select(Read)];
        cache[passId] = loaded;
        return loaded;
    }

    private static LedgerData Read(string path)
    {
        var lines = JsonReader.ReadLines(path);
        var records = lines.Records
            .Select(record => new LedgerRecord(
                path,
                record,
                RunClocks.ParseUtc(record, Contracts.ArmKeys.Ledger.Envelope.Utc),
                0.0))
            .ToList();
        return new LedgerData(path, records, lines.BadLines, []);
    }
}
