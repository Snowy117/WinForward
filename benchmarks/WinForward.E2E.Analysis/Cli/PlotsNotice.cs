namespace WinForward.E2E.Analysis.Cli;

/// <summary>
/// Writes <c>plots/SKIPPED.md</c>: the fixed statement that this analysis draws no plot, written on
/// every run rather than only when something failed.
/// </summary>
/// <remarks>
/// <para><b>The text is fixed and is compared with its frozen copy.</b>
/// <c>verification/plots-SKIPPED.md</c> holds the same bytes, so the notice cannot drift into a
/// description of an implementation detail; <c>plots/</c> itself is not part of the oracle's diff, and
/// the reference's own notice names the interpreter it would need and is deliberately different.</para>
/// <para><b>No plotting dependency.</b> The seven plots the previous implementation drew are not
/// reproduced anywhere in this project; their inputs are all still published in <c>tables.md</c>.</para>
/// </remarks>
internal static class PlotsNotice
{
    private const string Text = """
        # Plots are not generated

        The C# analysis writes `tables.md` and `verdict.json`, and nothing else: it renders no PNG and has
        no plotting dependency. This file is written unconditionally, together with those two, so the
        absence of `plots/` is stated rather than left to be inferred from an empty directory.

        No number in either file comes from a plot. Every cell is computed from the campaign's own records:
        the arm result records and sample series, `run.json`, the target ledgers and `environment.json`.

        The seven plots the previous implementation drew — latency percentiles per arm, loss rates, the
        reliability outcome mix, MIX private bytes, CPU per arm, the LAT p99 per pass and the dual-phase
        direct-vs-proxied lanes — are not reproduced here. Their inputs are all still in `tables.md`, so the
        plots can be rebuilt from it by any tool that reads markdown, without re-reading the raw tree.

        """;

    /// <summary>Writes the notice into <paramref name="plotsDirectory"/>, creating it if needed.</summary>
    internal static void Write(string plotsDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(plotsDirectory);

        Directory.CreateDirectory(plotsDirectory);
        using var writer = new StreamWriter(
            Path.Combine(plotsDirectory, "SKIPPED.md"),
            append: false,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n";
        writer.Write(Text);
    }
}
