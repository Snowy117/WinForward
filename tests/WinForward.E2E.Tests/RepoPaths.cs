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
    /// The Windows lightweight-validation plan: <c>full-plan</c>'s load shape at compressed arm
    /// durations, which is why it is a third shipped directory rather than a file inside one of the
    /// other two.
    /// </summary>
    internal static string WindowsPlansDirectory => Path.Combine(Root, "benchmarks", "WinForward.E2E", "scripts", "plans-windows");

    /// <summary>
    /// The minimal plans a fact loads by path: the Tier 0 defect repros, which are either rejected at
    /// load time or would need a live target, and the semantic fixtures that pin one plan key to one
    /// published value. They live with the tests, not in <c>scripts/plans/</c>, because none of them is
    /// a campaign plan.
    /// </summary>
    private static string Tier0PlansDirectory => Path.Combine(Root, "tests", "WinForward.E2E.Tests", "Fixtures", "plans");

    internal static string Tier0Plan(string name) => Path.Combine(Tier0PlansDirectory, name);

    /// <summary>
    /// The CLI's recorded text — the before/ tree from the last binary published before the two argument
    /// parsers became one shared walk, the after/ tree from the binary that carries it. The record has to
    /// outlive the change that took it, so the lookup follows the task directory under
    /// <c>.trellis/tasks/archive/&lt;month&gt;/</c> rather than breaking the suite the day the task is
    /// closed.
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
