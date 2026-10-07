namespace WinForward.E2E.Tests;

/// <summary>
/// Locates the repository from the test assembly's output directory, so a test can read the
/// shipped plans the way the released harness does instead of a copy that can drift.
/// </summary>
internal static class RepoPaths
{
    internal static string Root { get; } = FindRoot();

    internal static string PlansDirectory => Path.Combine(Root, "benchmarks", "WinForward.E2E", "scripts", "plans");

    internal static string ShortPlansDirectory => Path.Combine(Root, "benchmarks", "WinForward.E2E", "scripts", "plans-short");

    /// <summary>
    /// The minimal plans that reproduce the Tier 0 defects (D1-D7). They live with the tests, not in
    /// <c>scripts/plans/</c>: every one of them is either rejected at load time or would need a live
    /// target, so shipping them next to the campaign plans would invite running them.
    /// </summary>
    private static string Tier0PlansDirectory => Path.Combine(Root, "tests", "WinForward.E2E.Tests", "Fixtures", "plans");

    internal static string Tier0Plan(string name) => Path.Combine(Tier0PlansDirectory, name);

    /// <summary>
    /// The CLI's recorded text (research/cli-snapshots: the before/ tree from the last binary
    /// published before E2-d, the after/ tree from the one it produced), which lives with the
    /// parent task's research rather than with the tests because the record has to outlive the
    /// batch that took it. Archiving a task moves its whole directory under
    /// <c>.trellis/tasks/archive/&lt;month&gt;/</c>, so the lookup follows it there instead of
    /// breaking the suite the day the task is closed.
    /// </summary>
    internal static string CliSnapshotsDirectory { get; } = FindCliSnapshots();

    private const string HarnessTask = "10-07-e2e-harness-refactor";

    private static string FindCliSnapshots()
    {
        var live = Path.Combine(Root, ".trellis", "tasks", HarnessTask, "research", "cli-snapshots");
        if (Directory.Exists(live))
        {
            return live;
        }

        var archived = Directory
            .GetDirectories(Path.Combine(Root, ".trellis", "tasks", "archive"), HarnessTask, SearchOption.AllDirectories)
            .Select(task => Path.Combine(task, "research", "cli-snapshots"))
            .FirstOrDefault(Directory.Exists);

        return archived ?? throw new InvalidOperationException($"The CLI snapshots are in neither {live} nor the archive under .trellis/tasks/archive.");
    }

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
