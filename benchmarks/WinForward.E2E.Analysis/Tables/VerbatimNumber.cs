namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// The reference's number formatting, reproduced: <c>%.*f</c> with round-half-to-even on the decimal
/// value, falling back to <c>%.3g</c> when the fixed form would print a non-zero value as zero, with
/// a lower-case exponent of at least two digits and no thousands separator, no padding and no
/// alignment.
/// </summary>
/// <remarks>
/// <para><b>Completed in batch 1b, with unit tests.</b> .NET's own formatting differs in both places
/// that matter — its <c>F</c> rounding is not half-to-even on binary doubles, and its <c>G</c> prints
/// an upper-case exponent with as few digits as it can — and every number in <c>tables.md</c> is
/// compared as text (D20.5).</para>
/// <para>The same file owns the cell renderers built on it: <c>median [p25–p75] (n=K)</c> with the
/// en dash, the rule-of-three <c>&lt; 3/n</c> cell for an all-zero population, the three tails
/// (<c>(n=1)</c>, <c>(n=K)</c>, <c>(n=K of T; j null, m unavailable)</c>), the empty cell for a reading
/// the harness wrote as JSON null, and <c>n/a (&lt;reason&gt;)</c> for everything else. Those are
/// rendering *rules*, not incidentals: the golden was frozen from them, and
/// <c>.trellis/tasks/10-07-e2e-harness-refactor/research/python-oracle-changes.md</c> §5 lists the ones
/// that look like defects and must survive anyway.</para>
/// </remarks>
internal static class VerbatimNumber
{
    /// <summary>What this file still owes, for the run summary a caller reads.</summary>
    internal const string Pending = "1b %.3g / %.*f number formatting and the cell renderers";
}
