namespace WinForward.E2E.Analysis.Findings;

/// <summary>One pass's ledger, attributed arm by arm.</summary>
internal sealed class LedgerPassView
{
    /// <summary>The ledger files this pass read, in discovery order.</summary>
    internal required IReadOnlyList<string> Paths { get; init; }

    /// <summary>How many records the pass's ledgers hold.</summary>
    internal required int Records { get; init; }

    /// <summary>
    /// How many records of each <c>type</c>, keyed by the reference's own <c>str()</c> of the member
    /// and in first-occurrence order.
    /// </summary>
    /// <remarks>
    /// The reference counts the records by iterating a <b>set</b> of the types, so its member order is a
    /// hash-table artefact rather than a decision. Every reader sorts these keys before printing them
    /// (§14.1 does), and the semantic oracle ignores object member order, so the two orders are the same
    /// answer everywhere except a byte-for-byte comparison of the <c>types</c> object.
    /// </remarks>
    internal required IReadOnlyDictionary<string, int> Types { get; init; }

    /// <summary>How many records carry each <c>label</c>, in first-occurrence order.</summary>
    internal required IReadOnlyDictionary<string, int> Labels { get; init; }

    /// <summary>How many lines were not JSON.</summary>
    internal required int BadLines { get; init; }

    /// <summary>One entry per arm window.</summary>
    internal required IReadOnlyList<LedgerArmView> PerArm { get; init; }
}
