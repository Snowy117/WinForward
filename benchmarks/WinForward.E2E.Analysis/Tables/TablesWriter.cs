namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// Writes <c>tables.md</c>: one section per `## N.` heading, in the reference's own order, with the
/// batch that owns each section's body.
/// </summary>
/// <remarks>
/// <para><b>A section is a slice of the oracle.</b> The comparison between the two implementations
/// cuts <c>tables.md</c> at these headings and compares each section on its own, so the headings — not
/// just their bodies — are the batch boundaries: all sixteen exist from the first commit, their
/// bodies carry <c>&lt;!-- TODO(batch N) --&gt;</c> until the batch that owns them renders them
/// (D20.2). A missing heading would be reported as a missing slice, which is the one failure the
/// differ must never confuse with a pass.</para>
/// <para><b>The preamble is a slice too.</b> Everything before <c>## 0.</c> — the title, the
/// program-name line and the section-1 pointer — belongs to batch 1a, and the line that names the
/// program is neutral on purpose: it names neither implementation, so the two can be compared
/// (D6.4/D20.7).</para>
/// </remarks>
internal static class TablesWriter
{
    /// <summary>The batch that owns the preamble, the text before the first heading.</summary>
    private const string PreambleBatch = "1a";

    /// <summary>
    /// Every section, in write order: the heading as the reference spells it (the differ matches
    /// <c>^## (\d+)\.</c> and compares the whole section, heading included) and the batch that
    /// renders its body.
    /// </summary>
    private static readonly (string Heading, string Batch)[] s_sections =
    [
        ("## 0. Correctness findings", "2"),
        ("## 1. What each row is and what it does with UDP", "1c"),
        ("## 2. Environment and provenance", "1c"),
        ("## 3. Gate table (one row per pass and row)", "2"),
        ("## 4. Headline matrix", "4"),
        ("## 5. Latency detail", "3"),
        ("## 6. CPU detail", "4"),
        ("## 7. Memory detail", "4"),
        ("## 8. UDP accuracy detail", "3"),
        ("## 9. DNS comparability detail", "3"),
        ("## 10. Long-lived connection detail (`PERSIST`)", "4"),
        ("## 11. TCP reliability detail", "4"),
        ("## 12. Dual-phase detail (proxied vs direct lane)", "5"),
        ("## 13. Control block comparison (`control-pre` vs `control-post`)", "5"),
        ("## 14. Target-ledger cross-check", "5"),
        ("## 15. Data availability", "1a"),
    ];

    /// <summary>Writes the document: what has been rendered, then a placeholder for the rest.</summary>
    /// <param name="writer">Where the document goes; the caller owns the encoding and the newline.</param>
    /// <param name="rendered">
    /// Section number (or <c>preamble</c>) to its body, or <see langword="null"/> while no batch
    /// has rendered one: every section then carries its placeholder.
    /// </param>
    internal static void Write(TextWriter writer, IReadOnlyDictionary<string, string>? rendered = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (rendered is not null && rendered.TryGetValue("preamble", out var preamble))
        {
            writer.Write(preamble);
        }
        else
        {
            WritePlaceholder(writer, PreambleBatch);
        }

        foreach (var (heading, batch) in s_sections)
        {
            writer.Write(heading);
            writer.Write('\n');
            writer.Write('\n');
            var number = heading[3..heading.IndexOf('.', StringComparison.Ordinal)];
            if (rendered is not null && rendered.TryGetValue(number, out var body))
            {
                writer.Write(body);
            }
            else
            {
                WritePlaceholder(writer, batch);
            }

            writer.Write('\n');
        }
    }

    /// <summary>The one placeholder a section body carries until its batch renders it.</summary>
    private static void WritePlaceholder(TextWriter writer, string batch)
    {
        writer.Write("<!-- TODO(batch ");
        writer.Write(batch);
        writer.Write(") -->\n");
    }
}
