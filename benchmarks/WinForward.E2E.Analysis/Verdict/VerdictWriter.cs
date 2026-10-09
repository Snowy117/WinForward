namespace WinForward.E2E.Analysis.Verdict;

/// <summary>
/// Writes <c>verdict.json</c>: the top-level keys the reference publishes, in the reference's own
/// order, each one only once the batch that owns it has rendered it.
/// </summary>
/// <remarks>
/// <para><b>An unimplemented key is absent, never empty.</b> The oracle slices this file by top-level
/// key, and a key that is missing is reported as a *missing slice* (exit code 2) rather than compared
/// as an empty object — which is what makes the batch boundary enforceable: a key the C# side has not
/// reached yet cannot be mistaken for one it renders as nothing.</para>
/// <para><b>Order is output.</b> The reference writes with <c>sort_keys=False</c>, so the order below
/// is part of the compared bytes and is the reference's own declaration order.</para>
/// </remarks>
internal static class VerdictWriter
{
    /// <summary>Every top-level key, in write order, with the batch that renders it.</summary>
    private static readonly (string Key, string Batch)[] s_topLevelKeys =
    [
        ("generated_by", "1a"),
        ("raw", "1a"),
        ("flat_mode", "1a"),
        ("passes", "1a"),
        ("rows", "1a"),
        ("row_profiles", "1c"),
        ("bootstrap", "1b"),
        ("thresholds", "1b"),
        ("findings", "2"),
        ("findings_by_severity", "2"),
        ("control_blocks", "5"),
        ("dual_phase", "5"),
        ("ledger", "5"),
        ("metrics", "3/4"),
    ];

    /// <summary>Writes the object from the values a batch has already rendered.</summary>
    /// <param name="writer">Where the document goes; the caller owns the encoding and the newline.</param>
    /// <param name="rendered">
    /// Key to its already-formatted JSON text: a key of <c>s_topLevelKeys</c> that is not here is left
    /// out of the document entirely, and one that is here is written at its own position.
    /// </param>
    internal static void Write(TextWriter writer, IReadOnlyDictionary<string, string> rendered)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rendered);

        var written = s_topLevelKeys.Where(entry => rendered.ContainsKey(entry.Key)).ToArray();
        writer.Write("{\n");
        for (var index = 0; index < written.Length; index++)
        {
            var key = written[index].Key;
            writer.Write("  \"");
            writer.Write(key);
            writer.Write("\": ");
            writer.Write(rendered[key]);
            writer.Write(index + 1 < written.Length ? ",\n" : "\n");
        }

        writer.Write("}\n");
    }
}
