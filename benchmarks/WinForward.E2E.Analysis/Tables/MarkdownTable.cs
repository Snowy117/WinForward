namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// Renders one markdown table: a header row, a separator row, then one row per record — every cell
/// padded with single spaces, nothing aligned, nothing truncated, and no cell count enforced against
/// the header.
/// </summary>
/// <remarks>
/// <para><b>The cell count is not checked on purpose.</b> The reference's tables carry rows whose cell
/// count disagrees with their header, and a renderer that padded or refused such a row would differ
/// from the published output and lose the oracle.</para>
/// <para><b>The separator is exactly <c>|---|---|</c>…</b> for the header's arity: no spaces, no
/// alignment colons, no trailing space.</para>
/// </remarks>
internal static class MarkdownTable
{
    /// <summary>Renders a table from its header and its rows; either may be empty.</summary>
    internal static string Render(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var lines = new List<string>(rows.Count + 2)
        {
            "| " + string.Join(" | ", headers) + " |",
            "|" + string.Join('|', Enumerable.Repeat("---", headers.Count)) + "|",
        };

        foreach (var row in rows)
        {
            lines.Add("| " + string.Join(" | ", row) + " |");
        }

        return string.Join('\n', lines);
    }

    /// <summary>One cell value repeated for a row that has no numbers to print.</summary>
    internal static string[] Repeated(int count, string value)
    {
        var cells = new string[count];
        Array.Fill(cells, value);
        return cells;
    }
}
