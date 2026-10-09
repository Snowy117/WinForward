namespace WinForward.E2E.Analysis.Findings;

/// <summary>The campaign's ledgers, read once.</summary>
internal sealed class LedgerViews
{
    /// <summary>Whether any pass has a ledger at all.</summary>
    internal required bool Available { get; init; }

    /// <summary>One view per pass that has a ledger.</summary>
    internal required IReadOnlyDictionary<string, LedgerPassView> Passes { get; init; }

    /// <summary>Per pass, how a record is attributed to a run, in the reference's words.</summary>
    internal required IReadOnlyDictionary<string, string> Attribution { get; init; }
}
