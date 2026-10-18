using System.Diagnostics;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The calibration control's staging path: <c>-Rows BASE-singbox -DryRun</c> prints the exact client
/// command and names every missing file without starting a process or touching the machine.
/// </summary>
[Collection(CalibrationControlCollection.Name)]
public sealed class OrchestratorCalibrationControlTests
{
    [Fact]
    public void TheBaseSingboxDryRunPrintsTheSocks5ClientCommandWithoutStartingIt()
    {
        var staging = Staging();
        try
        {
            var (exitCode, output) = Run(
                staging,
                "-Rows", "BASE-singbox",
                "-Passes", "1",
                "-DryRun");

            Assert.Equal(0, exitCode);
            Assert.Contains("BASE-singbox", output, StringComparison.Ordinal);
            Assert.Contains("--socks5 127.0.0.1:1080", output, StringComparison.Ordinal);
            Assert.Contains("base-singbox-plan.json", output, StringComparison.Ordinal);
            Assert.Contains("dry run passed", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(staging.Root, recursive: true);
        }
    }

    [Fact]
    public void TheDryRunNamesAMissingControlPlanAndExitsOne()
    {
        var staging = Staging();
        try
        {
            File.Delete(Path.Combine(staging.PlanRoot, "base-singbox-plan.json"));

            var (exitCode, output) = Run(
                staging,
                "-Rows", "BASE-singbox",
                "-Passes", "1",
                "-DryRun");

            Assert.Equal(1, exitCode);
            Assert.Contains("missing:", output, StringComparison.Ordinal);
            Assert.Contains("base-singbox-plan.json", output, StringComparison.Ordinal);
            Assert.Contains("dry run FAILED", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(staging.Root, recursive: true);
        }
    }

    private static (int exitCode, string output) Run(StagingPaths staging, params string[] arguments)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoPaths.Root,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Path.Combine(RepoPaths.Root, "benchmarks", "WinForward.E2E", "scripts", "orchestrator.ps1"));
        start.ArgumentList.Add("-OutRoot");
        start.ArgumentList.Add(staging.OutRoot);
        start.ArgumentList.Add("-PlanRoot");
        start.ArgumentList.Add(staging.PlanRoot);
        start.ArgumentList.Add("-ClientExe");
        start.ArgumentList.Add(staging.ClientExe);
        start.ArgumentList.Add("-SingBoxExe");
        start.ArgumentList.Add(staging.SingBoxExe);
        start.ArgumentList.Add("-SingBoxConfig");
        start.ArgumentList.Add(staging.SingBoxConfig);
        start.ArgumentList.Add("-SingBoxSocks5Address");
        start.ArgumentList.Add("127.0.0.1");
        start.ArgumentList.Add("-SingBoxSocks5Port");
        start.ArgumentList.Add("1080");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private static StagingPaths Staging()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wf-orchestrator-{Guid.NewGuid():N}");
        var planRoot = Path.Combine(root, "plans");
        Directory.CreateDirectory(planRoot);
        Directory.CreateDirectory(Path.Combine(root, "results"));
        var client = Path.Combine(root, OperatingSystem.IsWindows() ? "client.exe" : "client");
        var singBox = Path.Combine(root, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
        var config = Path.Combine(root, "sing-box.json");
        File.WriteAllText(client, string.Empty);
        File.WriteAllText(singBox, string.Empty);
        File.WriteAllText(config, string.Empty);
        File.Copy(
            Path.Combine(RepoPaths.PlansDirectory, "base-singbox-plan.json"),
            Path.Combine(planRoot, "base-singbox-plan.json"));

        return new StagingPaths(root, planRoot, Path.Combine(root, "results"), client, singBox, config);
    }

    private sealed record StagingPaths(string Root, string PlanRoot, string OutRoot, string ClientExe, string SingBoxExe, string SingBoxConfig);
}

/// <summary>
/// The two calibration-control facts spawn a subprocess (pwsh or the analysis binary), so they run
/// outside the parallel batch: the harness's allocation and latency controls are timing-sensitive.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CalibrationControlCollection
{
    internal const string Name = "calibration-control-processes";
}
