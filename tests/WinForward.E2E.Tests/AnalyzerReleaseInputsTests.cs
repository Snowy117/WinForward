using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinForward.E2E.Analysis.Release;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The release input path end to end, through its public seam: the threshold file is parsed, the
/// caliber plan is hashed, the measured pass list is selected, and the provenance document is rendered.
/// The example file points at the real release plan, so the successful cases verify the checked-in
/// caliber rather than a mock of it.
/// </summary>
public sealed class AnalyzerReleaseInputsTests
{
    [Fact]
    public void TheMeasuredPassListDropsTheDiscardedPass()
    {
        var inputs = Build(PassIds(), out _);

        Assert.Equal(7, inputs.Passes.Total);
        Assert.Equal("1", string.Join(",", inputs.Passes.Discarded));
        Assert.Equal("2,3,4,5,6,7", string.Join(",", inputs.Passes.Measured));
        Assert.Equal(6, inputs.Passes.N);
        Assert.Equal(6, inputs.Passes.Required);
        Assert.False(inputs.Passes.Insufficient);
    }

    [Fact]
    public void FewerMeasuredPassesThanRequiredIsRecordedAsInsufficient()
    {
        var inputs = Build(["pass1", "pass2", "pass3", "pass4"], out _);

        Assert.Equal(4, inputs.Passes.Total);
        Assert.Equal(3, inputs.Passes.N);
        Assert.True(inputs.Passes.Insufficient);
        Assert.Contains("\"insufficient\": true", inputs.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiscardPassMustExistInTheCampaignTree()
    {
        var parsed = ReleaseInputs.TryBuild(
            Example(),
            Options(),
            5.0,
            ["pass2", "pass3", "pass4", "pass5", "pass6", "pass7"],
            RepoPaths.Root,
            out var inputs,
            out var error);

        Assert.False(parsed);
        Assert.Null(inputs);
        Assert.NotNull(error);
        Assert.Contains("'discardPass' is 1", error, StringComparison.Ordinal);
        Assert.Contains("not a pass in the campaign tree", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlatTreeIsNotAReleaseCampaign()
    {
        var parsed = ReleaseInputs.TryBuild(Example(), Options(), 5.0, ["flat"], RepoPaths.Root, out _, out var error);

        Assert.False(parsed);
        Assert.NotNull(error);
        Assert.Contains("not a numbered pass directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCliOverridesTheFilesPassCounts()
    {
        var inputs = Build(PassIds(), out var error, Options() with { MinPasses = 2, DiscardPass = 0 });

        Assert.Null(error);
        Assert.Empty(inputs.Passes.Discarded);
        Assert.Equal(7, inputs.Passes.N);
        Assert.Equal(2, inputs.Passes.Required);
        Assert.False(inputs.Passes.Insufficient);
    }

    [Fact]
    public void TheWarmupWindowMustMatchTheCalibration()
    {
        var parsed = ReleaseInputs.TryBuild(Example(), Options(), 3.0, PassIds(), RepoPaths.Root, out _, out var error);

        Assert.False(parsed);
        Assert.NotNull(error);
        Assert.Contains("'warmupSeconds' is 5", error, StringComparison.Ordinal);
        Assert.Contains("trims 3 s", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ACaliberHashMismatchIsRefused()
    {
        var thresholds = ParseFixture("caliber-mismatch.json");
        var parsed = ReleaseInputs.TryBuild(thresholds, Options(), 5.0, PassIds(), RepoPaths.Root, out _, out var error);

        Assert.False(parsed);
        Assert.NotNull(error);
        Assert.Contains("'caliber.sha256' is", error, StringComparison.Ordinal);
        Assert.Contains("hashes to", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingCaliberPlanIsRefused()
    {
        var thresholds = ParseFixture("caliber-missing-plan.json");
        var parsed = ReleaseInputs.TryBuild(thresholds, Options(), 5.0, PassIds(), RepoPaths.Root, out _, out var error);

        Assert.False(parsed);
        Assert.NotNull(error);
        Assert.Contains("'caliber.path' names", error, StringComparison.Ordinal);
        Assert.Contains("does not exist under", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerifiedCaliberHashIsTheOneTheArtifactRecords()
    {
        var inputs = Build(PassIds(), out _);

        Assert.Equal(inputs.Caliber.Sha256, inputs.CaliberSha256);
        Assert.Contains(inputs.CaliberSha256, inputs.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseInputsDocumentHasTheDeclaredShape()
    {
        var inputs = Build(PassIds(), out _);
        var text = inputs.ToJson();

        Assert.EndsWith("\n", text, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal(
            "schema,thresholds,caliber,referenceRow,opponents,primary,warmupSeconds,passes",
            string.Join(",", root.EnumerateObject().Select(member => member.Name)));

        Assert.Equal("release-inputs/1", root.GetProperty("schema").GetString());
        Assert.Equal(inputs.ThresholdsPath, root.GetProperty("thresholds").GetProperty("path").GetString());
        Assert.Equal(inputs.ThresholdsSha256, root.GetProperty("thresholds").GetProperty("sha256").GetString());
        Assert.Equal(10, root.GetProperty("caliber").GetProperty("seconds").EnumerateObject().Count());
        Assert.Equal("wf-aot-nativeudp", root.GetProperty("referenceRow").GetString());
        Assert.Equal(3, root.GetProperty("opponents").GetArrayLength());

        var primary = root.GetProperty("primary");
        Assert.Equal(4, primary.GetArrayLength());
        Assert.Equal(
            "metric,kind,band,business,mdd",
            string.Join(",", primary[0].EnumerateObject().Select(member => member.Name)));
        Assert.Equal("lat.tcp_rtt.p50", primary[0].GetProperty("metric").GetString());
        Assert.Equal(0.18, primary[0].GetProperty("band").GetDouble());

        var passes = root.GetProperty("passes");
        Assert.Equal(7, passes.GetProperty("total").GetInt32());
        Assert.Equal(1, passes.GetProperty("discarded").GetArrayLength());
        Assert.Equal(6, passes.GetProperty("measured").GetArrayLength());
        Assert.Equal(6, passes.GetProperty("n").GetInt32());
        Assert.Equal(6, passes.GetProperty("required").GetInt32());
        Assert.False(passes.GetProperty("insufficient").GetBoolean());
    }

    [Fact]
    public void AReleaseRunReturnsExitCodeTwoForAnUnreadableThresholdsFile()
    {
        var missing = Path.Combine(RepoPaths.Root, "no-such-thresholds.json");
        var outcome = ReleaseRun.Execute(
            new ReleaseOptions { Release = true, ThresholdsPath = missing },
            5.0,
            PassIds(),
            RepoPaths.Root);

        Assert.Equal(ReleaseRun.InputError, outcome.ExitCode);
        Assert.Null(outcome.Inputs);
        Assert.NotNull(outcome.Error);
        Assert.Contains("cannot read", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AReleaseRunBuildsTheRecordFromTheCheckedInExample()
    {
        var outcome = ReleaseRun.Execute(Options(), 5.0, PassIds(), RepoPaths.Root);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.Inputs);
        Assert.Equal(6, outcome.Inputs.Passes.N);
    }

    [Fact]
    public void AReleaseRunWithoutItsFlagsIsARefusedInvocation()
    {
        var options = new ReleaseOptions { Release = true };

        Assert.Throws<ArgumentException>(() => ReleaseRun.Execute(options, 5.0, PassIds(), RepoPaths.Root));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TheWarmupWindowMustBeFinite(double warmupSeconds)
    {
        // "NaN" is a value double.TryParse reads, and a file that does not declare warmupSeconds would
        // otherwise let it reach the document, where it is not JSON.
        var parsed = ReleaseInputs.TryBuild(
            ParseFixture("minimal.json"), Options(), warmupSeconds, PassIds(), RepoPaths.Root, out _, out var error);

        Assert.False(parsed);
        Assert.NotNull(error);
        Assert.Contains("'--warmup-seconds'", error, StringComparison.Ordinal);
        Assert.Contains("not a finite number", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentedPassDefaultsApplyWhenTheFileOmitsThem()
    {
        var parsed = ReleaseInputs.TryBuild(
            ParseFixture("minimal.json"), Options(), 5.0, PassIds(), RepoPaths.Root, out var inputs, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(inputs);
        Assert.Equal(ReleaseOptions.DefaultMinPasses, inputs.Passes.Required);
        Assert.Empty(inputs.Passes.Discarded);
        Assert.Equal(7, inputs.Passes.N);
        Assert.False(inputs.Passes.Insufficient);
    }

    [Fact]
    public void TheCaliberDurationsMustMatchThePlan()
    {
        var parsed = ReleaseInputs.TryBuild(
            ParseFixture("seconds-mismatch.json"), Options(), 5.0, PassIds(), RepoPaths.Root, out _, out var error);

        Assert.False(parsed);
        Assert.NotNull(error);
        Assert.Contains("'caliber.seconds.MIX' is 30", error, StringComparison.Ordinal);
        Assert.Contains("declares 60", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AReleaseRunRefusesANonFiniteWarmup()
    {
        var outcome = ReleaseRun.Execute(
            new ReleaseOptions { Release = true, ThresholdsPath = RepoPaths.ThresholdFixture("minimal.json") },
            double.NaN,
            PassIds(),
            RepoPaths.Root);

        Assert.Equal(ReleaseRun.InputError, outcome.ExitCode);
        Assert.Null(outcome.Inputs);
        Assert.NotNull(outcome.Error);
        Assert.Contains("not a finite number", outcome.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[1,2,3]\n", "root is not a JSON object")]
    [InlineData("{\"arms\": 5}\n", "has no 'arms' array")]
    [InlineData("{\"arms\": [null]}\n", "holds an arm without a name and seconds")]
    public void AMalformedCaliberPlanIsRefusedByName(string planText, string expected)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var thresholdsPath = WriteBadPlanFixture(directory, planText);

            var outcome = ReleaseRun.Execute(
                new ReleaseOptions { Release = true, ThresholdsPath = thresholdsPath },
                5.0,
                ["pass1"],
                directory);

            Assert.Equal(ReleaseRun.InputError, outcome.ExitCode);
            Assert.Null(outcome.Inputs);
            Assert.NotNull(outcome.Error);
            Assert.Contains("bad-plan.json", outcome.Error, StringComparison.Ordinal);
            Assert.Contains(expected, outcome.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheProcessRefusesANonObjectCaliberPlanWithoutWritingAnOutput()
    {
        // A plan whose root is an array must reach the release path's exit 2 and a field-naming refusal,
        // never escape as an InvalidOperationException from JsonElement.TryGetProperty. The campaign tree
        // is a one-row skeleton, enough for the loader and nothing more.
        var directory = CreateTemporaryDirectory();
        try
        {
            var thresholdsPath = WriteBadPlanFixture(directory);
            var raw = Path.Combine(directory, "raw");
            var row = Path.Combine(raw, "pass1", "row");
            Directory.CreateDirectory(row);
            File.WriteAllText(Path.Combine(row, "run.json"), "{}\n");
            var output = Path.Combine(directory, "out");

            var binary = Path.Combine(
                AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "WinForward.E2E.Analysis.exe" : "WinForward.E2E.Analysis");
            Assert.True(File.Exists(binary), $"the analysis binary is not beside the tests: {binary}");

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(binary)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            process.StartInfo.ArgumentList.Add("--raw");
            process.StartInfo.ArgumentList.Add(raw);
            process.StartInfo.ArgumentList.Add("--out");
            process.StartInfo.ArgumentList.Add(output);
            process.StartInfo.ArgumentList.Add("--release");
            process.StartInfo.ArgumentList.Add("--thresholds");
            process.StartInfo.ArgumentList.Add(thresholdsPath);

            Assert.True(process.Start());
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(2, process.ExitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("bad-plan.json", stderr, StringComparison.Ordinal);
            Assert.Contains("root is not a JSON object", stderr, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wf-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string WriteBadPlanFixture(string directory, string planText = "[1,2,3]\n")
    {
        var planPath = Path.Combine(directory, "bad-plan.json");
        File.WriteAllText(planPath, planText);
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(planPath)));

        var root = JsonNode.Parse(File.ReadAllText(RepoPaths.ThresholdFixture("minimal.json")));
        Assert.NotNull(root);
        var caliber = root.AsObject()["caliber"];
        Assert.NotNull(caliber);
        var caliberObject = caliber.AsObject();
        caliberObject["path"] = planPath;
        caliberObject["sha256"] = digest;

        var thresholdsPath = Path.Combine(directory, "thresholds.json");
        File.WriteAllText(
            thresholdsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return thresholdsPath;
    }

    private static string[] PassIds() => ["pass1", "pass2", "pass3", "pass4", "pass5", "pass6", "pass7"];

    private static ReleaseOptions Options() => new() { Release = true, ThresholdsPath = RepoPaths.ReleaseThresholdsExample };

    private static ReleaseInputs Build(
        IReadOnlyList<string> passIds,
        out string? error,
        ReleaseOptions? options = null)
    {
        var parsed = ReleaseInputs.TryBuild(
            Example(),
            options ?? Options(),
            5.0,
            passIds,
            RepoPaths.Root,
            out var inputs,
            out error);

        Assert.True(parsed, error);
        Assert.NotNull(inputs);
        return inputs;
    }

    private static ThresholdsFile Example() => Parse(RepoPaths.ReleaseThresholdsExample);

    private static ThresholdsFile ParseFixture(string name) => Parse(RepoPaths.ThresholdFixture(name));

    private static ThresholdsFile Parse(string path)
    {
        var parsed = ThresholdsFile.TryParse(path, out var file, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(file);
        return file;
    }
}
