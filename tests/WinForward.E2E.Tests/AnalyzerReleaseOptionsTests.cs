using System.Diagnostics;
using WinForward.E2E.Analysis.Release;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The release spellings of the command line, through <see cref="ReleaseOptions"/>, which the analysis
/// walks before it reads a legacy argument. A release flag is never silently ignored: each refusal
/// names the argument the caller has to change.
/// </summary>
public sealed class AnalyzerReleaseOptionsTests
{
    [Fact]
    public void NoReleaseFlagParsesAsALegacyInvocation()
    {
        Assert.True(ReleaseOptions.TryParse(["--raw", "raw", "--flat"], out var options, out var error), error);

        Assert.False(options.Release);
        Assert.Null(options.ThresholdsPath);
        Assert.Null(options.MinPasses);
        Assert.Null(options.DiscardPass);
    }

    [Fact]
    public void TheReleaseVectorIsReadWhateverOrderItArrivesIn()
    {
        string[] vector =
        [
            "--raw", "raw",
            "--release",
            "--thresholds", "thresholds.json",
            "--min-passes", "6",
            "--discard-pass", "1",
        ];

        Assert.True(ReleaseOptions.TryParse(vector, out var options, out var error), error);

        Assert.True(options.Release);
        Assert.Equal("thresholds.json", options.ThresholdsPath);
        Assert.Equal(6, options.MinPasses);
        Assert.Equal(1, options.DiscardPass);
    }

    [Fact]
    public void AValueMayBeAttachedWithAnEqualsSign()
    {
        string[] vector = ["--thresholds=thresholds.json", "--release", "--min-passes=6"];

        Assert.True(ReleaseOptions.TryParse(vector, out var options, out var error), error);

        Assert.Equal("thresholds.json", options.ThresholdsPath);
        Assert.Equal(6, options.MinPasses);
    }

    [Theory]
    [InlineData(new[] { "--thresholds", "thresholds.json" }, "argument '--thresholds' requires '--release'")]
    [InlineData(new[] { "--release" }, "argument '--release' requires '--thresholds <file>'")]
    [InlineData(new[] { "--min-passes", "6" }, "argument '--min-passes' is release-only and requires '--release'")]
    [InlineData(new[] { "--discard-pass", "1" }, "argument '--discard-pass' is release-only and requires '--release'")]
    public void AReleaseFlagWithoutItsCompanionIsRefused(string[] vector, string expected)
    {
        Assert.False(ReleaseOptions.TryParse(vector, out var options, out var error));
        Assert.False(options.Release);
        Assert.NotNull(error);
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData(new[] { "--release", "--thresholds", "thresholds.json", "--min-passes", "1" }, "argument --min-passes: 1 is below the minimum of 2")]
    [InlineData(new[] { "--release", "--thresholds", "thresholds.json", "--discard-pass", "-1" }, "argument --discard-pass: -1 is below the minimum of 0")]
    [InlineData(new[] { "--release", "--thresholds", "thresholds.json", "--min-passes", "six" }, "argument --min-passes: invalid int value: 'six'")]
    [InlineData(new[] { "--release", "--thresholds" }, "missing value for '--thresholds'")]
    [InlineData(new[] { "--release=true", "--thresholds", "thresholds.json" }, "argument '--release' takes no value")]
    public void AReleaseValueIsRefusedTheWayTheCallerCanFixIt(string[] vector, string expected)
    {
        Assert.False(ReleaseOptions.TryParse(vector, out _, out var error));
        Assert.NotNull(error);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void TheProcessRefusesReleaseCombinedWithFlat()
    {
        // AnalysisOptions is internal, so the --release/--flat combination is asserted through the
        // built entry point: the apphost is copied beside the test assembly by the project reference.
        var binary = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "WinForward.E2E.Analysis.exe" : "WinForward.E2E.Analysis");
        Assert.True(File.Exists(binary), $"the analysis binary is not beside the tests: {binary}");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(binary)
        {
            WorkingDirectory = RepoPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("--release");
        process.StartInfo.ArgumentList.Add("--thresholds");
        process.StartInfo.ArgumentList.Add("release-thresholds.example.json");
        process.StartInfo.ArgumentList.Add("--flat");

        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(2, process.ExitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("cannot be combined with '--flat'", stderr, StringComparison.Ordinal);
        Assert.Contains("usage:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseDefaultsAreTheDocumentedOnes()
    {
        Assert.Equal(6, ReleaseOptions.DefaultMinPasses);
        Assert.Equal(0, ReleaseOptions.DefaultDiscardPass);
        Assert.Equal(2, ReleaseOptions.MinimumMinPasses);
    }
}
