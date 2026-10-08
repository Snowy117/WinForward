namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// One campaign tree, as the loaders found it: every pass's rows, the ledgers those rows' traffic went
/// to, and the parameters the analysis was asked to run with.
/// </summary>
/// <remarks>
/// <para><b>The discovery order is part of the output.</b> §2 prints the ledger paths and §14 its rows,
/// so "which file came first" is compared text rather than an implementation detail.</para>
/// <para><b>Rows are runs.</b> A row directory exists once per pass, so a three-pass tree holds three
/// <see cref="ClientRun"/>s of each row; see <see cref="RowIds"/> for the distinct set.</para>
/// </remarks>
internal sealed class CampaignModel
{
    /// <summary>The campaign tree, spelled the way the reference prints it.</summary>
    internal required string Raw { get; init; }

    /// <summary>Whether <c>--raw</c>'s immediate subdirectories are the rows of one implicit pass.</summary>
    internal required bool Flat { get; init; }

    /// <summary>Each pass's rows, keyed by pass id and in the order the directory listed them.</summary>
    internal required IReadOnlyDictionary<string, IReadOnlyList<ClientRun>> Passes { get; init; }

    /// <summary>
    /// Each pass's ledger files, in discovery order. A pass with no ledger has no entry, which is how
    /// §2 tells "the campaign keeps no ledger here" from "the ledger is empty".
    /// </summary>
    internal required IReadOnlyDictionary<string, IReadOnlyList<string>> LedgerPaths { get; init; }

    /// <summary>How many resamples each bootstrap interval is drawn with.</summary>
    internal required int Resamples { get; init; }

    /// <summary>The base seed each comparison's generator is derived from.</summary>
    internal required int Seed { get; init; }

    /// <summary>How many passes an aggregate needs before it is allowed to make a claim.</summary>
    internal required int MinPasses { get; init; }

    /// <summary>The passes, in natural-key order; <c>pass2</c> before <c>pass10</c>.</summary>
    internal IReadOnlyList<string> PassIds => field ??= NaturalKey.Sort(Passes.Keys);

    /// <summary>Every loaded run, pass by pass, in the order §15 prints them.</summary>
    internal IReadOnlyList<ClientRun> Rows => field ??= Flatten();

    /// <summary>
    /// The distinct rows the campaign ran, in the reference's own order: declared rows first, in
    /// <see cref="RowProfiles.Order"/>, then anything undeclared by <see cref="NaturalKey"/>.
    /// </summary>
    /// <remarks>
    /// This is *not* the number of loaded runs. The frozen three-pass tree holds 27 runs — the 27 lines
    /// §15 prints — and 9 rows here, because the same row directory is read once per pass. A count that
    /// says 27 answers "how many runs did the analysis read", which is not the question
    /// <c>verdict.json</c>'s <c>rows</c> or the run summary ask.
    /// </remarks>
    internal IReadOnlyList<string> RowIds => field ??= OrderRowIds();

    /// <summary>How many distinct rows the campaign ran.</summary>
    internal int RowCount => RowIds.Count;

    /// <summary>How many ledger files the campaign has, counted once each across the passes.</summary>
    internal int LedgerFileCount =>
        LedgerPaths.Values.SelectMany(paths => paths).Distinct(StringComparer.Ordinal).Count();

    private List<ClientRun> Flatten()
    {
        var rows = new List<ClientRun>();
        foreach (var passId in PassIds)
        {
            rows.AddRange(Passes[passId]);
        }

        return rows;
    }

    private List<string> OrderRowIds()
    {
        var seen = Rows.Select(run => run.RunId).Distinct(StringComparer.Ordinal).ToList();

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < RowProfiles.Order.Count; index++)
        {
            rank[RowProfiles.Order[index]] = index;
        }

        seen.Sort((left, right) =>
        {
            var order = Rank(rank, left).CompareTo(Rank(rank, right));
            return order != 0 ? order : NaturalKey.Compare(left, right);
        });
        return seen;
    }

    private static int Rank(Dictionary<string, int> rank, string rowId) =>
        rank.TryGetValue(rowId, out var index) ? index : rank.Count;
}
