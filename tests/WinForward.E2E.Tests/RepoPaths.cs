namespace WinForward.E2E.Tests;

/// <summary>
/// Locates the repository from the test assembly's output directory, so a test can read the
/// shipped plans the way the released harness does instead of a copy that can drift.
/// </summary>
internal static class RepoPaths
{
    private static string Root { get; } = FindRoot();

    internal static string PlansDirectory => Path.Combine(Root, "benchmarks", "WinForward.E2E", "scripts", "plans");

    internal static string ShortPlansDirectory => Path.Combine(Root, "benchmarks", "WinForward.E2E", "scripts", "plans-short");

    /// <summary>
    /// The minimal plans that reproduce the Tier 0 defects (D1-D7). They live with the tests, not in
    /// <c>scripts/plans/</c>: every one of them is either rejected at load time or would need a live
    /// target, so shipping them next to the campaign plans would invite running them.
    /// </summary>
    private static string Tier0PlansDirectory => Path.Combine(Root, "tests", "WinForward.E2E.Tests", "Fixtures", "plans");

    internal static string Tier0Plan(string name) => Path.Combine(Tier0PlansDirectory, name);

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinForward.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No WinForward.slnx was found above {AppContext.BaseDirectory}, so the shipped plans were not validated.");
    }
}
