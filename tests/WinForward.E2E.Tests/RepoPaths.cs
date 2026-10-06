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
