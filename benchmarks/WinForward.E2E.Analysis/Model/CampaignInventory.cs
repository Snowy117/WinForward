namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// What one campaign tree contains, as the loaders found it: the passes, the rows inside each pass,
/// and the ledger files those rows' traffic went to.
/// </summary>
/// <remarks>
/// <para><b>Built in batch 1a.</b> The reference's model is <c>CampaignContext</c>: every pass's rows,
/// each row's arms (its <c>result</c> record, its 1 Hz samples, its <c>armSummary</c>, its <c>error</c>
/// and <c>samplerError</c> records), the dual phase's two lanes, the ledgers and the environment
/// block. The fields below are the skeleton of that shape; what the loaders fill in and the order
/// they discover it in is the batch's own work, and the oracle judges it byte for byte.</para>
/// <para><b>The discovery order is part of the output.</b> §2 prints the ledger paths and §14 its
/// rows, so "which file came first" is compared text, not an implementation detail.</para>
/// </remarks>
internal sealed record CampaignInventory(
    IReadOnlyList<string> PassIds,
    int RowCount,
    IReadOnlyList<string> LedgerPaths,
    int LedgerRecords);
